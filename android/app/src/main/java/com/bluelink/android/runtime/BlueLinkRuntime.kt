package com.bluelink.android.runtime

import android.bluetooth.BluetoothSocket
import android.content.Context
import android.net.Uri
import com.bluelink.android.CryptoStartup
import com.bluelink.android.bluetooth.BluetoothRepository
import com.bluelink.android.data.IdentityStore
import com.bluelink.android.data.local.AppSettings
import com.bluelink.android.data.local.BlueLinkRepository
import com.bluelink.android.diagnostics.CrashReporter
import com.bluelink.android.domain.ChatAttachment
import com.bluelink.android.domain.ChatItem
import com.bluelink.android.domain.ChatItemKind
import com.bluelink.android.domain.ConnectionPhase
import com.bluelink.android.domain.ConnectionState
import com.bluelink.android.domain.ConversationSummary
import com.bluelink.android.domain.DeviceAvailability
import com.bluelink.android.domain.DiagnosticEntry
import com.bluelink.android.domain.DiagnosticLevel
import com.bluelink.android.domain.MessageStatus
import com.bluelink.android.domain.ManagedSessionState
import com.bluelink.android.domain.PeerPlatform
import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TrustPrompt
import com.bluelink.android.session.SessionSupervisor
import com.bluelink.core.ChatEnvelope
import com.bluelink.core.ChatReceipt
import com.bluelink.core.AttachmentRole
import com.bluelink.core.ReceiptState
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

