package com.bluelink.android.runtime

import android.bluetooth.BluetoothSocket
import com.bluelink.android.data.local.findIdentityCandidate
import com.bluelink.android.data.local.applyIdentityAssociations
import android.content.Context
import android.net.Uri
import com.bluelink.android.CryptoStartup
import com.bluelink.android.bluetooth.BluetoothRepository
import com.bluelink.android.bluetooth.readBluetoothAccess
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
import com.bluelink.android.domain.DeviceProjectionPolicy
import com.bluelink.android.domain.DiagnosticEntry
import com.bluelink.android.domain.DiagnosticLevel
import com.bluelink.android.domain.MessageDeliveryPolicy
import com.bluelink.android.domain.MessageStatus
import com.bluelink.android.domain.ManagedSessionState
import com.bluelink.android.domain.NearbyDevice
import com.bluelink.android.domain.PeerPlatform
import com.bluelink.android.domain.TransferStatus
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
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.Job
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
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
    private fun currentFingerprint() = java.security.MessageDigest.getInstance("SHA-256")
        .digest(identityStore.identity.publicKey()).take(12).chunked(2)
        .joinToString(":") { bytes -> bytes.joinToString("") { "%02X".format(it) } }
    private val _identityFingerprint = MutableStateFlow(currentFingerprint())
    val identityFingerprint = _identityFingerprint.asStateFlow()
    private val sessionAdmission = com.bluelink.android.domain.SessionAdmissionGate()
    private val privacyMutex = Mutex()
    private val incomingConfirmation = com.bluelink.android.domain.TrustConfirmationGate()
    private val incomingConfirmationMutex = Mutex()
    private val _incomingFileRequest = MutableStateFlow<com.bluelink.android.domain.IncomingFileRequest?>(null)
    val incomingFileRequest = _incomingFileRequest.asStateFlow()
    fun confirmIncomingFile(requestId: UUID, accepted: Boolean) { incomingConfirmation.resolve(requestId, accepted) }
    private suspend fun requestIncomingFile(session: ManagedSessionState, item: TransferItem): Boolean = incomingConfirmationMutex.withLock {
        val request = com.bluelink.android.domain.IncomingFileRequest(sessionId = session.sessionId, peerName = session.peerName, transfer = item)
        val result = incomingConfirmation.open(request.requestId)
        _incomingFileRequest.value = request
        try { result.await() } finally {
            incomingConfirmation.close(request.requestId)
            if (_incomingFileRequest.value?.requestId == request.requestId) _incomingFileRequest.value = null
        }
    }
    private val fileConflictMutex = Mutex()
    private val fileConflictDecisions = ConcurrentHashMap<UUID, CompletableDeferred<com.bluelink.android.files.DuplicateChoice>>()
    private val _fileConflictRequest = MutableStateFlow<com.bluelink.android.domain.FileConflictRequest?>(null)
    val fileConflictRequest = _fileConflictRequest.asStateFlow()
    fun resolveFileConflict(id: UUID, choice: com.bluelink.android.files.DuplicateChoice) { fileConflictDecisions[id]?.complete(choice) }
    private suspend fun requestFileConflict(name: String) = fileConflictMutex.withLock {
        val request = com.bluelink.android.domain.FileConflictRequest(fileName = name)
        val result = CompletableDeferred<com.bluelink.android.files.DuplicateChoice>()
        fileConflictDecisions[request.requestId] = result
        _fileConflictRequest.value = request
        try { kotlinx.coroutines.withTimeoutOrNull(60_000L) { result.await() } ?: com.bluelink.android.files.DuplicateChoice.CANCEL }
        finally { fileConflictDecisions.remove(request.requestId); if (_fileConflictRequest.value?.requestId == request.requestId) _fileConflictRequest.value = null }
    }
    private val eventNotifications = com.bluelink.android.service.BlueLinkNotifications(context)
    private val notifiedPhases = ConcurrentHashMap<UUID, ConnectionPhase>()
    private val diagnosticSequence = AtomicLong()
    private val _diagnostics = MutableStateFlow<List<DiagnosticEntry>>(emptyList())
    val diagnostics: StateFlow<List<DiagnosticEntry>> = _diagnostics.asStateFlow()
    val bluetooth = BluetoothRepository(
        context,
        identityStore.identity.peerId().joinToString("") { "%02X".format(it) },
        ::recordDiagnostic,
        hasActiveConnection = { address -> sessions.value.any {
            it.transport == com.bluelink.android.domain.SessionTransport.BLUETOOTH &&
                it.phase != ConnectionPhase.DISCONNECTED && it.transportAddress.equals(address, true)
        } },
    )
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO +
        CoroutineExceptionHandler { _, failure ->
            CrashReporter.recordNonFatal(context, "RuntimeScope", failure)
            recordDiagnostic(DiagnosticLevel.ERROR, "Runtime", "后台任务失败：${failure.javaClass.simpleName}: ${failure.message ?: "无详情"}")
        })
    private val peerPreferences by lazy { com.bluelink.android.data.local.PeerPreferences(java.io.File(context.filesDir,"peer-preferences.json")) }
    fun savePeerPreference(peer: String,note: String?=null,pinned: Boolean?=null) { scope.launch {
        try { peerPreferences.update(peer,note,pinned) } catch (_: Exception) { _operationFailed.value=true }
    } }
    private val draftLedger = com.bluelink.android.domain.DraftLedger()
    private val draftSaveMutex = Mutex()
    val composer by lazy { com.bluelink.android.composer.ComposerController(context) { peer, part -> sendComposerPart(peer, part) } }
    private val draftScheduleLock = Any()
    private var draftDelay: Job? = null
    private val sendingDrafts = mutableSetOf<com.bluelink.android.domain.DraftSnapshot>()
    private val _drafts = MutableStateFlow<Map<String, String>?>(null)
    val drafts = _drafts.asStateFlow()
    private val _draftSaveFailed = MutableStateFlow(false)
    val draftSaveFailed = _draftSaveFailed.asStateFlow()

    fun editDraft(peerId: String, text: String) {
        synchronized(draftLedger) {
            if (_drafts.value == null) return
            draftLedger.edit(peerId, text)
            _drafts.value = draftLedger.texts()
        }
        synchronized(draftScheduleLock) {
            draftDelay?.cancel()
            draftDelay = scope.launch {
                delay(250)
                kotlinx.coroutines.withContext(kotlinx.coroutines.NonCancellable) { flushDrafts() }
            }
        }
    }
    fun requestDraftFlush() { scope.launch { flushDrafts() } }
    private suspend fun flushDrafts(): Boolean = draftSaveMutex.withLock { persistDrafts() }
    private suspend fun persistDrafts(): Boolean {
        return try {
            messagePersistence.run {
                draftLedger.pending().forEach { draft ->
                    check(repository.saveDraft(draft.peerId, draft.text)) { "Draft conversation unavailable" }
                    draftLedger.acknowledge(draft)
                }
            }
            _draftSaveFailed.value = false
            true
        } catch (canceled: CancellationException) { throw canceled }
        catch (_: Exception) { _draftSaveFailed.value = true; false }
    }
    private suspend fun clearDrafts(peerId: String? = null) = draftSaveMutex.withLock {
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Main.immediate) { composer.clear(peerId) }
        synchronized(draftLedger) { draftLedger.clear(peerId); _drafts.value = draftLedger.texts() }
        check(persistDrafts()) { "Draft could not be cleared from storage" }
    }
    private suspend fun loadDrafts() {
        val stored = repository.loadDrafts()
        synchronized(draftLedger) { draftLedger.load(stored); _drafts.value = draftLedger.texts() }
    }

    @Volatile private var appForeground = false
    private val started = AtomicBoolean()
    private val startupDiscovery = com.bluelink.android.bluetooth.StartupDiscovery()
    @Volatile private var settingsLoaded = false
    private val cryptoOperational = AtomicBoolean()
    private val dialing = ConcurrentHashMap.newKeySet<String>()
    private val trustMutex = Mutex()
    @Volatile
    private var cryptoFailureDetail = cryptoStartup.detail
    private val trustConfirmation = com.bluelink.android.domain.TrustConfirmationGate()
    @Volatile private var activeSessionId: UUID? = null
    @Volatile private var activePeerId: String? = null
    @Volatile private var appSettings = AppSettings()
    private val reconnectBackoff = ConcurrentHashMap<String, ReconnectBackoff>()
    private val connectedThisRun = ConcurrentHashMap.newKeySet<String>()
    private val manuallyDisconnected = ConcurrentHashMap.newKeySet<String>()
    @Volatile private var latestAutoConnectSnapshot: AutoConnectSnapshot? = null
    private val transferSamples = ConcurrentHashMap<UUID, TransferSample>()

    private val _connection = MutableStateFlow(ConnectionState())
    val connection: StateFlow<ConnectionState> = _connection.asStateFlow()
    private val _messages = MutableStateFlow<List<ChatItem>>(emptyList())
    val messages: StateFlow<List<ChatItem>> = _messages.asStateFlow()
    private val _historyRevision = MutableStateFlow(0L)
    val historyRevision = _historyRevision.asStateFlow()
    private val historySelectionVersion = java.util.concurrent.atomic.AtomicLong(0)

    private suspend fun loadProjectedHistory(peerId: String, before: UUID? = null, around: UUID? = null,
                                             all: Boolean = false): List<ChatItem> {
        val messages = repository.loadHistory(peerId, before, around, all)
        val transfers = recovery.mergeHistory(emptyList()).associateBy { it.id } + transferIndex.value.items
        return messages.map { message -> message.copy(attachments = message.attachments.map { attachment ->
            val task = transfers[attachment.transferId]?.takeIf { it.peerId.equals(peerId, true) }
            when {
                task != null -> attachment.withTransfer(task)
                attachment.isTransferActive && liveTransferOwners[attachment.transferId] == null -> attachment.copy(state = TransferStatus.FAILED.name)
                else -> attachment
            }
        }) }
    }

    suspend fun loadSearchHistory(peerId: String): List<ChatItem> = kotlinx.coroutines.withContext(Dispatchers.IO) {
        val stored = loadProjectedHistory(peerId, all = true)
        val current = if(activePeerId.equals(peerId,true)) _messages.value else emptyList()
        (stored + current).associateBy { it.id }.values.toList()
    }

    suspend fun loadHistoryContext(peerId: String, id: UUID): Boolean = kotlinx.coroutines.withContext(Dispatchers.IO) {
        val version = historySelectionVersion.get()
        val page = loadProjectedHistory(peerId, around = id)
        if(version != historySelectionVersion.get() || !activePeerId.equals(peerId,true)) return@withContext false
        if(page.none { it.id == id }) return@withContext _messages.value.any { it.id == id }
        mergeHistoryPage(page, peerId, version)
        version == historySelectionVersion.get() && activePeerId.equals(peerId,true)
    }

    suspend fun loadEarlierHistory(peerId: String): Boolean = kotlinx.coroutines.withContext(Dispatchers.IO) {
        val version = historySelectionVersion.get()
        val first = _messages.value.firstOrNull() ?: return@withContext false
        val page = loadProjectedHistory(peerId, before = first.id)
        if(version != historySelectionVersion.get() || !activePeerId.equals(peerId,true)) return@withContext false
        mergeHistoryPage(page, peerId, version)
        page.isNotEmpty() && version == historySelectionVersion.get() && activePeerId.equals(peerId,true)
    }

    private fun mergeHistoryPage(page: List<ChatItem>, peerId: String, version: Long) {
        activeSessionId?.let { session -> messagesBySession.compute(contentId(session)) { _, current ->
            (page + current.orEmpty()).associateBy { it.id }.values.sortedWith(compareBy<ChatItem> { it.timestamp }.thenBy { it.id.toString() })
        } }
        _messages.update { current ->
            if(version != historySelectionVersion.get() || !activePeerId.equals(peerId,true)) current
            else (page + current).associateBy { it.id }.values.sortedWith(compareBy<ChatItem> { it.timestamp }.thenBy { it.id.toString() })
        }
    }
    private val _transfers = MutableStateFlow<Map<UUID, TransferItem>>(emptyMap())
    val transfers: StateFlow<Map<UUID, TransferItem>> = _transfers.asStateFlow()
    private val transferIndex = MutableStateFlow(com.bluelink.android.domain.TransferHistoryIndex())
    private val transferHistoryMutex = Mutex()
    val allTransfers: StateFlow<Map<UUID, TransferItem>> = transferIndex.map { it.items }
        .stateIn(scope, SharingStarted.Eagerly, emptyMap())
    private fun selectedTransfers(): Map<UUID, TransferItem> = transferIndex.value.items
        .filterValues { activePeerId != null && it.peerId.equals(activePeerId, true) }

    private val _trustPrompt = MutableStateFlow<TrustPrompt?>(null)
    val trustPrompt: StateFlow<TrustPrompt?> = _trustPrompt.asStateFlow()
    private val securityLock = Any()
    private val securityRequests = linkedMapOf<UUID, com.bluelink.android.domain.SecurityRequest>()
    private val _securityRequest = MutableStateFlow<com.bluelink.android.domain.SecurityRequest?>(null)
    val securityRequest = _securityRequest.asStateFlow()
    private val _conversations = MutableStateFlow<List<ConversationSummary>>(emptyList())
    val conversations: StateFlow<List<ConversationSummary>> = _conversations.asStateFlow()
    private val _devices = MutableStateFlow<List<NearbyDevice>>(emptyList())
    val devices: StateFlow<List<NearbyDevice>> = _devices.asStateFlow()
    private val _settings = MutableStateFlow(AppSettings())
    val settings: StateFlow<AppSettings> = _settings.asStateFlow()
    private val contentScopes = com.bluelink.android.domain.PeerContentScope()
    private fun contentId(id: UUID) = contentScopes.key(id)
    private fun isSelectedContent(id: UUID) = activeSessionId?.let { contentId(it) == contentId(id) } == true
    private val messagePersistence = com.bluelink.android.data.local.MessagePersistenceQueue(scope)
    private val conversationReads = com.bluelink.android.domain.ConversationReadTracker { peerId ->
        messagePersistence.enqueue { repository.markConversationRead(peerId) }
    }
    private val messagesBySession = ConcurrentHashMap<UUID, List<ChatItem>>()
    private val recovery = com.bluelink.android.data.local.TransferRecoveryStore(java.io.File(context.filesDir, "Recovery"))
    private val sessionSupervisor = SessionSupervisor(
        context = context,
        identityStore = identityStore,
        expectedTrustedKeys = { repository.trustedKeysAtAddress(it, identityStore) },
        findIdentityCandidate = { peerId, hint -> repository.findIdentityCandidate(peerId, hint) },
        applyIdentityAssociation = ::applyIdentityAssociation,
        peerPlatform = { address -> conversations.value.firstOrNull { it.transportAddress.equals(address, true) }?.platform
            ?: bluetooth.devices.value.firstOrNull { it.address.equals(address, true) }?.platform ?: PeerPlatform.UNKNOWN },
        onSecurityRequest = ::presentSecurityRequest,
        onSecurityFinished = ::finishSecurityRequest,
        onReceiveConfirmation = ::requestIncomingFile,
        onFileConflict = ::requestFileConflict,
        onMessage = ::handleMessage,
        onTransfer = ::handleTransfer,
        onEnvelope = ::handleEnvelope,
        onReceipt = ::handleReceipt,
        onMessageStatus = ::handleMessageStatus,
        onStateChanged = ::handleSessionState,
        onDiagnostic = ::recordDiagnostic,
    )
    init {
        sessionSupervisor.persistRecovery = { session, item -> recovery.record(session.sessionId, session.startedAtEpochMs, session.transport.name, item) }
    }
    val sessions: StateFlow<List<ManagedSessionState>> = sessionSupervisor.states
    private val liveTransferOwners = ConcurrentHashMap<UUID, UUID>()
    private val usb = com.bluelink.android.usb.MtpDirectoryController(context, { sessions.value },
        { sessionSupervisor.mtpEnabled = it }, { sessionSupervisor.refreshMtp() })
    val usbState = usb.state
    fun retryUsb() = usb.refresh(retry = true)

    fun start() {
        recordDiagnostic(DiagnosticLevel.INFO, "Runtime", "启动 BlueLink Android 运行时")
        if (!started.compareAndSet(false, true)) return
        CrashReporter.drain(context).forEach { recordDiagnostic(DiagnosticLevel.ERROR, "Crash", it) }
        scope.launch(Dispatchers.IO) {
            runCatching { com.bluelink.android.files.OwnedTemporaryFiles.collect(java.io.File(context.cacheDir,"outgoing"),
                recovery::referencesTemporary, preview = false, minimumAgeMs = 86_400_000) }
                .onFailure { recordDiagnostic(DiagnosticLevel.WARNING,"Storage","暂未清理旧发送缓存") }
            runCatching { com.bluelink.android.usb.MtpOwnedStorage.collect(context,false,recovery::referencesTemporary) }
                .onFailure { recordDiagnostic(DiagnosticLevel.WARNING,"Storage","暂未清理旧 USB 中转缓存") }
        }
        scope.launch { sessions.collect { updateSelectedConnection(it); usb.observe() } }
        scope.launch { repository.transferHistory.collect { stored -> transferIndex.update { index -> index.restore(recovery.mergeHistory(stored).map { value ->
            if (value.status in com.bluelink.android.domain.HistoryQuery.activeStatuses && liveTransferOwners[value.id] == null)
                value.copy(status = TransferStatus.FAILED, failureDetail = "设备通道已断开，请由发送方重试传输") else value
        }) } } }
        scope.launch { allTransfers.collect { _transfers.value = selectedTransfers() } }

        scope.launch {
            repository.associateComposerDrafts = { legacy ->
                recovery.associate(identityStore.identityAssociations())
                kotlinx.coroutines.withContext(Dispatchers.Main.immediate) { composer.associate(identityStore.identityAssociations(), legacy) }
            }
            repository.initialize(identityStore)
            runCatching {
                val references = (repository.referencedFiles() + recovery.mergeHistory(emptyList()).mapNotNull { it.localUri }).mapNotNull { raw ->
                    if (raw.startsWith("file:")) java.io.File(java.net.URI(raw)).absolutePath
                    else raw.takeIf { it.startsWith("/") }
                }.toSet()
                withContext(Dispatchers.IO) { composer.collectStartupOrphans(references) }
            }.onFailure { recordDiagnostic(DiagnosticLevel.WARNING,"Storage","草稿缓存仍保留，稍后重试清理") }
            loadDrafts()
            repository.settings.collect { settings ->
                appSettings = settings
                settingsLoaded = true
                usb.configure(settings.usbTransferEnabled, appForeground || settings.keepBackgroundSessions)
                bluetooth.localDisplayName = settings.localDeviceName
                sessionSupervisor.localDeviceName = { bluetooth.deviceDisplayName }
                bluetooth.setDiscoverable(settings.allowDiscovery && sessionAdmission.ticket() != null)
                _settings.value = settings
                if (!settings.diagnosticsEnabled) _diagnostics.value = emptyList()
                sessionSupervisor.maxReceiveBytes = if (settings.receiveSizeLimitEnabled)
                    settings.receiveSizeLimitBytes else Long.MAX_VALUE
                sessionSupervisor.downloadDestination = settings.downloadDirectory
                sessionSupervisor.autoAcceptFiles = settings.autoDownloadFiles
                sessionSupervisor.duplicateFilePolicy = settings.duplicateFilePolicy
                sessionSupervisor.autoSaveImages = settings.autoSaveImages
                sessionSupervisor.autoSaveOtherAttachments = settings.autoSaveOtherAttachments
                sessionSupervisor.largeFilesOnlyWhileCharging = settings.largeFilesOnlyWhileCharging
                transferHistoryMutex.withLock {
                    messagePersistence.run { repository.applyRetention(settings.retentionPeriod) }
                    com.bluelink.android.domain.RecordRetention.cutoff(settings.retentionPeriod, System.currentTimeMillis())?.let { cutoff ->
                        transferIndex.update { it.retainSince(cutoff) }
                    }
                }
                scanAtStartupIfReady()
            }
        }
        scope.launch {
            // Keep all discovered endpoints for historical-device menus and reconnects.
            // DeviceScreenState alone decides which ones belong in the new-device group.
            bluetooth.devices.collect { _devices.value = it }
        }
        scope.launch {
            combine(repository.peers, repository.conversations, sessions, bluetooth.devices, repository.settings) { peers, stored, active, nearby, preferences ->
                val conversationsByPeer = stored.associateBy { it.peerId }
                val sessionsByPeer = active.filter { it.peerId != null && it.phase == ConnectionPhase.CONNECTED }
                    .groupBy { requireNotNull(it.peerId) }.mapValues { (peer, matches) -> requireNotNull(com.bluelink.android.domain.SessionRoute.preferred(matches, peer)) }
                val canonicalPeers = peers.groupBy { it.peerId.lowercase() }.values.map { matches ->
                    matches.sortedWith(compareByDescending<com.bluelink.android.data.local.PeerEntity> {
                        if (sessionsByPeer.containsKey(it.peerId)) 1 else 0
                    }.thenByDescending { it.lastConnectedAt ?: it.lastSeenAt }).first()
                }
                canonicalPeers.map { peer ->
                    val session = sessionsByPeer[peer.peerId]
                    val visible = nearby.any { device ->
                        com.bluelink.android.domain.DeviceActions.canConnect(device) &&
                            (device.address.equals(peer.transportAddress, true) ||
                                (device.discoveryId.isNotBlank() && peer.peerId.startsWith(device.discoveryId, true)))
                    }
                    ConversationSummary(peer.peerId, peer.displayName.ifBlank { "已信任设备" },
                        runCatching { PeerPlatform.valueOf(peer.platform) }.getOrDefault(PeerPlatform.UNKNOWN),
                        DeviceProjectionPolicy.availability(
                            connected = session != null,
                            nearby = visible,
                        ),
                        session?.sessionId, peer.transportAddress, conversationsByPeer[peer.peerId]?.unreadCount ?: 0,
                        conversationsByPeer[peer.peerId]?.lastActivityAt ?: peer.lastSeenAt,
                        lastConnectedAt = peer.lastConnectedAt, isTrusted = peer.trustState == "TRUSTED",
                        isRemoved = peer.trustState == "REMOVED",
                         usbReady = com.bluelink.android.usb.UsbStatePolicy.isReady(preferences.usbTransferEnabled, peer.peerId, active),
                        transport = session?.transport ?: com.bluelink.android.domain.SessionTransport.BLUETOOTH)
                }.sortedWith(compareBy<ConversationSummary> { when (it.availability) {
                    DeviceAvailability.CONNECTED -> 0; DeviceAvailability.OFFLINE -> 1; DeviceAvailability.CONNECTABLE -> 2
                } }.thenByDescending { it.lastActivityAt })
            }.combine(peerPreferences.state) { peers, preferences -> peers.map { peer ->
                val value=preferences[peer.peerId.lowercase(java.util.Locale.ROOT)]
                peer.copy(localNote=value?.note.orEmpty(),isPinned=value?.pinned ?: false)
            }.sortedWith(compareBy<ConversationSummary> { if(it.availability==DeviceAvailability.CONNECTED) 0 else 1 }
                .thenByDescending { it.isPinned }.thenByDescending { it.lastActivityAt })
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
        recordDiagnostic(DiagnosticLevel.INFO, "Runtime", "本地历史与运行时观察已启动")
    }

    fun discover() {
        recordDiagnostic(DiagnosticLevel.INFO, "Runtime", "用户请求重新扫描")
        bluetooth.startPresence()
        startupDiscovery.manualRequest()
        bluetooth.startDiscovery()
    }

    private fun scanAtStartupIfReady() {
        if (startupDiscovery.claim(settingsLoaded, appSettings.scanOnStartup,
                readBluetoothAccess(context).canUseBluetooth)) bluetooth.startDiscovery()
    }

    fun connect(device: com.bluelink.android.domain.NearbyDevice) = connectInternal(device, automatic = false)

    private fun connectInternal(device: com.bluelink.android.domain.NearbyDevice, automatic: Boolean) {
        val admissionTicket = sessionAdmission.ticket() ?: return
        if (!automatic) conversations.value.filter {
            it.transportAddress.equals(device.address, true) ||
                (device.discoveryId.isNotBlank() && it.peerId.startsWith(device.discoveryId, true))
        }.forEach { manuallyDisconnected.remove(it.peerId.lowercase(java.util.Locale.ROOT)) }

        if (!cryptoOperational.get()) {
            recordDiagnostic(DiagnosticLevel.ERROR, "Crypto", "无法连接：$cryptoFailureDetail")
            _connection.value = ConnectionState(ConnectionPhase.OFFLINE, detail = "加密运行时不可用，请查看诊断")
            return
        }
        if (!dialing.add(device.address)) {
            recordDiagnostic(DiagnosticLevel.WARNING, "Connection", "该设备已有拨号任务，忽略重复连接请求")
            return
        }
        val name = device.name
        recordDiagnostic(DiagnosticLevel.INFO, "Connection", "${if (automatic) "自动" else "开始"}连接 $name")
        _connection.value = ConnectionState(ConnectionPhase.CONNECTING, name, "正在连接可用的 BLE Rendezvous", device.address)
        scope.launch {
            try {
                runCatching {
                    bluetooth.connect(device, onStage = { stage ->
                        recordDiagnostic(DiagnosticLevel.INFO, "Connection", stage)
                        _connection.value = ConnectionState(ConnectionPhase.CONNECTING, name, stage, device.address)
                    }, onConnected = { connection ->
                        recordDiagnostic(DiagnosticLevel.INFO, "Connection", "RFCOMM 已连接，进入安全会话")
                        startSession(connection.socket, listenerRole = false, connection.peerName, admissionTicket)
                    })
                }
                    .onFailure {
                        val detail = it.message ?: it.javaClass.simpleName
                        recordDiagnostic(DiagnosticLevel.ERROR, "Connection",
                            "连接失败：${it.javaClass.simpleName}: $detail")
                        _connection.value = ConnectionState(ConnectionPhase.DISCONNECTED, name, detail, device.address)
                        if (automatic) registerReconnectFailure(device.address)
                    }
            } finally {
                dialing.remove(device.address)
            }
        }
    }

    fun sendChat(text: String, fromDraft: Boolean = false) {
        val value = text.trim()
        if (value.isEmpty()) return
        val state = com.bluelink.android.domain.SessionRoute.preferred(sessions.value, activePeerId, appSettings.usbTransferEnabled) ?: return
        val draft = if (fromDraft) state.peerId?.let(draftLedger::get) else null
        if (fromDraft && (draft == null || draft.text != text || _drafts.value == null)) return
        if (draft != null && !synchronized(sendingDrafts) { sendingDrafts.add(draft) }) return
        val sessionId = state.sessionId
        val item = ChatItem(text = value, outgoing = true, status = MessageStatus.SENDING)
        messagesBySession.compute(contentId(sessionId)) { _, items -> (items ?: emptyList()) + item }
        _messages.value = messagesBySession[contentId(sessionId)].orEmpty()
        if (appSettings.saveChatHistory) messagePersistence.enqueue { repository.saveChat(state, item) }
        val completed = AtomicBoolean()
        val onSent: (Boolean) -> Unit = { sent ->
            if (completed.compareAndSet(false, true)) {
                handleMessageStatus(state, item.id, if (sent) MessageStatus.SENT else MessageStatus.FAILED)
                if (draft != null) scope.launch {
                    try {
                        if (sent) {
                            if (appSettings.saveChatHistory) messagePersistence.run {
                                repository.saveChat(state, item.copy(status = MessageStatus.SENT))
                            }
                            synchronized(draftLedger) {
                                if (_drafts.value != null && draftLedger.clearAfterSend(draft)) _drafts.value = draftLedger.texts()
                            }
                            flushDrafts()
                        }
                    } catch (canceled: CancellationException) { throw canceled }
                    catch (_: Exception) { _draftSaveFailed.value = true }
                    finally { synchronized(sendingDrafts) { sendingDrafts.remove(draft) } }
                }
            }
        }
        try { sessionSupervisor.sendChat(sessionId, value, item.id, onSent) }
        catch (_: Exception) { onSent(false) }
    }

    /** Explicit incoming-share confirmation only. Failed shared text is never auto-queued. */
    suspend fun sendSharedText(peerId: String, id: UUID, text: String): Boolean {
        if(!identityStore.trustedEntries().containsKey(peerId.lowercase(java.util.Locale.ROOT))) return false
        val state=com.bluelink.android.domain.SessionRoute.preferred(sessions.value,peerId,appSettings.usbTransferEnabled) ?: return false
        val item=ChatItem(id,text,true,java.time.Instant.now(),MessageStatus.SENDING)
        if(appSettings.saveChatHistory && messagePersistence.run {repository.prepareSharedText(state,item)}) return true
        withContext(Dispatchers.Main) {
            messagesBySession.compute(contentId(state.sessionId)) { _,items ->
                val previous=items.orEmpty().firstOrNull {it.id==id}
                items.orEmpty().filterNot {it.id==id} + if(previous?.status in setOf(MessageStatus.DELIVERED,MessageStatus.READ)) previous!! else item
            }
            if(isSelectedContent(state.sessionId)) _messages.value=messagesBySession[contentId(state.sessionId)].orEmpty()
            _historyRevision.update {it+1}
        }
        val sent=CompletableDeferred<Boolean>()
        val success=try {sessionSupervisor.sendSharedChat(state.sessionId,text,id) {sent.complete(it)};sent.await()}
            catch(error:Exception) {if(error is CancellationException)throw error;false}
        val status=if(success) MessageStatus.SENT else MessageStatus.FAILED
        withContext(Dispatchers.Main) {handleMessageStatus(state,id,status)}
        if(appSettings.saveChatHistory) messagePersistence.run {repository.updateMessageStatus(id.toString(),peerId,status)}
        return success
    }

    suspend fun sendSharedFile(peerId: String,id: UUID,uri: Uri,name: String,size: Long): Boolean {
        if(!identityStore.trustedEntries().containsKey(peerId.lowercase(java.util.Locale.ROOT))) return false
        val state=com.bluelink.android.domain.SessionRoute.preferred(sessions.value,peerId,appSettings.usbTransferEnabled) ?: return false
        transferIndex.value.items[id]?.let { existing ->
            if(!existing.peerId.equals(peerId,true)) return false
            if(existing.status==TransferStatus.COMPLETED || existing.status in com.bluelink.android.domain.HistoryQuery.activeStatuses) return true
            val original=existing.localUri ?: return false
            if(!withContext(Dispatchers.IO) { com.bluelink.android.files.FileInteraction.readable(context,original) }) return false
            return sessionSupervisor.retryFile(state.sessionId,Uri.parse(original),existing)
        }
        val prepared=CompletableDeferred<Boolean>()
        sessionSupervisor.sendFile(state.sessionId,uri,name,size,id,
            beforeQueue = { item -> transferHistoryMutex.withLock { repository.prepareSharedTransfer(state,item) } },
            onPrepared = { item -> prepared.complete(item != null) })
        return prepared.await()
    }

    suspend fun sendComposerPart(peerId: String, part: com.bluelink.android.composer.ComposerPart): Boolean {
        val file = part.file ?: return sendSharedText(peerId, part.id, part.text.orEmpty())
        if (!identityStore.trustedEntries().containsKey(peerId.lowercase(java.util.Locale.ROOT))) return false
        val state = com.bluelink.android.domain.SessionRoute.preferred(sessions.value, peerId, appSettings.usbTransferEnabled) ?: return false
        if (file.size < 0 || !withContext(Dispatchers.IO) { java.io.File(file.path).let { it.isFile && it.length() == file.size } }) return false
        val announced = CompletableDeferred<Boolean>()
        sessionSupervisor.sendFile(state.sessionId, Uri.fromFile(java.io.File(file.path)), file.name, file.size, UUID.randomUUID(),
            beforeQueue = { item -> transferHistoryMutex.withLock { repository.prepareSharedTransfer(state, item) } },
            onPrepared = { if (it == null) announced.complete(false) }, onAnnounced = { announced.complete(it) })
        return announced.await()
    }

    fun sendFile(uri: Uri, name: String, size: Long) {
        com.bluelink.android.domain.SessionRoute.preferred(sessions.value, activePeerId, appSettings.usbTransferEnabled)
            ?.let { sessionSupervisor.sendFile(it.sessionId, uri, name, size) }
    }

    private val fileBatchMutex = Mutex()
    val fileBatch: com.bluelink.android.domain.FileBatchOperations = object : com.bluelink.android.domain.FileBatchOperations {
        override suspend fun check(ids: List<UUID>, action: com.bluelink.android.domain.FileBatchAction) = withContext(Dispatchers.IO) {
            require(ids.distinct().size <= com.bluelink.android.domain.FileBatchPolicy.MAXIMUM_SELECTION)
            ids.distinct().map { id ->
                val item = transferIndex.value.items[id]
                com.bluelink.android.domain.FileBatchPolicy.check(id, item, action,
                    item?.let { resolveTransferSession(it) != null } == true,
                    action !in setOf(com.bluelink.android.domain.FileBatchAction.RETRY, com.bluelink.android.domain.FileBatchAction.SHARE) ||
                        com.bluelink.android.files.FileInteraction.readable(context, item?.localUri))
            }
        }
        override suspend fun run(ids: List<UUID>, action: com.bluelink.android.domain.FileBatchAction) = fileBatchMutex.withLock {
            val shares = mutableListOf<com.bluelink.android.domain.ChatAttachment>()
            val results = com.bluelink.android.domain.FileBatchRunner.run(ids, { check(listOf(it),action).single() }) { id ->
                val item = transferIndex.value.items[id] ?: error("Missing transfer")
                when(action) {
                    com.bluelink.android.domain.FileBatchAction.RETRY -> check(sessionSupervisor.retryFile(
                        resolveTransferSession(item) ?: error("Offline"), Uri.parse(item.localUri), item))
                    com.bluelink.android.domain.FileBatchAction.CANCEL -> check(sessionSupervisor.cancelTransfer(
                        resolveTransferSession(item) ?: error("Offline"), id))
                    com.bluelink.android.domain.FileBatchAction.DELETE_RECORDS -> transferHistoryMutex.withLock {
                        val latest = transferIndex.value.items[id] ?: error("Missing transfer")
                        check(latest.status !in com.bluelink.android.domain.HistoryQuery.activeStatuses)
                        check(recovery.dismiss(id))
                        repository.deleteTransfer(id)
                        transferIndex.update { it.forget(setOf(id)) }
                        _transfers.value = selectedTransfers()
                    }
                    com.bluelink.android.domain.FileBatchAction.SHARE -> shares.add(com.bluelink.android.domain.ChatAttachment(
                        item.attachmentId ?: item.id, item.id, item.name, item.mimeType, item.totalBytes,
                        item.localUri, item.status.name, completedBytes = item.completedBytes))
                }
            }
            if(shares.isEmpty()) results else try {
                val intent = withContext(Dispatchers.IO) { com.bluelink.android.files.FileInteraction.multiShareIntent(context, shares) }
                withContext(Dispatchers.Main) { context.startActivity(android.content.Intent.createChooser(intent,null).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)) }
                results
            } catch(error: Exception) {
                if(error is kotlinx.coroutines.CancellationException) throw error
                results.map { if(it.outcome == com.bluelink.android.domain.FileBatchOutcome.SUBMITTED)
                    it.copy(outcome = com.bluelink.android.domain.FileBatchOutcome.FAILED, reason = com.bluelink.android.domain.FileBatchReason.FAILED) else it }
            }
        }
    }

    fun retryTransfer(value: TransferItem) {
        val uri = value.localUri?.let(Uri::parse) ?: return
        if (value.outgoing) resolveTransferSession(value)?.let { sessionSupervisor.retryFile(it, uri, value) }
    }

    internal fun collectTemporaryFiles(preview: Boolean): com.bluelink.android.files.TemporaryCleanup {
        val local=com.bluelink.android.files.OwnedTemporaryFiles.collect(java.io.File(context.cacheDir,"outgoing"),recovery::referencesTemporary,preview)
        val usb=com.bluelink.android.usb.MtpOwnedStorage.collect(context,preview,recovery::referencesTemporary)
        return com.bluelink.android.files.TemporaryCleanup(local.bytes+usb.bytes,local.files+usb.files,local.retained+usb.retained,local.errors+usb.errors)
    }

    fun switchQueuedToBluetooth(value: TransferItem): Boolean {
        val current=transferIndex.value.items[value.id] ?: return false
        val session=resolveTransferSession(current) ?: return false
        return sessionSupervisor.switchQueuedToBluetooth(session,current)
    }

    fun retryTransferFrom(value: TransferItem, uri: Uri): Boolean {
        val current = transferIndex.value.items[value.id] ?: return false
        if (current.peerId != value.peerId || com.bluelink.android.domain.TransferAction.RESELECT !in
            com.bluelink.android.domain.TransferActions.available(current)) return false
        val session = resolveTransferSession(current) ?: return false
        return sessionSupervisor.retryFile(session, uri, current)
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
        com.bluelink.android.domain.TransferSessionSelector.resolve(value, sessions.value,
            setOfNotNull(liveTransferOwners[value.id]))

    fun confirmTrust(accepted: Boolean, requestId: UUID) {
        trustConfirmation.resolve(requestId, accepted)
    }

    fun disconnect() {
        val id = activeSessionId ?: return
        recordDiagnostic(DiagnosticLevel.INFO, "Connection", "用户请求断开当前会话")
        sessions.value.firstOrNull { it.sessionId == id }?.peerId?.let {
            manuallyDisconnected.add(it.lowercase(java.util.Locale.ROOT))
        }
        sessions.value.filter { it.peerId.equals(activePeerId, true) }.forEach { sessionSupervisor.disconnect(it.sessionId) }
        _connection.value = ConnectionState(ConnectionPhase.DISCONNECTED, detail = "已断开")
    }

    fun disconnectPeer(peerId: String) {
        val session = sessions.value.firstOrNull { it.peerId.equals(peerId, true) } ?: return
        recordDiagnostic(DiagnosticLevel.INFO, "Connection", "用户请求断开 ${session.peerName}")
        manuallyDisconnected.add(peerId.lowercase(java.util.Locale.ROOT))
        sessions.value.filter { it.peerId.equals(peerId, true) }.forEach { sessionSupervisor.disconnect(it.sessionId) }
        if (isSelectedContent(session.sessionId))
            _connection.value = ConnectionState(ConnectionPhase.DISCONNECTED, session.peerName, "已断开")
    }

    fun selectSession(sessionId: UUID) {
        historySelectionVersion.incrementAndGet()
        requestDraftFlush()
        activeSessionId = sessionId
        val state = sessions.value.firstOrNull { it.sessionId == sessionId }
        activePeerId = state?.peerId
        _messages.value = messagesBySession[contentId(sessionId)].orEmpty()
        _transfers.value = selectedTransfers()
        state?.let {
            _connection.value = ConnectionState(it.phase, it.peerName, it.detail, transport = it.transport)
            it.peerId?.let { peerId ->
                loadHistory(peerId, sessionId)
            }
        }
    }

    fun setVisibleMessagePeer(peerId: String?) = conversationReads.setVisiblePeer(peerId)

    fun setMessagePageResumed(resumed: Boolean) = conversationReads.setResumed(resumed)

    fun selectPeer(peerId: String) {
        val selectionVersion = historySelectionVersion.incrementAndGet()
        requestDraftFlush()
        activePeerId = peerId
        val connected = com.bluelink.android.domain.SessionRoute.preferred(sessions.value, peerId)
        if (connected != null) selectSession(connected.sessionId) else {
            activeSessionId = null
            _messages.value = emptyList()
            _transfers.value = selectedTransfers()
            val summary = conversations.value.firstOrNull { it.peerId == peerId }
            _connection.value = ConnectionState(ConnectionPhase.OFFLINE, summary?.peerName, "设备离线 · 可查看历史记录")
            scope.launch {
                val history = loadProjectedHistory(peerId)
                // A slower previous selection must not replace the newly selected conversation.
                if (selectionVersion != historySelectionVersion.get() || activeSessionId != null || !activePeerId.equals(peerId, true)) return@launch
                _messages.value = history
                _transfers.value = selectedTransfers()
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
        eventNotifications.settingsChanged(value)
        if (!value.diagnosticsEnabled) _diagnostics.value = emptyList()
        scope.launch { repository.saveSettings(value) }
    }

    fun keepBackgroundSessionsEnabled(): Boolean = appSettings.keepBackgroundSessions

    fun onAppBackgrounded() {
        requestDraftFlush()
        appForeground = false
        if (appSettings.keepBackgroundSessions) return
        recordDiagnostic(DiagnosticLevel.INFO, "Runtime", "后台会话已关闭，应用离开前台后停止蓝牙端点")
        usb.configure(appSettings.usbTransferEnabled, false)
        sessionSupervisor.disconnectAll("后台会话已关闭")
        bluetooth.suspendBackgroundWork()
    }

    fun onAppForegrounded() {
        requestDraftFlush()
        appForeground = true
        // History/settings must load on a cold start even without Bluetooth access.
        if (!started.get()) {
            start()
            return
        }
        usb.configure(appSettings.usbTransferEnabled, true)
        usb.refresh()
        if (!readBluetoothAccess(context).canUseBluetooth || sessionAdmission.ticket() == null) return
        bluetooth.setDiscoverable(appSettings.allowDiscovery)
        startBluetoothEndpoints()
        scanAtStartupIfReady()
    }

    private val _operationFailed = MutableStateFlow(false)
    val operationFailed = _operationFailed.asStateFlow()
    fun dismissOperationFailure() { _operationFailed.value = false }
    fun forgetAllPeers() { scope.launch {
        try { performPrivacyAction(com.bluelink.android.domain.PrivacyAction.REMOVE_ALL_TRUST) }
        catch (canceled: kotlinx.coroutines.CancellationException) { throw canceled }
        catch (_: Exception) { _operationFailed.value = true }
    } }
    fun forgetPeer(peerId: String) { scope.launch {
        privacyMutex.withLock {
            try {
                manuallyDisconnected.add(peerId.lowercase(java.util.Locale.ROOT))
                sessions.value.filter { it.peerId.equals(peerId, true) }.forEach {
                    sessionSupervisor.disconnect(it.sessionId, "设备信任已移除")
                }
                identityStore.removeTrust(peerId)
                repository.synchronizeTrust(peerId, identityStore, removeFromDeviceList = true)
            } catch (canceled: kotlinx.coroutines.CancellationException) { throw canceled }
            catch (_: Exception) { _operationFailed.value = true }
        }
    } }

    /** Only invoked after the app confirmation; no received files are removed. */
    suspend fun performPrivacyAction(action: com.bluelink.android.domain.PrivacyAction) = privacyMutex.withLock {
        kotlinx.coroutines.withContext(Dispatchers.IO + kotlinx.coroutines.NonCancellable) {
            when (action) {
                com.bluelink.android.domain.PrivacyAction.CLEAR_MESSAGES -> {
                    clearDrafts()
                    messagePersistence.run { repository.clearChatHistory(); _historyRevision.value += 1 }
                    messagesBySession.clear(); _messages.value = emptyList()
                }
                com.bluelink.android.domain.PrivacyAction.CLEAR_TRANSFERS -> {
                    clearTransferRecords()
                }
                else -> {
                    sessionAdmission.pause()
                    try {
                        bluetooth.setDiscoverable(false)
                        bluetooth.suspendBackgroundWork()
                        manuallyDisconnected.addAll(conversations.value.map { it.peerId.lowercase(java.util.Locale.ROOT) })
                        sessionSupervisor.disconnectAll("设备身份或信任已更改")
                        trustConfirmation.cancelAll(); incomingConfirmation.cancelAll()
                        fileConflictDecisions.values.forEach { it.complete(com.bluelink.android.files.DuplicateChoice.CANCEL) }
                        if (action == com.bluelink.android.domain.PrivacyAction.RESET_IDENTITY) identityStore.resetIdentity()
                        else identityStore.removeAllTrust()
                        _identityFingerprint.value = currentFingerprint()
                        connectedThisRun.clear(); reconnectBackoff.clear()
                        repository.synchronizeAllTrust(identityStore,
                            removeFromDeviceList = action == com.bluelink.android.domain.PrivacyAction.REMOVE_ALL_TRUST)
                    } finally {
                        bluetooth.updateIdentity(identityStore.identity.peerId().joinToString("") { "%02X".format(it) })
                        sessionAdmission.resume()
                        if (appForeground || appSettings.keepBackgroundSessions) {
                            bluetooth.setDiscoverable(appSettings.allowDiscovery)
                            startBluetoothEndpoints()
                        }
                    }
                }
            }
        }
    }

    fun clearChatHistory() {
        messagesBySession.clear(); _messages.value = emptyList()
        scope.launch { clearDrafts(); messagePersistence.run { repository.clearChatHistory(); _historyRevision.value += 1 } }
    }

    suspend fun deleteSelectedMessages(peerId: String, selection: List<UUID>): List<com.bluelink.android.domain.MessageBatchResult> =
        messagePersistence.run {
            com.bluelink.android.domain.MessageBatch.delete(selection,
                lookup = { id -> if (activePeerId == peerId) _messages.value.firstOrNull { it.id == id } else null },
                activeTransfer = { current -> current.attachments.any { attachment ->
                    transferIndex.value.items[attachment.transferId]?.let {
                        it.status in com.bluelink.android.domain.HistoryQuery.activeStatuses || it.recoveryPending } == true
                } }, remove = { id ->
                    repository.deleteMessage(id)
                    messagesBySession.replaceAll { _, items -> items.filterNot { it.id == id } }
                    _messages.value = _messages.value.filterNot { it.id == id }
                    _historyRevision.value += 1
                })
        }

    fun deleteMessage(messageId: UUID) {
        messagesBySession.replaceAll { _, items -> items.filterNot { it.id == messageId } }
        _messages.value = _messages.value.filterNot { it.id == messageId }
        messagePersistence.enqueue { repository.deleteMessage(messageId); _historyRevision.value += 1 }
    }

    fun clearConversation(peerId: String) {
        sessions.value.filter { it.peerId.equals(peerId, true) }.forEach { state ->
            messagesBySession[contentId(state.sessionId)] = emptyList()
        }
        if (activePeerId.equals(peerId, true)) _messages.value = emptyList()
        scope.launch { clearDrafts(peerId); messagePersistence.run { repository.clearConversation(peerId); _historyRevision.value += 1 } }
    }

    private suspend fun clearTransferRecords() = transferHistoryMutex.withLock {
        val removedIds = transferIndex.value.items.values.filter {
            it.status !in com.bluelink.android.domain.HistoryQuery.activeStatuses && !it.recoveryPending
        }.map { it.id }.filter { recovery.dismiss(it) }.toSet()
        repository.clearTransferHistory()
        transferIndex.update { it.forget(removedIds) }
        _transfers.value = selectedTransfers()
    }

    fun clearTransferHistory() { scope.launch { clearTransferRecords() } }

    fun deleteTransfer(transferId: UUID) { scope.launch {
        transferHistoryMutex.withLock {
            val current = transferIndex.value.items[transferId] ?: return@withLock
            if (current.status in com.bluelink.android.domain.HistoryQuery.activeStatuses || !recovery.dismiss(transferId)) return@withLock
            repository.deleteTransfer(transferId)
            transferIndex.update { it.forget(setOf(transferId)) }
            _transfers.value = selectedTransfers()
        }
    } }

    private fun startSession(socket: BluetoothSocket, listenerRole: Boolean, preferredName: String? = null, admissionTicket: Long) {
        val id = sessionAdmission.admit(admissionTicket) { sessionSupervisor.add(socket, listenerRole, preferredName) }
        if (id == null) {
            runCatching { socket.close() }
            recordDiagnostic(DiagnosticLevel.WARNING, "Connection", "连接请求已过期，拒绝新的 RFCOMM 通道")
            return
        }
        if (activeSessionId == null) selectSession(id)
    }

    private fun presentSecurityRequest(request: com.bluelink.android.domain.SecurityRequest) = synchronized(securityLock) {
        securityRequests[request.id] = request
        _securityRequest.value = securityRequests.values.firstOrNull()
    }
    private fun finishSecurityRequest(request: com.bluelink.android.domain.SecurityRequest) {
        if (request.stage.value in setOf(com.bluelink.android.domain.TrustStage.COMPLETED, com.bluelink.android.domain.TrustStage.CANCELED))
            dismissSecurityRequest(request.id)
    }
    fun confirmSecurityRequest(id: UUID) = synchronized(securityLock) { securityRequests[id]?.confirm(); Unit }
    fun dismissSecurityRequest(id: UUID) = synchronized(securityLock) {
        securityRequests.remove(id)?.cancel()
        _securityRequest.value = securityRequests.values.firstOrNull()
    }
    fun retrySecurityRequest(id: UUID) {
        val request = synchronized(securityLock) { securityRequests[id] } ?: return
        if (request.stage.value !in setOf(com.bluelink.android.domain.TrustStage.REJECTED,
                com.bluelink.android.domain.TrustStage.TIMED_OUT, com.bluelink.android.domain.TrustStage.REMOTE_CLOSED,
                com.bluelink.android.domain.TrustStage.FAILED)) return
        dismissSecurityRequest(id)
        if (request.transportAddress.startsWith("usb:", ignoreCase = true)) {
            retryUsb()
            return
        }
        val target = bluetooth.devices.value.firstOrNull { it.address.equals(request.transportAddress, true) }
        if (target != null) connect(target) else scope.launch {
            _connection.value = ConnectionState(ConnectionPhase.CONNECTING, request.peerName, "正在查找设备以重新连接", request.transportAddress)
            discover()
            val found = kotlinx.coroutines.withTimeoutOrNull(15_000L) {
                bluetooth.devices.first { list -> list.any { it.address.equals(request.transportAddress, true) && it.rendezvousAvailable && it.connectable } }
                    .first { it.address.equals(request.transportAddress, true) && it.rendezvousAvailable && it.connectable }
            }
            if (found != null) connect(found)
            else _connection.value = ConnectionState(ConnectionPhase.DISCONNECTED, request.peerName, "暂未发现该设备，请确认对端蓝联已打开", request.transportAddress)
        }
    }
    private fun handleMessage(session: ManagedSessionState, message: ChatItem) {
        if (!message.outgoing && !appForeground) eventNotifications.message(session, message.id.toString(), appSettings)
        messagesBySession.compute(contentId(session.sessionId)) { _, items -> (items ?: emptyList()) + message }
        if (isSelectedContent(session.sessionId)) _messages.value = messagesBySession[contentId(session.sessionId)].orEmpty()
        if (appSettings.saveChatHistory) conversationReads.recordMessage(session.peerId, message.outgoing) { unread ->
            messagePersistence.enqueue { repository.saveChat(session, message, unread = unread) }
        }
    }

    private val transferProjectionGate = Any()
    private val transferProjection = com.bluelink.android.session.SessionTransferLedger()
    private fun handleTransfer(session: ManagedSessionState, transfer: TransferItem) {
        synchronized(transferProjectionGate) {
        if (transfer.attemptId != null && transferProjection.record(transfer) == null) return
        if (transfer.status in setOf(TransferStatus.OFFERED, TransferStatus.QUEUED, TransferStatus.TRANSFERRING,
                TransferStatus.PAUSED, TransferStatus.REMOTE_PAUSED, TransferStatus.RESUMING, TransferStatus.VERIFYING, TransferStatus.COMMITTING))
            liveTransferOwners[transfer.id] = session.sessionId
        else liveTransferOwners.remove(transfer.id, session.sessionId)
        val oldState = transferIndex.value.items[transfer.id]?.status
        if (transfer.status == com.bluelink.android.domain.TransferStatus.COMPLETED &&
            oldState != com.bluelink.android.domain.TransferStatus.COMPLETED && transfer.role != AttachmentRole.IMAGE_PREVIEW) {
            eventNotifications.completed(transfer, appSettings)
        }
        val now = System.currentTimeMillis()
        val sample = requireNotNull(transferSamples.compute(transfer.id) { _, previous ->
            val updatedAt = maxOf(now, (previous?.updatedAt ?: 0L) + 1L)
            val elapsed = previous?.let { (updatedAt - it.updatedAt).coerceAtLeast(1) } ?: 0L
            val instant = if (previous != null && transfer.completedBytes > previous.bytes && elapsed >= 120)
                (transfer.completedBytes - previous.bytes) * 1000.0 / elapsed else 0.0
            val speed = when {
                instant <= 0.0 -> previous?.speed ?: 0.0
                previous == null || previous.speed <= 0.0 -> instant
                else -> previous.speed * .65 + instant * .35
            }
            TransferSample(transfer.completedBytes, updatedAt,
                previous?.startedAt ?: transferIndex.value.items[transfer.id]?.startedAtEpochMs ?: transfer.startedAtEpochMs, speed)
        })
        val scoped = transfer.copy(peerId = session.peerId, startedAtEpochMs = sample.startedAt,
            updatedAtEpochMs = sample.updatedAt, bytesPerSecond = sample.speed)
        transferIndex.update { it.admit(scoped) }
        scoped.messageId?.let { messageId -> messagesBySession.computeIfPresent(contentId(session.sessionId)) { _, items ->
            items.map { message -> if (message.id != messageId) message else message.copy(
                attachments = message.attachments.map { attachment ->
                    if (scoped.role == AttachmentRole.IMAGE_PREVIEW && attachment.isImage) attachment.copy(
                        previewUri = scoped.localUri ?: attachment.previewUri)
                    else attachment.withTransfer(scoped)
                })
            }
        } }
        if (isSelectedContent(session.sessionId)) _messages.value = messagesBySession[contentId(session.sessionId)].orEmpty()
        if (isSelectedContent(session.sessionId)) _transfers.value = selectedTransfers()
        if (appSettings.saveTransferHistory) scope.launch {
            transferHistoryMutex.withLock {
                if (transferIndex.value.accepts(scoped.id)) repository.saveTransfer(session, scoped)
            }
        }
        }
    }

    private fun handleEnvelope(session: ManagedSessionState, envelope: ChatEnvelope, outgoing: Boolean) {
        if (!outgoing && !appForeground && envelope.attachments().isNotEmpty()) eventNotifications.message(session, envelope.messageId().toString(), appSettings)
        recordDiagnostic(DiagnosticLevel.INFO, "Message",
            "收到结构化消息（session=${session.sessionId.toString().take(8)}，type=${envelope.kind()}，attachments=${envelope.attachments().size}）")
        if (appSettings.saveChatHistory && envelope.attachments().isNotEmpty()) {
            conversationReads.recordMessage(session.peerId, outgoing) { unread ->
                messagePersistence.enqueue { repository.saveEnvelope(session, envelope, outgoing, unread = unread) }
            }
        }
        if (envelope.attachments().isNotEmpty()) {
            val descriptors = if (envelope.kind() == com.bluelink.core.ChatPayloadKind.IMAGE)
                envelope.attachments().filter { it.role() == AttachmentRole.IMAGE_ORIGINAL }
            else envelope.attachments().filter { it.role() == AttachmentRole.FILE }
            val attachments = descriptors.map { value -> ChatAttachment(value.attachmentId(), value.transferId(),
                value.fileName(), value.mimeType(), value.size()) }
            val item = ChatItem(envelope.messageId(), envelope.body(), outgoing,
                java.time.Instant.ofEpochMilli(envelope.createdAt()),
                if (outgoing) MessageStatus.SENDING else MessageStatus.RECEIVED,
                if (envelope.kind() == com.bluelink.core.ChatPayloadKind.IMAGE) ChatItemKind.IMAGE else ChatItemKind.FILE,
                attachments)
            messagesBySession.compute(contentId(session.sessionId)) { _, items ->
                val previous = items?.firstOrNull { it.id == item.id }
                (items ?: emptyList()).filterNot { it.id == item.id } + if (previous == null) item
                else item.copy(status = MessageDeliveryPolicy.merge(previous.status, item.status), attachments = previous.attachments)
            }
            if (isSelectedContent(session.sessionId)) _messages.value = messagesBySession[contentId(session.sessionId)].orEmpty()
        }
    }

    private fun handleReceipt(session: ManagedSessionState, receipt: ChatReceipt) {
        val status = when (receipt.state()) {
            ReceiptState.FAILED -> MessageStatus.FAILED
            ReceiptState.READ -> MessageStatus.READ
            ReceiptState.DELIVERED -> MessageStatus.DELIVERED
            null -> return
        }
        handleMessageStatus(session, receipt.messageId(), status)
    }

    private fun handleMessageStatus(session: ManagedSessionState, messageId: UUID, status: MessageStatus) {
        var found = false
        messagesBySession.computeIfPresent(contentId(session.sessionId)) { _, items -> items.map { item ->
            if (item.id != messageId || !item.outgoing) item else {
                found = true
                item.copy(status = MessageDeliveryPolicy.merge(item.status, status))
            }
        } }
        if (!found) return
        if (isSelectedContent(session.sessionId)) _messages.value = messagesBySession[contentId(session.sessionId)].orEmpty()
        val peerId = session.peerId ?: return
        if (appSettings.saveChatHistory) messagePersistence.enqueue {
            repository.updateMessageStatus(messageId.toString(), peerId, status)
        }
    }

    private suspend fun applyIdentityAssociation() = draftSaveMutex.withLock {
        synchronized(draftLedger) { _drafts.value = null }
        try {
            check(persistDrafts()) { "Drafts unavailable for identity association" }
            transferHistoryMutex.withLock {
                messagePersistence.run {
                    repository.applyIdentityAssociations(identityStore)
                    val associations = identityStore.identityAssociations()
                    while (activePeerId?.let { associations[it] } != null) activePeerId = associations[activePeerId]
                    loadDrafts()
                }
            }
        } finally { synchronized(draftLedger) { _drafts.value = draftLedger.texts() } }
    }

    private fun handleSessionState(state: ManagedSessionState) {
        state.peerId?.let { contentScopes.bind(state.sessionId, it) }
        val previousPhase = notifiedPhases.put(state.sessionId, state.phase)
        if (previousPhase != state.phase && (state.phase == ConnectionPhase.CONNECTED ||
                (state.phase == ConnectionPhase.DISCONNECTED && previousPhase == ConnectionPhase.CONNECTED))) {
            eventNotifications.connection(state, appSettings)
        }
        if (state.phase == ConnectionPhase.DISCONNECTED) notifiedPhases.remove(state.sessionId)
        when (state.phase) {
            ConnectionPhase.CONNECTED -> {
                state.peerId?.let { connectedThisRun.add(it.lowercase(java.util.Locale.ROOT)) }
                if (activeSessionId == state.sessionId) activePeerId = state.peerId
                reconnectBackoff.remove(state.transportAddress.uppercase())
                scope.launch {
                    repository.recordConnectedSession(state, identityStore)
                    state.peerId?.let {
                        loadHistory(it, state.sessionId)
                    }
                }
            }
            ConnectionPhase.DISCONNECTED -> {
                val interrupted = transferIndex.value.items.values.filter {
                    liveTransferOwners[it.id] == state.sessionId
                }
                interrupted.forEach { handleTransfer(state, it.copy(status = TransferStatus.FAILED,
                    failureDetail = "设备通道已断开，请重试传输")) }
                scope.launch { repository.recordDisconnectedSession(state) }
            }
            else -> Unit
        }
    }

    private fun loadHistory(peerId: String, sessionId: UUID) {
        scope.launch {
            val persisted = loadProjectedHistory(peerId)
            messagesBySession.compute(contentId(sessionId)) { _, current ->
                (persisted + current.orEmpty()).associateBy { it.id }.values.sortedBy { it.timestamp }
            }
            if (isSelectedContent(sessionId)) _messages.value = messagesBySession[contentId(sessionId)].orEmpty()
            if (isSelectedContent(sessionId)) _transfers.value = selectedTransfers()
        }
    }

    private fun attemptAutoConnect(snapshot: AutoConnectSnapshot) {
        if (!cryptoOperational.get() ||
            !readBluetoothAccess(context).canUseBluetooth) return
        val activeAddresses = snapshot.sessions.filter { it.phase != ConnectionPhase.DISCONNECTED }
            .map { it.transportAddress.uppercase() }.toSet()
        val now = System.currentTimeMillis()
        snapshot.peers.forEach { peer ->
            val key = peer.peerId.lowercase(java.util.Locale.ROOT)
            if (!com.bluelink.android.domain.AutoConnectionPolicy.shouldConnect(
                    peer.trustState == "TRUSTED", key in connectedThisRun, key in manuallyDisconnected,
                    snapshot.settings.autoConnectTrustedDevices, snapshot.settings.reconnectAfterDisconnect)) return@forEach
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
        val selected = com.bluelink.android.domain.SessionRoute.preferred(states, activePeerId)
            ?: states.firstOrNull { it.sessionId == activeSessionId }
        if (selected != null) {
            if (activeSessionId != selected.sessionId) {
                activeSessionId = selected.sessionId
                selected.peerId?.let { loadHistory(it, selected.sessionId) }
            }
            activePeerId = selected.peerId ?: activePeerId
            _connection.value = ConnectionState(selected.phase, selected.peerName, selected.detail, transport = selected.transport)
            _messages.value = messagesBySession[contentId(selected.sessionId)].orEmpty()
            _transfers.value = selectedTransfers()
        } else if (activeSessionId != null) {
            activeSessionId = null
            // Keep the selected peer and its history instead of jumping to another connected device.
            _connection.value = ConnectionState(ConnectionPhase.DISCONNECTED, detail = "设备已断开")
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
        val admissionTicket = sessionAdmission.ticket() ?: return
        if (!readBluetoothAccess(context).canUseBluetooth) return
        bluetooth.startPresence { connection ->
            if (cryptoOperational.get()) {
                recordDiagnostic(DiagnosticLevel.INFO, "Connection", "正在响应 Windows 发起的连接请求")
                startSession(connection.socket, listenerRole = false, preferredName = connection.peerName, admissionTicket = admissionTicket)
            } else {
                recordDiagnostic(DiagnosticLevel.ERROR, "Crypto", "拒绝安全会话：$cryptoFailureDetail")
                connection.socket.close()
            }
        }
        bluetooth.startServer { accepted ->
            if (cryptoOperational.get()) startSession(accepted, listenerRole = true, admissionTicket = admissionTicket)
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
