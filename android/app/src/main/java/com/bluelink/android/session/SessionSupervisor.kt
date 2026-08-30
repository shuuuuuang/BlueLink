package com.bluelink.android.session

import android.Manifest
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import com.bluelink.android.data.IdentityStore
import com.bluelink.android.domain.ChatItem
import com.bluelink.android.domain.ConnectionPhase
import com.bluelink.android.domain.DiagnosticLevel
import com.bluelink.android.domain.ManagedSessionState
import com.bluelink.android.domain.TransferItem
import com.bluelink.core.ChatEnvelope
import com.bluelink.core.ChatReceipt
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.util.UUID

internal class SessionSupervisor(
    private val context: Context,
    private val identityStore: IdentityStore,
    private val onTrustRequired: suspend (sessionId: UUID, peerId: String, publicKey: ByteArray,
                                          safetyCode: String, peerName: String) -> Boolean,
    private val onMessage: (ManagedSessionState, ChatItem) -> Unit,
    private val onTransfer: (ManagedSessionState, TransferItem) -> Unit,
    private val onEnvelope: (ManagedSessionState, ChatEnvelope, Boolean) -> Unit,
    private val onReceipt: (ManagedSessionState, ChatReceipt) -> Unit,
    private val onStateChanged: (ManagedSessionState) -> Unit,
    private val onDiagnostic: (DiagnosticLevel, String, String) -> Unit,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val lock = Any()
    private val entries = mutableMapOf<UUID, Entry>()
    private val peerIndex = mutableMapOf<String, UUID>()
    private val _states = MutableStateFlow<List<ManagedSessionState>>(emptyList())
    val states: StateFlow<List<ManagedSessionState>> = _states.asStateFlow()

    @Volatile var maxConcurrentSessions: Int = 4
    @Volatile var maxReceiveBytes: Long = 500L * 1024 * 1024
        set(value) { field = value; eachSession { it.maxReceiveBytes = value } }
    @Volatile var downloadDestination: String = "downloads://BlueLink"
        set(value) { field = value; eachSession { it.downloadDestination = value } }
    @Volatile var autoAcceptFiles: Boolean = true
        set(value) { field = value; eachSession { it.autoAcceptFiles = value } }
    val activeCount: Int get() = synchronized(lock) { entries.size }
    val canAccept: Boolean get() = activeCount < maxConcurrentSessions.coerceIn(1, 8)

    fun add(socket: BluetoothSocket, listenerRole: Boolean, preferredName: String? = null): UUID? {
        val mayReadRemote = context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) ==
            PackageManager.PERMISSION_GRANTED
        val remote = if (mayReadRemote) runCatching { socket.remoteDevice }.getOrNull() else null
        val address = if (mayReadRemote) runCatching { remote?.address }.getOrNull().orEmpty() else ""
        val name = preferredName ?: if (mayReadRemote)
            runCatching { remote?.name ?: address }.getOrDefault("附近设备") else "附近设备"
        lateinit var entry: Entry
        synchronized(lock) {
            if (entries.size >= maxConcurrentSessions.coerceIn(1, 8)) {
                runCatching { socket.close() }
                return null
            }
            val sessionId = UUID.randomUUID()
            val session = PeerSession(
                context = context,
                socket = socket,
                listenerRole = listenerRole,
                identityStore = identityStore,
                onTrustRequired = { peerId, publicKey, safetyCode ->
                    update(entry, ConnectionPhase.TRUST_REQUIRED, "等待用户核对安全代码")
                    onTrustRequired(sessionId, peerId, publicKey, safetyCode, name)
                },
                onMessage = { onMessage(entry.snapshot(), it) },
                onTransfer = { onTransfer(entry.snapshot(), it) },
                onReady = { ready(entry, it) },
                onClosed = { closeEntry(entry, it) },
                onDiagnostic = onDiagnostic,
                onEnvelope = { envelope, outgoing -> onEnvelope(entry.snapshot(), envelope, outgoing) },
                onReceipt = { onReceipt(entry.snapshot(), it) },
                initialMaxReceiveBytes = maxReceiveBytes,
                initialDownloadDestination = downloadDestination,
                initialAutoAcceptFiles = autoAcceptFiles,
            )
            entry = Entry(sessionId, name, address, session)
            entries[sessionId] = entry
        }
        update(entry, ConnectionPhase.SECURE_HANDSHAKE, "正在验证设备身份")
        entry.job = scope.launch {
            try { entry.session.run() }
            finally { closeEntry(entry, "连接已关闭") }
        }
        return entry.id
    }

    fun sendChat(sessionId: UUID, text: String, messageId: UUID, onSent: (Boolean) -> Unit) {
        find(sessionId)?.session?.sendChat(text, messageId, onSent) ?: onSent(false)
    }

    fun sendFile(sessionId: UUID, uri: Uri, name: String, size: Long) {
        find(sessionId)?.session?.sendFile(uri, name, size)
    }

    fun retryFile(sessionId: UUID, uri: Uri, template: TransferItem) {
        find(sessionId)?.session?.retryFile(uri, template)
    }

    fun cancelTransfer(sessionId: UUID, transferId: UUID, reason: String = "用户取消") {
        find(sessionId)?.session?.cancelTransfer(transferId, reason)
    }

    fun pauseTransfer(sessionId: UUID, transferId: UUID) {
        find(sessionId)?.session?.pauseTransfer(transferId)
    }

    fun resumeTransfer(sessionId: UUID, transferId: UUID) {
        find(sessionId)?.session?.resumeTransfer(transferId)
    }

    fun disconnect(sessionId: UUID, reason: String = "已断开") {
        find(sessionId)?.let {
            it.session.close()
            closeEntry(it, reason)
        }
    }

    fun disconnectAll(reason: String) {
        val values = synchronized(lock) { entries.values.toList() }
        values.forEach { entry ->
            runCatching { entry.session.close() }
            closeEntry(entry, reason)
        }
    }

    fun close() {
        disconnectAll("应用已关闭")
        scope.cancel()
    }

    private fun ready(entry: Entry, peerId: String) {
        var duplicate = false
        synchronized(lock) {
            if (entry.closed) return
            val existing = peerIndex[peerId]
            if (existing != null && existing != entry.id && entries.containsKey(existing)) duplicate = true
            else {
                entry.peerId = peerId
                entry.phase = ConnectionPhase.CONNECTED
                entry.detail = "端到端加密 · BTX/1.1"
                peerIndex[peerId] = entry.id
            }
        }
        if (duplicate) {
            entry.session.close()
            closeEntry(entry, "同一设备已有活动会话")
        } else {
            val state = entry.snapshot()
            onStateChanged(state)
            publish()
        }
    }

    private fun update(entry: Entry, phase: ConnectionPhase, detail: String) {
        synchronized(lock) {
            if (entry.closed) return
            entry.phase = phase
            entry.detail = detail
        }
        onStateChanged(entry.snapshot())
        publish()
    }

    private fun closeEntry(entry: Entry, reason: String) {
        val finalState: ManagedSessionState
        synchronized(lock) {
            if (entry.closed) return
            entry.closed = true
            entries.remove(entry.id)
            entry.peerId?.let { if (peerIndex[it] == entry.id) peerIndex.remove(it) }
            entry.phase = ConnectionPhase.DISCONNECTED
            entry.detail = reason
            finalState = entry.snapshot()
        }
        _states.value = synchronized(lock) { entries.values.map { it.snapshot() } } + finalState
        onStateChanged(finalState)
        publish()
    }

    private fun publish() {
        _states.value = synchronized(lock) {
            entries.values.sortedBy { it.startedAt }.map { it.snapshot() }
        }
    }

    private fun find(id: UUID): Entry? = synchronized(lock) { entries[id] }

    private fun eachSession(action: (PeerSession) -> Unit) {
        val sessions = synchronized(lock) { entries.values.map { it.session } }
        sessions.forEach(action)
    }

    private class Entry(val id: UUID, val peerName: String, val transportAddress: String, val session: PeerSession) {
        var peerId: String? = null
        var phase: ConnectionPhase = ConnectionPhase.CONNECTING
        var detail: String = "正在建立会话"
        val startedAt: Long = System.currentTimeMillis()
        var job: Job? = null
        var closed: Boolean = false
        fun snapshot() = ManagedSessionState(id, peerId, peerName, transportAddress, phase, detail, startedAt)
    }
}