class BlueLinkRuntime(
    private val context: Context,
    private val identityStore: IdentityStore,
    private val cryptoStartup: CryptoStartup,
    private val repository: BlueLinkRepository,
) {
    val identityFingerprint: String by lazy {
        java.security.MessageDigest.getInstance("SHA-256").digest(identityStore.identity.publicKey())
            .take(12).chunked(2).joinToString(":") { bytes -> bytes.joinToString("") { "%02X".format(it) } }
    }
    private val diagnosticSequence = AtomicLong()
    private val _diagnostics = MutableStateFlow<List<DiagnosticEntry>>(emptyList())
    val diagnostics: StateFlow<List<DiagnosticEntry>> = _diagnostics.asStateFlow()
    val bluetooth = BluetoothRepository(
        context,
        identityStore.identity.peerId().joinToString("") { "%02X".format(it) },
        ::recordDiagnostic,
    )
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO +
        CoroutineExceptionHandler { _, failure ->
            CrashReporter.recordNonFatal(context, "RuntimeScope", failure)
            recordDiagnostic(DiagnosticLevel.ERROR, "Runtime", "后台任务失败：${failure.javaClass.simpleName}: ${failure.message ?: "无详情"}")
        })
    private val started = AtomicBoolean()
    private val discoveryStarted = AtomicBoolean()
    private val cryptoOperational = AtomicBoolean()
    private val dialing = ConcurrentHashMap.newKeySet<String>()
    private val trustMutex = Mutex()
    @Volatile
    private var cryptoFailureDetail = cryptoStartup.detail
    @Volatile
    private var trustDecision: CompletableDeferred<Boolean>? = null
    @Volatile private var activeSessionId: UUID? = null
    @Volatile private var activePeerId: String? = null
    @Volatile private var appSettings = AppSettings()
    private val reconnectBackoff = ConcurrentHashMap<String, ReconnectBackoff>()
    @Volatile private var latestAutoConnectSnapshot: AutoConnectSnapshot? = null
    private val transferSamples = ConcurrentHashMap<UUID, TransferSample>()

    private val _connection = MutableStateFlow(ConnectionState())
    val connection: StateFlow<ConnectionState> = _connection.asStateFlow()
    private val _messages = MutableStateFlow<List<ChatItem>>(emptyList())
    val messages: StateFlow<List<ChatItem>> = _messages.asStateFlow()
    private val _transfers = MutableStateFlow<Map<UUID, TransferItem>>(emptyMap())
    val transfers: StateFlow<Map<UUID, TransferItem>> = _transfers.asStateFlow()
    private val _trustPrompt = MutableStateFlow<TrustPrompt?>(null)
    val trustPrompt: StateFlow<TrustPrompt?> = _trustPrompt.asStateFlow()
    private val _conversations = MutableStateFlow<List<ConversationSummary>>(emptyList())
    val conversations: StateFlow<List<ConversationSummary>> = _conversations.asStateFlow()
    private val _settings = MutableStateFlow(AppSettings())
    val settings: StateFlow<AppSettings> = _settings.asStateFlow()
    private val messagesBySession = ConcurrentHashMap<UUID, List<ChatItem>>()
    private val transfersBySession = ConcurrentHashMap<UUID, Map<UUID, TransferItem>>()
    private val sessionSupervisor = SessionSupervisor(
        context = context,
        identityStore = identityStore,
        onTrustRequired = ::requestTrust,
        onMessage = ::handleMessage,
        onTransfer = ::handleTransfer,
        onEnvelope = ::handleEnvelope,
        onReceipt = ::handleReceipt,
        onStateChanged = ::handleSessionState,
        onDiagnostic = ::recordDiagnostic,
    )
    val sessions: StateFlow<List<ManagedSessionState>> = sessionSupervisor.states

    fun start() {
        recordDiagnostic(DiagnosticLevel.INFO, "Runtime", "启动 BlueLink Android 运行时")
        if (!started.compareAndSet(false, true)) return
        CrashReporter.drain(context).forEach { recordDiagnostic(DiagnosticLevel.ERROR, "Crash", it) }
        scope.launch { sessions.collect { updateSelectedConnection(it) } }
        scope.launch {
            repository.initialize(identityStore)
            repository.settings.collect { settings ->
                appSettings = settings
                _settings.value = settings
                if (!settings.diagnosticsEnabled) _diagnostics.value = emptyList()
                sessionSupervisor.maxConcurrentSessions = settings.maxConcurrentConnections
                sessionSupervisor.maxReceiveBytes = if (settings.receiveSizeLimitEnabled)
                    settings.receiveSizeLimitBytes else Long.MAX_VALUE
                sessionSupervisor.downloadDestination = settings.downloadDirectory
                sessionSupervisor.autoAcceptFiles = settings.autoDownloadFiles
                repository.applyRetention(settings.retentionPeriod)
                if (settings.scanOnStartup && discoveryStarted.compareAndSet(false, true))
                    bluetooth.startDiscovery()
            }
        }
        scope.launch {
            combine(repository.peers, repository.conversations, sessions, bluetooth.devices) { peers, stored, active, nearby ->
                val conversationsByPeer = stored.associateBy { it.peerId }
                val sessionsByPeer = active.filter { it.peerId != null && it.phase == ConnectionPhase.CONNECTED }
                    .associateBy { requireNotNull(it.peerId) }
                val canonicalPeers = peers.groupBy { it.peerId.lowercase() }.values.map { matches ->
                    matches.sortedWith(compareByDescending<com.bluelink.android.data.local.PeerEntity> {
                        if (sessionsByPeer.containsKey(it.peerId)) 1 else 0
                    }.thenByDescending { it.lastConnectedAt ?: it.lastSeenAt }).first()
                }
                canonicalPeers.map { peer ->
                    val session = sessionsByPeer[peer.peerId]
                    val visible = nearby.any { device ->
                        device.address.equals(peer.transportAddress, true) ||
                            (device.discoveryId.isNotBlank() && peer.peerId.startsWith(device.discoveryId, true))
                    }
                    ConversationSummary(peer.peerId, peer.displayName.ifBlank { "已信任设备" },
                        runCatching { PeerPlatform.valueOf(peer.platform) }.getOrDefault(PeerPlatform.UNKNOWN),
                        when { session != null -> DeviceAvailability.CONNECTED; visible -> DeviceAvailability.CONNECTABLE; else -> DeviceAvailability.OFFLINE },
                        session?.sessionId, peer.transportAddress, conversationsByPeer[peer.peerId]?.unreadCount ?: 0,
                        conversationsByPeer[peer.peerId]?.lastActivityAt ?: peer.lastSeenAt)
                }.sortedWith(compareBy<ConversationSummary> { when (it.availability) {
                    DeviceAvailability.CONNECTED -> 0; DeviceAvailability.OFFLINE -> 1; DeviceAvailability.CONNECTABLE -> 2
                } }.thenByDescending { it.lastActivityAt })
            }.collect { _conversations.value = it }
        }
        scope.launch {
            combine(repository.peers, repository.settings, bluetooth.devices, sessions) { peers, settings, devices, active ->
                AutoConnectSnapshot(peers.filter { it.trustState == "TRUSTED" }, settings, devices, active)
            }.collect {
                latestAutoConnectSnapshot = it
                attemptAutoConnect(it)
            }
        }
        scope.launch {
            while (isActive) {
                delay(5_000)
                latestAutoConnectSnapshot?.let(::attemptAutoConnect)
            }
        }
        if (!cryptoStartup.ready) {
            recordDiagnostic(DiagnosticLevel.ERROR, "Crypto", "加密运行时不可用：${cryptoStartup.detail}")
            _connection.value = ConnectionState(ConnectionPhase.OFFLINE, detail = "加密运行时不可用，请查看诊断")
        } else {
            recordDiagnostic(DiagnosticLevel.INFO, "Crypto", cryptoStartup.detail)
            try {
                identityStore.identity
                cryptoOperational.set(true)
                recordDiagnostic(DiagnosticLevel.INFO, "Crypto", "设备身份密钥已加载")
            } catch (failure: Throwable) {
                cryptoFailureDetail = "${failure.javaClass.simpleName}: ${failure.message ?: "身份密钥无法读取"}"
                recordDiagnostic(DiagnosticLevel.ERROR, "Crypto", "设备身份密钥加载失败：$cryptoFailureDetail")
                _connection.value = ConnectionState(ConnectionPhase.OFFLINE, detail = "设备身份密钥不可用，请查看诊断")
            }
        }
        startBluetoothEndpoints()
        recordDiagnostic(DiagnosticLevel.INFO, "Bluetooth", "设备发现、Presence 与 GATT 服务启动完成")
    }

    fun discover() {
        recordDiagnostic(DiagnosticLevel.INFO, "Runtime", "用户请求重新扫描")
        bluetooth.startPresence()
        discoveryStarted.set(true)
        bluetooth.startDiscovery()
    }

    fun connect(device: com.bluelink.android.domain.NearbyDevice) = connectInternal(device, automatic = false)

    private fun connectInternal(device: com.bluelink.android.domain.NearbyDevice, automatic: Boolean) {
        if (!cryptoOperational.get()) {
            recordDiagnostic(DiagnosticLevel.ERROR, "Crypto", "无法连接：$cryptoFailureDetail")
            _connection.value = ConnectionState(ConnectionPhase.OFFLINE, detail = "加密运行时不可用，请查看诊断")
            return
        }
        if (!sessionSupervisor.canAccept) {
            recordDiagnostic(DiagnosticLevel.WARNING, "Connection", "已达到最大连接数 ${sessionSupervisor.maxConcurrentSessions}")
            return
        }
        if (!dialing.add(device.address)) {
            recordDiagnostic(DiagnosticLevel.WARNING, "Connection", "该设备已有拨号任务，忽略重复连接请求")
            return
        }
        val name = device.name
        recordDiagnostic(DiagnosticLevel.INFO, "Connection", "${if (automatic) "自动" else "开始"}连接 $name")
        _connection.value = ConnectionState(ConnectionPhase.CONNECTING, name, "正在连接可用的 BLE Rendezvous")
        scope.launch {
            try {
                runCatching {
                    bluetooth.connect(device) { stage ->
                        recordDiagnostic(DiagnosticLevel.INFO, "Connection", stage)
                        _connection.value = ConnectionState(ConnectionPhase.CONNECTING, name, stage)
                    }
                }
                    .onSuccess { connection ->
                        recordDiagnostic(DiagnosticLevel.INFO, "Connection", "RFCOMM 已连接，进入安全会话")
                        startSession(connection.socket, listenerRole = false, connection.peerName)
                    }
                    .onFailure {
                        val detail = it.message ?: it.javaClass.simpleName
                        recordDiagnostic(DiagnosticLevel.ERROR, "Connection",
                            "连接失败：${it.javaClass.simpleName}: $detail")
                        _connection.value = ConnectionState(ConnectionPhase.DISCONNECTED, name, detail)
                        if (automatic) registerReconnectFailure(device.address)
                    }
            } finally {
                dialing.remove(device.address)
            }
        }
    }

    fun sendChat(text: String) {
        val value = text.trim()
        if (value.isEmpty()) return
        val item = ChatItem(text = value, outgoing = true, status = MessageStatus.SENDING)
        val sessionId = activeSessionId ?: return
        messagesBySession.compute(sessionId) { _, items -> (items ?: emptyList()) + item }
        _messages.value = messagesBySession[sessionId].orEmpty()
        sessions.value.firstOrNull { it.sessionId == sessionId }?.let { state ->
            if (appSettings.saveChatHistory) scope.launch { repository.saveChat(state, item) }
        }
        sessionSupervisor.sendChat(sessionId, value, item.id) { sent ->
            val finalStatus = if (sent) MessageStatus.SENT else MessageStatus.LOCAL_QUEUED
            messagesBySession.computeIfPresent(sessionId) { _, items ->
                items.map { if (it.id == item.id) it.copy(status = finalStatus) else it }
            }
            if (activeSessionId == sessionId) _messages.value = messagesBySession[sessionId].orEmpty()
            if (appSettings.saveChatHistory)
                scope.launch { repository.updateMessageStatus(item.id.toString(), finalStatus) }
        }
    }

    fun sendFile(uri: Uri, name: String, size: Long) {
        activeSessionId?.let { sessionSupervisor.sendFile(it, uri, name, size) }
    }

    fun retryTransfer(value: TransferItem) {
        val uri = value.localUri?.let(Uri::parse) ?: return
        if (value.outgoing) resolveTransferSession(value)?.let { sessionSupervisor.retryFile(it, uri, value) }
    }

    fun pauseTransfer(value: TransferItem) {
        resolveTransferSession(value)?.let { sessionSupervisor.pauseTransfer(it, value.id) }
    }

    fun resumeTransfer(value: TransferItem) {
        resolveTransferSession(value)?.let { sessionSupervisor.resumeTransfer(it, value.id) }
    }

    fun cancelTransfer(value: TransferItem) {
        resolveTransferSession(value)?.let { sessionSupervisor.cancelTransfer(it, value.id) }
    }

    private fun resolveTransferSession(value: TransferItem): UUID? =
        sessions.value.firstOrNull { !value.peerId.isNullOrBlank() && it.peerId.equals(value.peerId, true) }?.sessionId
            ?: activeSessionId

    fun confirmTrust(accepted: Boolean) {
        trustDecision?.complete(accepted)
        trustDecision = null
        _trustPrompt.value = null
    }

    fun disconnect() {
        val id = activeSessionId ?: return
        recordDiagnostic(DiagnosticLevel.INFO, "Connection", "用户请求断开当前会话")
        sessionSupervisor.disconnect(id)
        _connection.value = ConnectionState(ConnectionPhase.DISCONNECTED, detail = "已断开")
    }

    fun disconnectPeer(peerId: String) {
        val session = sessions.value.firstOrNull { it.peerId.equals(peerId, true) } ?: return
        recordDiagnostic(DiagnosticLevel.INFO, "Connection", "用户请求断开 ${session.peerName}")
        sessionSupervisor.disconnect(session.sessionId)
        if (activeSessionId == session.sessionId)
            _connection.value = ConnectionState(ConnectionPhase.DISCONNECTED, session.peerName, "已断开")
    }

    fun selectSession(sessionId: UUID) {
        activeSessionId = sessionId
        val state = sessions.value.firstOrNull { it.sessionId == sessionId }
        activePeerId = state?.peerId
        _messages.value = messagesBySession[sessionId].orEmpty()
        _transfers.value = transfersBySession[sessionId].orEmpty()
        state?.let {
            _connection.value = ConnectionState(it.phase, it.peerName, it.detail)
            it.peerId?.let { peerId ->
                scope.launch { repository.markConversationRead(peerId) }
                loadHistory(peerId, sessionId)
            }
        }
    }

    fun selectPeer(peerId: String) {
        activePeerId = peerId
        val connected = sessions.value.firstOrNull { it.peerId.equals(peerId, true) && it.phase == ConnectionPhase.CONNECTED }
        if (connected != null) selectSession(connected.sessionId) else {
            activeSessionId = null
            scope.launch {
                repository.markConversationRead(peerId)
                _messages.value = repository.loadHistory(peerId)
                _transfers.value = repository.loadTransfers(peerId).associateBy { it.id }
                val summary = conversations.value.firstOrNull { it.peerId == peerId }
                _connection.value = ConnectionState(ConnectionPhase.OFFLINE, summary?.peerName, "设备离线 · 可查看历史记录")
            }
        }
    }

    fun clearDiagnostics() {
        _diagnostics.value = emptyList()
        recordDiagnostic(DiagnosticLevel.INFO, "Diagnostics", "诊断日志已清空")
    }

    fun saveSettings(value: AppSettings) {
        _settings.value = value
        appSettings = value
        if (!value.diagnosticsEnabled) _diagnostics.value = emptyList()
        scope.launch { repository.saveSettings(value) }
    }

    fun keepBackgroundSessionsEnabled(): Boolean = appSettings.keepBackgroundSessions

    fun onAppBackgrounded() {
        if (appSettings.keepBackgroundSessions) return
        recordDiagnostic(DiagnosticLevel.INFO, "Runtime", "后台会话已关闭，应用离开前台后停止蓝牙端点")
        sessionSupervisor.disconnectAll("后台会话已关闭")
        bluetooth.suspendBackgroundWork()
        discoveryStarted.set(false)
    }

    fun onAppForegrounded() {
        if (!started.get()) return
        startBluetoothEndpoints()
        if (appSettings.scanOnStartup && discoveryStarted.compareAndSet(false, true)) bluetooth.startDiscovery()
    }

    fun forgetPeer(peerId: String) {
        scope.launch { repository.removeTrust(peerId, identityStore) }
    }

    fun clearChatHistory() {
        messagesBySession.clear(); _messages.value = emptyList()
        scope.launch { repository.clearChatHistory() }
    }

    fun deleteMessage(messageId: UUID) {
        messagesBySession.replaceAll { _, items -> items.filterNot { it.id == messageId } }
        _messages.value = _messages.value.filterNot { it.id == messageId }
        scope.launch { repository.deleteMessage(messageId) }
    }

    fun clearConversation(peerId: String) {
        sessions.value.filter { it.peerId.equals(peerId, true) }.forEach { state ->
            messagesBySession[state.sessionId] = emptyList()
        }
        if (activePeerId.equals(peerId, true)) _messages.value = emptyList()
        scope.launch { repository.clearConversation(peerId) }
    }

    fun clearTransferHistory() {
        transfersBySession.clear(); _transfers.value = emptyMap()
        scope.launch { repository.clearTransferHistory() }
    }

    fun deleteTransfer(transferId: UUID) {
        transfersBySession.replaceAll { _, items -> items - transferId }
        _transfers.value = _transfers.value - transferId
        scope.launch { repository.deleteTransfer(transferId) }
    }

    private fun startSession(socket: BluetoothSocket, listenerRole: Boolean, preferredName: String? = null) {
        val id = sessionSupervisor.add(socket, listenerRole, preferredName)
        if (id == null) {
            recordDiagnostic(DiagnosticLevel.WARNING, "Connection", "已达到最大连接数，拒绝新的 RFCOMM 通道")
            return
        }
        if (activeSessionId == null) selectSession(id)
    }

    private suspend fun requestTrust(sessionId: UUID, peerId: String, publicKey: ByteArray,
                                     safetyCode: String, peerName: String): Boolean = trustMutex.withLock {
        val request = TrustPrompt(peerId = peerId, peerName = peerName, safetyCode = safetyCode)
        _connection.value = ConnectionState(ConnectionPhase.TRUST_REQUIRED, peerName, "请核对两端安全代码")
        _trustPrompt.value = request
        val fingerprint = publicKey.take(4).joinToString("") { "%02x".format(it) }
        recordDiagnostic(DiagnosticLevel.WARNING, "Handshake",
            "会话 ${sessionId.toString().take(8)} 等待信任确认（身份指纹=$fingerprint…）")
        try { CompletableDeferred<Boolean>().also { trustDecision = it }.await() }
        finally { trustDecision = null; if (_trustPrompt.value?.requestId == request.requestId) _trustPrompt.value = null }
    }

    private fun handleMessage(session: ManagedSessionState, message: ChatItem) {
        messagesBySession.compute(session.sessionId) { _, items -> (items ?: emptyList()) + message }
        if (activeSessionId == session.sessionId) _messages.value = messagesBySession[session.sessionId].orEmpty()
        if (appSettings.saveChatHistory)
            scope.launch { repository.saveChat(session, message, unread = activePeerId != session.peerId) }
    }

    private fun handleTransfer(session: ManagedSessionState, transfer: TransferItem) {
        val now = System.currentTimeMillis()
        val previous = transferSamples[transfer.id]
        val elapsed = previous?.let { (now - it.updatedAt).coerceAtLeast(1) } ?: 0
        val instant = if (previous != null && transfer.completedBytes > previous.bytes && elapsed >= 120)
            (transfer.completedBytes - previous.bytes) * 1000.0 / elapsed else 0.0
        val speed = when {
            instant <= 0.0 -> previous?.speed ?: 0.0
            previous == null || previous.speed <= 0.0 -> instant
            else -> previous.speed * .65 + instant * .35
        }
        val startedAt = previous?.startedAt ?: now
        transferSamples[transfer.id] = TransferSample(transfer.completedBytes, now, startedAt, speed)
        val scoped = transfer.copy(peerId = session.peerId, startedAtEpochMs = startedAt,
            updatedAtEpochMs = now, bytesPerSecond = speed)
        if (scoped.role != AttachmentRole.IMAGE_PREVIEW)
            transfersBySession.compute(session.sessionId) { _, items -> (items ?: emptyMap()) + (scoped.id to scoped) }
        scoped.messageId?.let { messageId -> messagesBySession.computeIfPresent(session.sessionId) { _, items ->
            items.map { message -> if (message.id != messageId) message else message.copy(
                attachments = message.attachments.map { attachment ->
                    if (scoped.role == AttachmentRole.IMAGE_PREVIEW && attachment.isImage) attachment.copy(
                        previewUri = scoped.localUri ?: attachment.previewUri)
                    else if (attachment.transferId != scoped.id) attachment else attachment.copy(
                        localUri = scoped.localUri ?: attachment.localUri, state = scoped.status.name,
                        completedBytes = scoped.completedBytes, bytesPerSecond = scoped.bytesPerSecond)
                })
            }
        } }
        if (activeSessionId == session.sessionId) _messages.value = messagesBySession[session.sessionId].orEmpty()
        if (activeSessionId == session.sessionId) _transfers.value = transfersBySession[session.sessionId].orEmpty()
        if (appSettings.saveTransferHistory) scope.launch { repository.saveTransfer(session, scoped) }
    }

    private fun handleEnvelope(session: ManagedSessionState, envelope: ChatEnvelope, outgoing: Boolean) {
        recordDiagnostic(DiagnosticLevel.INFO, "Message",
            "收到结构化消息（session=${session.sessionId.toString().take(8)}，type=${envelope.kind()}，attachments=${envelope.attachments().size}）")
        if (appSettings.saveChatHistory && envelope.attachments().isNotEmpty())
            scope.launch { repository.saveEnvelope(session, envelope, outgoing, unread = !outgoing && activePeerId != session.peerId) }
        if (envelope.attachments().isNotEmpty()) {
            val descriptors = if (envelope.kind() == com.bluelink.core.ChatPayloadKind.IMAGE)
                envelope.attachments().filter { it.role() == AttachmentRole.IMAGE_ORIGINAL }
            else envelope.attachments().filter { it.role() == AttachmentRole.FILE }
            val attachments = descriptors.map { value -> ChatAttachment(value.attachmentId(), value.transferId(),
                value.fileName(), value.mimeType(), value.size()) }
            val item = ChatItem(envelope.messageId(), envelope.body(), outgoing,
                java.time.Instant.ofEpochMilli(envelope.createdAt()),
                if (outgoing) MessageStatus.SENT else MessageStatus.RECEIVED,
                if (envelope.kind() == com.bluelink.core.ChatPayloadKind.IMAGE) ChatItemKind.IMAGE else ChatItemKind.FILE,
                attachments)
            messagesBySession.compute(session.sessionId) { _, items ->
                (items ?: emptyList()).filterNot { it.id == item.id } + item
            }
            if (activeSessionId == session.sessionId) _messages.value = messagesBySession[session.sessionId].orEmpty()
        }
    }

    private fun handleReceipt(session: ManagedSessionState, receipt: ChatReceipt) {
        messagesBySession.computeIfPresent(session.sessionId) { _, items -> items.map { item ->
            if (item.id != receipt.messageId()) item else item.copy(status = when (receipt.state()) {
                ReceiptState.FAILED -> MessageStatus.FAILED
                ReceiptState.READ -> MessageStatus.READ
                ReceiptState.DELIVERED -> MessageStatus.DELIVERED
                null -> MessageStatus.FAILED
            })
        } }
        if (activeSessionId == session.sessionId) _messages.value = messagesBySession[session.sessionId].orEmpty()
        if (appSettings.saveChatHistory) scope.launch { repository.updateMessageStatus(receipt.messageId().toString(), when (receipt.state()) {
            ReceiptState.FAILED -> MessageStatus.FAILED
            ReceiptState.READ -> MessageStatus.READ
            ReceiptState.DELIVERED -> MessageStatus.DELIVERED
            null -> MessageStatus.FAILED
        }) }
    }

    private fun handleSessionState(state: ManagedSessionState) {
        when (state.phase) {
            ConnectionPhase.CONNECTED -> {
                if (activeSessionId == state.sessionId) activePeerId = state.peerId
                reconnectBackoff.remove(state.transportAddress.uppercase())
                scope.launch {
                    repository.recordConnectedSession(state, identityStore)
                    state.peerId?.let {
                        loadHistory(it, state.sessionId)
                        flushQueuedMessages(state, it)
                    }
                }
            }
            ConnectionPhase.DISCONNECTED -> scope.launch { repository.recordDisconnectedSession(state) }
            else -> Unit
        }
    }

    private fun loadHistory(peerId: String, sessionId: UUID) {
        scope.launch {
            val persisted = repository.loadHistory(peerId)
            val persistedTransfers = repository.loadTransfers(peerId)
            val transient = messagesBySession[sessionId].orEmpty()
            val merged = (persisted + transient).associateBy { it.id }.values.sortedBy { it.timestamp }
            messagesBySession[sessionId] = merged
            val currentTransfers = transfersBySession[sessionId].orEmpty()
            transfersBySession[sessionId] = (persistedTransfers.associateBy { it.id } + currentTransfers)
            if (activeSessionId == sessionId) _messages.value = merged
            if (activeSessionId == sessionId) _transfers.value = transfersBySession[sessionId].orEmpty()
        }
    }

    private suspend fun flushQueuedMessages(state: ManagedSessionState, peerId: String) {
        val persisted = repository.loadHistory(peerId)
        val transient = messagesBySession[state.sessionId].orEmpty()
        val merged = (persisted + transient).associateBy { it.id }.values.sortedBy { it.timestamp }
        messagesBySession[state.sessionId] = merged
        for (queued in merged.filter { it.outgoing && it.status == MessageStatus.LOCAL_QUEUED }) {
            val sent = CompletableDeferred<Boolean>()
            sessionSupervisor.sendChat(state.sessionId, queued.text, queued.id) { sent.complete(it) }
            if (!sent.await()) break
            messagesBySession.computeIfPresent(state.sessionId) { _, items ->
                items.map { if (it.id == queued.id) it.copy(status = MessageStatus.SENT) else it }
            }
            repository.updateMessageStatus(queued.id.toString(), MessageStatus.SENT)
            if (activeSessionId == state.sessionId)
                _messages.value = messagesBySession[state.sessionId].orEmpty()
        }
    }

    private fun attemptAutoConnect(snapshot: AutoConnectSnapshot) {
        if (!snapshot.settings.autoConnectTrustedDevices || !cryptoOperational.get()) return
        val activeAddresses = snapshot.sessions.filter { it.phase != ConnectionPhase.DISCONNECTED }
            .map { it.transportAddress.uppercase() }.toSet()
        val now = System.currentTimeMillis()
        snapshot.peers.forEach { peer ->
            val address = peer.transportAddress.takeIf { it.isNotBlank() } ?: return@forEach
            if (address.uppercase() in activeAddresses || reconnectBackoff[address.uppercase()]?.nextAttemptAt?.let { it > now } == true) return@forEach
            val device = snapshot.devices.firstOrNull {
                (it.address.equals(address, true) ||
                    (it.discoveryId.isNotBlank() && peer.peerId.startsWith(it.discoveryId, true))) &&
                    it.rendezvousAvailable && it.connectable
            }
                ?: return@forEach
            val failures = reconnectBackoff[address.uppercase()]?.failures ?: 0
            reconnectBackoff[address.uppercase()] = ReconnectBackoff(failures, now + 10_000)
            connectInternal(device, automatic = true)
        }
    }

    private fun registerReconnectFailure(address: String) {
        reconnectBackoff.compute(address.uppercase()) { _, current ->
            val failures = ((current?.failures ?: 0) + 1).coerceAtMost(6)
            ReconnectBackoff(failures, System.currentTimeMillis() + (5_000L shl failures).coerceAtMost(5 * 60_000L))
        }
    }

    private fun updateSelectedConnection(states: List<ManagedSessionState>) {
        val selected = states.firstOrNull { it.sessionId == activeSessionId }
            ?: states.firstOrNull { it.phase == ConnectionPhase.CONNECTED }
            ?: states.firstOrNull()
        if (selected != null) {
            if (activeSessionId == null || states.none { it.sessionId == activeSessionId }) activeSessionId = selected.sessionId
            activePeerId = selected.peerId ?: activePeerId
            _connection.value = ConnectionState(selected.phase, selected.peerName, selected.detail)
            _messages.value = messagesBySession[selected.sessionId].orEmpty()
            _transfers.value = transfersBySession[selected.sessionId].orEmpty()
        } else if (activeSessionId != null) {
            activeSessionId = null
            _connection.value = ConnectionState(ConnectionPhase.DISCONNECTED, detail = "所有会话均已断开")
            _messages.value = emptyList()
            _transfers.value = emptyMap()
        }
    }

    private fun recordDiagnostic(level: DiagnosticLevel, component: String, message: String) {
        if (!appSettings.diagnosticsEnabled && component != "Diagnostics") return
        val entry = DiagnosticEntry(
            sequence = diagnosticSequence.incrementAndGet(),
            level = level,
            component = component,
            message = message.replace('\n', ' ').replace('\r', ' '),
        )
        _diagnostics.update { entries -> (entries + entry).takeLast(MAX_DIAGNOSTIC_ENTRIES) }
    }

    private fun startBluetoothEndpoints() {
        bluetooth.startPresence { connection ->
            if (cryptoOperational.get()) {
                recordDiagnostic(DiagnosticLevel.INFO, "Connection", "正在响应 Windows 发起的连接请求")
                startSession(connection.socket, listenerRole = false, preferredName = connection.peerName)
            } else {
                recordDiagnostic(DiagnosticLevel.ERROR, "Crypto", "拒绝安全会话：$cryptoFailureDetail")
                connection.socket.close()
            }
        }
        bluetooth.startServer { accepted ->
            if (cryptoOperational.get()) startSession(accepted, listenerRole = true)
            else {
                recordDiagnostic(DiagnosticLevel.ERROR, "Crypto", "拒绝传入会话：$cryptoFailureDetail")
                accepted.close()
            }
        }
    }

    companion object { private const val MAX_DIAGNOSTIC_ENTRIES = 300 }

    private data class ReconnectBackoff(val failures: Int, val nextAttemptAt: Long)
    private data class TransferSample(val bytes: Long, val updatedAt: Long, val startedAt: Long, val speed: Double)
    private data class AutoConnectSnapshot(
        val peers: List<com.bluelink.android.data.local.PeerEntity>,
        val settings: AppSettings,
        val devices: List<com.bluelink.android.domain.NearbyDevice>,
        val sessions: List<ManagedSessionState>,
    )
}
