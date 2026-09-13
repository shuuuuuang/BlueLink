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
    private val expectedTrustedKeys: suspend (String) -> List<ByteArray>,
    private val peerPlatform: (String) -> com.bluelink.android.domain.PeerPlatform,
    private val onSecurityRequest: (com.bluelink.android.domain.SecurityRequest) -> Unit,
    private val onSecurityFinished: (com.bluelink.android.domain.SecurityRequest) -> Unit,
    private val onFileConflict: suspend (String) -> com.bluelink.android.files.DuplicateChoice,
    private val onReceiveConfirmation: suspend (ManagedSessionState, TransferItem) -> Boolean,
    private val onMessage: (ManagedSessionState, ChatItem) -> Unit,
    private val onTransfer: (ManagedSessionState, TransferItem) -> Unit,
    private val onEnvelope: (ManagedSessionState, ChatEnvelope, Boolean) -> Unit,
    private val onReceipt: (ManagedSessionState, ChatReceipt) -> Unit,
    private val onMessageStatus: (ManagedSessionState, UUID, com.bluelink.android.domain.MessageStatus) -> Unit,
    private val onStateChanged: (ManagedSessionState) -> Unit,
    private val onDiagnostic: (DiagnosticLevel, String, String) -> Unit,
    private val findIdentityCandidate: suspend (String, String?) -> com.bluelink.android.domain.IdentityCandidate? = { _, _ -> null },
    private val applyIdentityAssociation: suspend () -> Unit = {},
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val lock = Any()
    private val entries = mutableMapOf<UUID, Entry>()
    private val peerIndex = mutableMapOf<Pair<String, com.bluelink.android.domain.SessionTransport>, UUID>()
    @Volatile var localDeviceName: () -> String = { "" }
    private val _states = MutableStateFlow<List<ManagedSessionState>>(emptyList())
    val states: StateFlow<List<ManagedSessionState>> = _states.asStateFlow()
    @Volatile var mtpEnabled = false
        set(value) { field = value; eachSession { it.mtpEnabled = value } }

    fun refreshMtp() { if (mtpEnabled) eachSession { it.refreshMtp() } }

    @Volatile var maxReceiveBytes: Long = 500L * 1024 * 1024
        set(value) { field = value; eachSession { it.maxReceiveBytes = value } }
    @Volatile var downloadDestination: String = "downloads://BlueLink"
        set(value) { field = value; eachSession { it.downloadDestination = value } }
    @Volatile var autoAcceptFiles: Boolean = true
        set(value) { field = value; eachSession { it.autoAcceptFiles = value } }
    @Volatile var duplicateFilePolicy: String = "rename"
        set(value) { field = value; eachSession { it.duplicateFilePolicy = value } }
    @Volatile var autoSaveImages: Boolean = true
        set(value) { field = value; eachSession { it.autoSaveImages = value } }
    @Volatile var autoSaveOtherAttachments: Boolean = false
        set(value) { field = value; eachSession { it.autoSaveOtherAttachments = value } }
    @Volatile var largeFilesOnlyWhileCharging: Boolean = false
        set(value) { field = value; eachSession { it.largeFilesOnlyWhileCharging = value } }
    val activeCount: Int get() = synchronized(lock) { entries.size }

    fun add(socket: BluetoothSocket, listenerRole: Boolean, preferredName: String? = null): UUID? {
        val mayReadRemote = context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) ==
            PackageManager.PERMISSION_GRANTED
        val remote = if (mayReadRemote) runCatching { socket.remoteDevice }.getOrNull() else null
        val address = if (mayReadRemote) runCatching { remote?.address }.getOrNull().orEmpty() else ""
        val name = preferredName ?: if (mayReadRemote)
            runCatching { remote?.name ?: address }.getOrDefault("附近设备") else "附近设备"
        return add(com.bluelink.android.transport.BluetoothPeerConnection(socket, name, address, peerPlatform(address), listenerRole))
    }

    fun add(connection: com.bluelink.android.transport.PeerConnection): UUID? {
        val address = connection.address
        val name = connection.name
        lateinit var entry: Entry
        synchronized(lock) {
            if (connection.transport == com.bluelink.android.domain.SessionTransport.USB && entries.values.any { it.transport == connection.transport }) {
                runCatching { connection.close() }; return null
            }
            val sessionId = UUID.randomUUID()
            val session = PeerSession(
                context = context,
                connection = connection,
                listenerRole = connection.listenerRole,
                identityStore = identityStore,
                onPeerIdentified = { peerId ->
                    synchronized(lock) { entry.peerId = peerId }
                    publish()
                },
                sessionId = sessionId,
                transportAddress = address,
                platform = connection.platform,
                preferredPeerName = name,
                localDeviceName = localDeviceName(),
                expectedTrustedKeys = { expectedTrustedKeys(address) },
                findIdentityCandidate = { peerId ->
                    findIdentityCandidate(peerId, connection.identityHint)?.also {
                        if (!canAssociate(it.peerId)) throw TrustHandshakeException(com.bluelink.android.domain.TrustStage.REVOKED)
                    }
                },
                canAssociateIdentity = { canAssociate(it.peerId) },
                applyIdentityAssociation = applyIdentityAssociation,
                onSecurityRequest = { request ->
                    update(entry, ConnectionPhase.TRUST_REQUIRED, "等待用户核对安全代码")
                    onSecurityRequest(request)
                },
                onSecurityFinished = onSecurityFinished,
                onMessage = { onMessage(entry.snapshot(), it) },
                onTransfer = { publishTransfer(entry, it) },
                onReady = { ready(entry, it) },
                onClosed = { closeEntry(entry, it) },
                onDiagnostic = onDiagnostic,
                onEnvelope = { envelope, outgoing -> onEnvelope(entry.snapshot(), envelope, outgoing) },
                onReceipt = { onReceipt(entry.snapshot(), it) },
                onMessageStatus = { id, status -> onMessageStatus(entry.snapshot(), id, status) },
                initialMaxReceiveBytes = maxReceiveBytes,
                initialDownloadDestination = downloadDestination,
                initialAutoAcceptFiles = autoAcceptFiles,
                initialDuplicateFilePolicy = duplicateFilePolicy,
                onFileConflict = onFileConflict,
                initialAutoSaveImages = autoSaveImages,
                initialAutoSaveOtherAttachments = autoSaveOtherAttachments,
                initialLargeFilesOnlyWhileCharging = largeFilesOnlyWhileCharging,
                onReceiveConfirmation = { onReceiveConfirmation(entry.snapshot(), it) },
            )
            entry = Entry(sessionId, name, address, session, connection.transport, connection.platform, connection.identityHint)
            session.mtpEnabled = mtpEnabled
            session.onMtpChanged = { publish(); onStateChanged(entry.snapshot()) }
            entries[sessionId] = entry
        }
        update(entry, ConnectionPhase.SECURE_HANDSHAKE, "正在验证设备身份")
        entry.job = scope.launch(start = kotlinx.coroutines.CoroutineStart.LAZY) {
            try { entry.session.run() }
            finally { closeEntry(entry, "连接已关闭") }
        }
        synchronized(lock) { if (entry.closed) entry.job?.cancel() else entry.job?.start() }
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
            closeEntry(it, reason)
            it.job?.cancel()
            it.session.close()
        }
    }

    fun disconnectAll(reason: String) {
        val values = synchronized(lock) { entries.values.toList() }
        values.forEach { entry ->
            closeEntry(entry, reason)
            entry.job?.cancel()
            runCatching { entry.session.close() }
        }
    }

    fun close() {
        disconnectAll("应用已关闭")
        scope.cancel()
    }

    private fun canAssociate(peerId: String): Boolean = synchronized(lock) {
        entries.values.none { it.peerId.equals(peerId, true) && it.phase == ConnectionPhase.CONNECTED } && !identityStore.isRetired(peerId)
    }

    private fun ready(entry: Entry, peerId: String) {
        var duplicate = false
        synchronized(lock) {
            if (entry.closed) return
            val key = peerId.lowercase(java.util.Locale.ROOT) to entry.transport
            val existing = peerIndex[key]
            if (identityStore.isRetired(peerId) || key.first !in identityStore.trustedEntries()) duplicate = true
            else if (existing != null && existing != entry.id && entries.containsKey(existing)) duplicate = true
            else {
                entry.peerName = entry.session.peerName
                entry.peerId = peerId
                entry.phase = ConnectionPhase.CONNECTED
                entry.detail = "${if (entry.transport == com.bluelink.android.domain.SessionTransport.USB) "USB" else "Bluetooth"} · 端到端加密 · BTX/1.1"
                peerIndex[key] = entry.id
            }
        }
        if (duplicate) {
            closeEntry(entry, "同一设备已有活动会话")
            entry.session.close()
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

    private fun publishTransfer(entry: Entry, value: TransferItem) {
        val report = synchronized(lock) { entry.transfers.record(value) } ?: return
        onTransfer(entry.snapshot(), report)
    }

    private fun closeEntry(entry: Entry, reason: String) {
        val finalState: ManagedSessionState
        val interrupted: List<TransferItem>
        synchronized(lock) {
            if (entry.closed) return
            entry.closed = true
            interrupted = entry.transfers.close()
            entries.remove(entry.id)
            entry.peerId?.let { val key = it.lowercase(java.util.Locale.ROOT) to entry.transport; if (peerIndex[key] == entry.id) peerIndex.remove(key) }
            entry.phase = ConnectionPhase.DISCONNECTED
            entry.detail = reason
            finalState = entry.snapshot()
        }
        interrupted.forEach { onTransfer(finalState, it) }
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

    private class Entry(val id: UUID, var peerName: String, val transportAddress: String, val session: PeerSession,
        val transport: com.bluelink.android.domain.SessionTransport, val platform: com.bluelink.android.domain.PeerPlatform, val identityHint: String?) {
        var peerId: String? = null
        var phase: ConnectionPhase = ConnectionPhase.CONNECTING
        var detail: String = "正在建立会话"
        val startedAt: Long = System.currentTimeMillis()
        var job: Job? = null
        var closed: Boolean = false
        val transfers = SessionTransferLedger()
        fun snapshot() = ManagedSessionState(id, peerId, peerName, transportAddress, phase, detail, startedAt, transport, platform, identityHint, session.hasPeerProvidedName, session.mtpReady)
    }
}
