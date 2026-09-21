package com.bluelink.android.session

import android.content.ContentResolver
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.util.Log
import com.bluelink.android.data.IdentityStore
import com.bluelink.android.data.hex
import com.bluelink.android.domain.ChatItem
import com.bluelink.android.domain.DiagnosticLevel
import com.bluelink.android.domain.MessageStatus
import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TransferStatus
import com.bluelink.android.files.ReceivedFileStore
import com.bluelink.core.BtxFrame
import com.bluelink.core.BtxCapabilities
import com.bluelink.core.BtxRecordCodec
import com.bluelink.core.ChatEnvelope
import com.bluelink.core.ChatPayloadKind
import com.bluelink.core.ChatReceipt
import com.bluelink.core.AttachmentDescriptor
import com.bluelink.core.AttachmentRole
import com.bluelink.core.HandshakeHello
import com.bluelink.core.MessagePayloadCodec
import com.bluelink.core.ProtocolGreeting
import com.bluelink.core.ReceiptState
import com.bluelink.core.ReplayGuard
import com.bluelink.core.SessionKeys
import com.bluelink.core.TransferControl
import com.bluelink.core.TransferControlAction
import com.bluelink.core.TransferControlCodec
import com.bluelink.core.TransferReceiver
import com.bluelink.core.WireMessageType
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.security.MessageDigest
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.PriorityBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

internal class PeerSession(
    private val context: Context,
    private val connection: com.bluelink.android.transport.PeerConnection,
    private val listenerRole: Boolean,
    private val identityStore: IdentityStore,
    private val sessionId: UUID,
    private val transportAddress: String,
    private val platform: com.bluelink.android.domain.PeerPlatform,
    private val preferredPeerName: String,
    private val localDeviceName: String = "",
    private val expectedTrustedKeys: suspend () -> List<ByteArray>,
    private val onSecurityRequest: (com.bluelink.android.domain.SecurityRequest) -> Unit,
    private val onSecurityFinished: (com.bluelink.android.domain.SecurityRequest) -> Unit,
    private val onPeerIdentified: (String) -> Unit,
    private val onMessage: (ChatItem) -> Unit,
    onTransfer: (TransferItem) -> Unit,
    private val onReady: (String) -> Unit,
    private val onClosed: (String) -> Unit,
    private val onDiagnostic: (DiagnosticLevel, String, String) -> Unit = { _, _, _ -> },
    private val onEnvelope: (ChatEnvelope, Boolean) -> Unit = { _, _ -> },
    private val onReceipt: (ChatReceipt) -> Unit = {},
    private val onMessageStatus: (UUID, MessageStatus) -> Unit = { _, _ -> },
    initialMaxReceiveBytes: Long = Long.MAX_VALUE,
    initialDownloadDestination: String = "downloads://BlueLink",
    initialAutoAcceptFiles: Boolean = true,
    initialAutoSaveImages: Boolean = true,
    initialAutoSaveOtherAttachments: Boolean = false,
    initialLargeFilesOnlyWhileCharging: Boolean = false,
    private val onReceiveConfirmation: suspend (TransferItem) -> Boolean,
    initialDuplicateFilePolicy: String = "rename",
    private val onFileConflict: suspend (String) -> com.bluelink.android.files.DuplicateChoice,
    private val findIdentityCandidate: suspend (String) -> com.bluelink.android.domain.IdentityCandidate? = { null },
    private val canAssociateIdentity: (com.bluelink.android.domain.IdentityCandidate) -> Boolean = { false },
    private val applyIdentityAssociation: suspend () -> Unit = {},

) {
    companion object {
        private const val FILE_EXTENT_SIZE = 64 * 1024
        private const val OFFER_TIMEOUT_MS = 30_000L
    }

    private val transferStreams = TransferStreamFence()
    private val notifyTransfer = onTransfer
    private val onTransfer: (TransferItem) -> Unit = { notifyTransfer(transferStreams.stamp(it)) }
    private val closed = java.util.concurrent.atomic.AtomicBoolean()
    private var usbLiveness: UsbSessionLiveness? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val outbound = PriorityBlockingQueue<Outbound>()
    private val order = AtomicLong()
    private val incoming = ConcurrentHashMap<UUID, IncomingTransfer>()
    private val receiveDecisionLock = Any()
    private var receiveClosed = false
    private val finishing = ConcurrentHashMap<UUID, Job>()
    private val pendingConfirmations = ConcurrentHashMap<UUID, Pair<Job, TransferItem>>()
    private val pendingOffers = ConcurrentHashMap<UUID, PendingOffer>()
    private val outgoing = ConcurrentHashMap<UUID, OutgoingTransfer>()
    var onMtpChanged: () -> Unit = {}
    private val mtp = MtpFileChannel(context, scope, transferStreams,
        send = { enqueue(WireMessageType.MTP_CONTROL, 0, it).await() },
        queued = { offer -> TransferPauseController(onTransfer).also { it.report(incomingItem(offer, TransferStatus.QUEUED)) } },
        import = { id, input, key ->
            val transfer = incoming[id] ?: throw IOException("USB 文件尚未获得接收许可")
            val coroutine = currentCoroutineContext()
            var offset = 0L
            com.bluelink.core.MtpFileCipher.decrypt(input, id, transfer.offer.size, key, { bytes, length ->
                transfer.receiver.importChunk(offset, bytes, length); offset += length
            }, {
                coroutine.ensureActive()
                kotlinx.coroutines.runBlocking(coroutine) { transfer.progress.awaitResumed() }
            })
        },
        failure = { id, detail ->
            cancelIncoming(id, detail, TransferStatus.FAILED)
            enqueue(WireMessageType.TRANSFER_FAILED, 2, TransferWire.failure(FileTransferFailure(id, detail)))
        }, changed = { onMtpChanged() })
    var mtpEnabled: Boolean get() = mtp.enabled; set(value) { mtp.enabled = value }
    fun refreshMtp() = mtp.requestProbe()
    val mtpReady: Boolean get() = mtp.enabled && mtp.ready
    private lateinit var keys: SessionKeys
    private var negotiation = ProtocolGreeting.Negotiation(1, 0, BtxCapabilities.NONE)
    private var writer: Job? = null
    private var sendSequence = 0L
    @Volatile var maxReceiveBytes: Long = initialMaxReceiveBytes
        set(value) { field = value; reconsiderPendingOffers() }
    @Volatile var downloadDestination: String = initialDownloadDestination
    @Volatile var autoAcceptFiles: Boolean = initialAutoAcceptFiles
        set(value) { field = value; if (value) reconsiderPendingOffers() }
    @Volatile var duplicateFilePolicy = initialDuplicateFilePolicy
    @Volatile var autoSaveImages = initialAutoSaveImages
    @Volatile var autoSaveOtherAttachments = initialAutoSaveOtherAttachments
    @Volatile var largeFilesOnlyWhileCharging = initialLargeFilesOnlyWhileCharging
    private fun needsCharging(bytes: Long) = com.bluelink.android.domain.ReceivePolicy.needsCharging(bytes,
        largeFilesOnlyWhileCharging, context.getSystemService(android.os.BatteryManager::class.java).isCharging)
    private var remoteDeviceName: String = ""
    val hasPeerProvidedName: Boolean get() = remoteDeviceName.isNotBlank()
    val peerName: String get() = remoteDeviceName.ifBlank { preferredPeerName.ifBlank { connection.name } }

    suspend fun run() = withContext(Dispatchers.IO) {
        var stage = "安全握手"
        try {
            onDiagnostic(DiagnosticLevel.INFO, "Handshake",
                "安全握手开始（role=${if (listenerRole) "listener" else "dialer"}）")
            val handshake = SecureConnectionHandshake(connection.input, connection.output, { connection.close() },
                listenerRole, identityStore::captureIdentity, identityStore::trustedKey, expectedTrustedKeys,
                identityStore::beginTrustVerification, identityStore::completeTrustVerification,
                peerName, sessionId, transportAddress, platform,
                onSecurityRequest, onSecurityFinished, onPeerIdentified,
                localDeviceName = localDeviceName,
                findCandidate = findIdentityCandidate,
                commitAssociation = { verification, candidate ->
                    check(canAssociateIdentity(candidate)) { "原设备仍有活动会话，请断开后重试" }
                    identityStore.associateIdentity(verification, candidate)
                }, applyAssociation = applyIdentityAssociation).run()
            remoteDeviceName = handshake.remoteDeviceName
            keys = handshake.keys
            negotiation = handshake.negotiation
            mtp.allowAttemptSwitch = negotiation.supports(BtxCapabilities.TRANSFER_ATTEMPT_STREAMS)
            sendSequence = 1 // The handshake wrote exactly one encrypted PROTOCOL_HELLO.
            onDiagnostic(DiagnosticLevel.INFO, "Handshake", "安全握手密钥派生与信任检查完成")
            stage = "协议会话"
            writer = scope.launch(transferStreams.context()) {
                try {
                    writerLoop()
                } catch (failure: CancellationException) {
                    failPendingWrites(failure)
                } catch (failure: Throwable) {
                    val detail = failure.message?.takeIf { it.isNotBlank() }
                        ?: failure.javaClass.simpleName
                    onDiagnostic(DiagnosticLevel.ERROR, "Transport",
                        "BTX 写入循环已停止：${failure.javaClass.simpleName}: $detail")
                    failPendingWrites(failure)
                    onClosed("设备通道写入失败，连接已断开。")
                    close()
                }
            }
            onReady(keys.remotePeerId().hex())
            if (negotiation.supports(BtxCapabilities.MTP_FILES) && connection.transport == com.bluelink.android.domain.SessionTransport.BLUETOOTH) mtp.start()
            if (connection.transport == com.bluelink.android.domain.SessionTransport.USB) {
                val liveness = UsbSessionLiveness().also { usbLiveness = it }
                scope.launch(transferStreams.context()) {
                    liveness.run({ enqueue(WireMessageType.PING, 0, byteArrayOf()) }) { reason ->
                        onDiagnostic(DiagnosticLevel.WARNING, "USB", reason)
                        onClosed(reason)
                        close()
                    }
                }
            }
            readLoop(handshake.replay)
        } catch (failure: CancellationException) {
            throw failure
        } catch (failure: Throwable) {
            Log.e("BlueLinkSession", "$stage failed", failure)
            val detail = failure.message?.takeIf { it.isNotBlank() } ?: failure.javaClass.simpleName
            onDiagnostic(DiagnosticLevel.ERROR, "Session",
                "$stage 失败：${failure.javaClass.simpleName}: $detail")
            onClosed("$stage 失败：$detail")
        } finally {
            close()
        }
    }

    fun sendChat(text: String, messageId: UUID = UUID.randomUUID(), onSent: (Boolean) -> Unit) =
        sendChatInternal(text,messageId,true,onSent)
    fun sendSharedChat(text: String, messageId: UUID, onSent: (Boolean) -> Unit) =
        sendChatInternal(text,messageId,false,onSent)
    private fun sendChatInternal(text:String,messageId:UUID,trim:Boolean,onSent:(Boolean)->Unit) {
        if(text.isBlank() || !::keys.isInitialized) {onSent(false);return}
        ChatSubmission.start(scope,onSent) {
            val payload=ChatSubmission.payload(messageId,text,negotiation.supports(BtxCapabilities.STRUCTURED_MESSAGES),trim)
            enqueue(WireMessageType.CHAT,1,payload).await()
        }
    }

    internal var attempts = TransferAttemptRegistry()
    private val preparationGate = Any()
    private var preparationTail = CompletableDeferred(Unit)

    fun sendFile(uri: Uri, name: String, size: Long, originalId: UUID = UUID.randomUUID(), beforeQueue: suspend (TransferItem) -> Unit = {}, onPrepared: (TransferItem?) -> Unit = {}, onAnnounced: (Boolean) -> Unit = {}) {
        val announced = java.util.concurrent.atomic.AtomicBoolean()
        fun reportAnnounced(success: Boolean) { if (announced.compareAndSet(false, true)) onAnnounced(success) }
        val attempt = attempts.acquire(originalId) ?: run { onPrepared(null); reportAnnounced(false); return }
        val prepared = CompletableDeferred<Unit>()
        val previous = synchronized(preparationGate) { preparationTail.also { preparationTail = prepared } }
        PreparedSubmission.start(scope,beforeQueue,onPrepared) { reportPrepared ->
            val snapshots = mutableListOf<OutgoingSnapshot>()
            var queuedOriginal: TransferItem? = null
            var originalStarted = false
            try {
                previous.await()
                onDiagnostic(DiagnosticLevel.INFO, "Transfer", "正在创建稳定文件快照（id=${originalId.toString().take(8)}）")
                val original = createSnapshot(context.contentResolver, uri, originalId,
                    persistent = true, sourceName = name).also(snapshots::add)
                val stableOriginalUri = Uri.fromFile(original.file).toString()
                onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                    "文件快照完成（id=${originalId.toString().take(8)}，metadataBytes=${size.coerceAtLeast(0L)}，actualBytes=${original.size}）")
                val mimeType = context.contentResolver.getType(uri) ?: mimeTypeFor(name)
                val structured = negotiation.supports(BtxCapabilities.STRUCTURED_MESSAGES or BtxCapabilities.ATTACHMENT_METADATA)
                if (!structured) {
                    reportPrepared(TransferItem(originalId,name,original.size,outgoing=true,status=TransferStatus.QUEUED,
                        localUri=stableOriginalUri,mimeType=mimeType,sourceSha256=original.hash.hex(),attemptId=attempt.id))
                    transmitSnapshot(original, originalId, name, mimeType, null, null,
                        AttachmentRole.FILE, stableOriginalUri, onQueued = { prepared.complete(Unit) }, attempt = attempt)
                    reportAnnounced(true)
                    return@start
                }
                val messageId = UUID.randomUUID()
                val originalAttachmentId = UUID.randomUUID()
                val originalRole = if (mimeType.startsWith("image/")) AttachmentRole.IMAGE_ORIGINAL else AttachmentRole.FILE
                queuedOriginal = TransferItem(originalId, name, original.size, outgoing = true,
                    status = TransferStatus.QUEUED, messageId = messageId, attachmentId = originalAttachmentId,
                    mimeType = mimeType, localUri = stableOriginalUri, role = originalRole, sourceSha256 = original.hash.hex(), attemptId = attempt.id)
                attempt.publish(queuedOriginal, onTransfer)
                reportPrepared(requireNotNull(queuedOriginal))
                val descriptors = mutableListOf<AttachmentDescriptor>()
                var preview: ImagePreviewSnapshot? = null
                var previewId: UUID? = null
                var previewAttachmentId: UUID? = null
                if (originalRole == AttachmentRole.IMAGE_ORIGINAL) {
                    previewId = UUID.randomUUID()
                    previewAttachmentId = UUID.randomUUID()
                    try {
                        preview = createImagePreview(original.file, previewId).also { snapshots += it.snapshot }
                        descriptors += AttachmentDescriptor(previewAttachmentId, previewId, AttachmentRole.IMAGE_PREVIEW,
                            "${name.substringBeforeLast('.', name)}.preview${preview.extension}", preview.mimeType,
                            preview.snapshot.size, preview.snapshot.hash)
                    } catch (failure: CancellationException) { throw failure }
                    catch (failure: Throwable) {
                        onDiagnostic(DiagnosticLevel.WARNING, "Transfer",
                            "无法生成图片预览，将继续发送原图：${failure.message ?: failure.javaClass.simpleName}")
                        previewId = null
                        previewAttachmentId = null
                    }
                }
                descriptors += AttachmentDescriptor(originalAttachmentId, originalId, originalRole,
                    name, mimeType, original.size, original.hash)
                val envelope = ChatEnvelope(messageId,
                    if (originalRole == AttachmentRole.FILE) ChatPayloadKind.FILE else ChatPayloadKind.IMAGE,
                    System.currentTimeMillis(), "", descriptors)
                // Publish before writing: the reader can deliver a receipt before await resumes.
                onEnvelope(envelope, true)
                try {
                    enqueue(WireMessageType.CHAT, 1, MessagePayloadCodec.encode(envelope)).await()
                    onMessageStatus(messageId, MessageStatus.SENT)
                    reportAnnounced(true)
                } catch (failure: Throwable) {
                    onMessageStatus(messageId, MessageStatus.FAILED)
                    throw failure
                }
                if (preview != null && previewId != null && previewAttachmentId != null) {
                    try {
                        transmitSnapshot(preview.snapshot, previewId,
                            "${name.substringBeforeLast('.', name)}.preview${preview.extension}",
                            preview.mimeType, messageId, previewAttachmentId, AttachmentRole.IMAGE_PREVIEW,
                            Uri.fromFile(preview.snapshot.file).toString())
                    } catch (failure: CancellationException) { throw failure }
                    catch (failure: Throwable) {
                        onDiagnostic(DiagnosticLevel.WARNING, "Transfer",
                            "图片预览发送失败，将继续发送原图：${failure.message ?: failure.javaClass.simpleName}")
                    }
                }
                originalStarted = true
                transmitSnapshot(original, originalId, name, mimeType, messageId, originalAttachmentId,
                    originalRole, stableOriginalUri, onQueued = { prepared.complete(Unit) }, attempt = attempt)
            } catch (failure: Throwable) {
                val detail = failure.message?.takeIf { it.isNotBlank() } ?: failure.javaClass.simpleName
                if (!originalStarted) queuedOriginal?.let {
                    attempt.publish(it.copy(status = if (failure is CancellationException) TransferStatus.CANCELED
                        else TransferStatus.FAILED, failureDetail = detail), onTransfer)
                }
                onDiagnostic(if (failure is CancellationException) DiagnosticLevel.WARNING else DiagnosticLevel.ERROR,
                    "Transfer", "文件发送${if (failure is CancellationException) "已取消" else "失败"}：$detail")
            } finally {
                // Preserve submission order even if preparation is canceled before its predecessor finishes.
                if (previous.isCompleted) prepared.complete(Unit)
                else scope.launch(kotlinx.coroutines.NonCancellable + transferStreams.context()) { previous.join(); prepared.complete(Unit) }
                snapshots.forEach { snapshot -> snapshot.file.let { file ->
                    if (!snapshot.persistent) {
                        try { if (file.exists() && !file.delete()) onDiagnostic(DiagnosticLevel.WARNING, "Transfer", "发送缓存稍后由系统清理") }
                        finally { com.bluelink.android.files.OwnedTemporaryFiles.release(file) }
                    }
                } }
            }
        }.invokeOnCompletion { attempt.close(); reportAnnounced(false) }
    }

    // A supervisor shares ownership across reconnects until old workers finish cleanup.
    fun retryFile(uri: Uri, template: TransferItem, forceBluetooth: Boolean = false): Boolean {
        val attempt = attempts.acquire(template.id) ?: return false
        attempt.publish(template.copy(status = TransferStatus.QUEUED, failureDetail = null), onTransfer)
        scope.launch(transferStreams.context()) {
            var snapshot: OutgoingSnapshot? = null
            try {
                snapshot = createSnapshot(context.contentResolver, uri, template.id)
                TransferSourceIdentity.problem(template, snapshot.size, snapshot.hash)?.let {
                    throw IllegalStateException(context.getString(if (it == TransferSourceIdentity.Problem.MISSING)
                        com.bluelink.android.R.string.transfer_source_identity_missing else com.bluelink.android.R.string.transfer_source_identity_changed))
                }
                transmitSnapshot(snapshot, template.id, template.name, template.mimeType,
                    template.messageId, template.attachmentId, template.role, uri.toString(), attempt = attempt, forceBluetooth = forceBluetooth)
            } catch (failure: Throwable) {
                val detail = failure.message?.takeIf { it.isNotBlank() } ?: failure.javaClass.simpleName
                onDiagnostic(if (failure is CancellationException) DiagnosticLevel.WARNING else DiagnosticLevel.ERROR,
                    "Transfer", "文件重试${if (failure is CancellationException) "已取消" else "失败"}：$detail")
                attempt.publish(template.copy(status = if (failure is CancellationException) TransferStatus.CANCELED else TransferStatus.FAILED, failureDetail = detail), onTransfer)
            } finally {
                snapshot?.file?.let { file ->
                    try { if (file.exists() && !file.delete()) onDiagnostic(DiagnosticLevel.WARNING, "Transfer", "重试缓存稍后由系统清理") }
                    finally { com.bluelink.android.files.OwnedTemporaryFiles.release(file) }
                }
            }
        }.invokeOnCompletion { failure ->
            try {
                if (failure != null) attempt.publish(template.copy(status = TransferStatus.CANCELED,
                    failureDetail = failure.message), onTransfer)
            } finally { attempt.close() }
        }
        return true
    }

    private suspend fun transmitSnapshot(prepared: OutgoingSnapshot, id: UUID, name: String, mimeType: String,
                                         messageId: UUID?, attachmentId: UUID?, role: AttachmentRole,
                                         localUri: String, onQueued: () -> Unit = {}, attempt: TransferAttemptRegistry.Lease? = null, forceBluetooth: Boolean = false) {
        return withContext(transferStreams.context(transferStreams.startOutgoing(id,
            negotiation.supports(BtxCapabilities.TRANSFER_ATTEMPT_STREAMS), listenerRole))) {
        val ownedAttempt = if (attempt == null) checkNotNull(attempts.acquire(id)) else null
        val owner = attempt ?: requireNotNull(ownedAttempt)
        val state = OutgoingTransfer { owner.publish(it, onTransfer) }
        var committedBytes = 0L
        fun report(item: TransferItem) = state.progress.report(item.copy(sourceSha256 = prepared.hash.hex(), attemptId = owner.id))
        outgoing[id] = state
        try {
            val offer = if (messageId != null && attachmentId != null)
                FileOffer(id, name, prepared.size, FILE_EXTENT_SIZE, prepared.hash,
                    messageId, attachmentId, mimeType, role)
            else FileOffer(id, name, prepared.size, FILE_EXTENT_SIZE, prepared.hash)
            report(TransferItem(id, name, prepared.size, outgoing = true, status = TransferStatus.OFFERED,
                messageId = messageId, attachmentId = attachmentId, mimeType = mimeType,
                localUri = localUri, role = role))
            val useMtp = mtpReady && !forceBluetooth
            suspend fun sendCore() {
            report(requireNotNull(state.progress.item).copy(status = TransferStatus.OFFERED))
            enqueue(WireMessageType.TRANSFER_OFFER, 2, TransferWire.offer(offer)).await()
            val startIndex = state.accepted.await()
            committedBytes = minOf(prepared.size, startIndex.toLong() * FILE_EXTENT_SIZE)
            prepared.file.inputStream().buffered(FILE_EXTENT_SIZE).use { input ->
                if (!useMtp && committedBytes > 0) {
                    input.skipExactly(committedBytes)
                    report(TransferItem(id, name, prepared.size, committedBytes, true,
                        TransferStatus.RESUMING, messageId, attachmentId, mimeType, localUri, role = role))
                    onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                        "从持久化断点继续（id=${id.toString().take(8)}，extent=$startIndex，bytes=$committedBytes）")
                }
                if (useMtp) { mtp.sendBlob(id, input); committedBytes = prepared.size } else {
                var index = startIndex
                while (true) {
                    state.awaitResumed()
                    val data = input.readNBytes(FILE_EXTENT_SIZE)
                    if (data.isEmpty()) break
                    val acknowledgement = state.expectExtent(index)
                    enqueue(WireMessageType.TRANSFER_EXTENT, 2,
                        TransferWire.extent(FileExtent(id, index, TransferReceiver.sha256(data), data))).await()
                    acknowledgement.await(); state.removeExtent(index); index++
                    committedBytes += data.size
                    report(TransferItem(id, name, prepared.size, committedBytes, true,
                        TransferStatus.TRANSFERRING, messageId, attachmentId, mimeType, localUri, role = role))
                }
            }
            }
            check(committedBytes == prepared.size) { "快照读取长度发生变化：expected=${prepared.size}, actual=$committedBytes" }
            enqueue(WireMessageType.TRANSFER_FINISH, 2, TransferWire.id(id)).await()
            report(TransferItem(id, name, prepared.size, prepared.size, true, TransferStatus.VERIFYING,
                messageId, attachmentId, mimeType, localUri, role = role))
            state.completed.await()
            report(TransferItem(id, name, prepared.size, prepared.size, true, TransferStatus.COMPLETED,
                messageId, attachmentId, mimeType, localUri, role = role))
            }
            if (useMtp) mtp.runOutgoing(offer, state.progress, ::sendCore, onQueued, negotiation.supports(BtxCapabilities.TRANSFER_ATTEMPT_STREAMS)) else sendCore()
        } catch (failure: Throwable) {
            val canceled = failure is CancellationException && state.progress.item?.status != TransferStatus.FAILED
            val detail = failure.message?.takeIf { it.isNotBlank() } ?: failure.javaClass.simpleName
            report(TransferItem(id, name, prepared.size, committedBytes, true,
                if (canceled) TransferStatus.CANCELED else TransferStatus.FAILED,
                messageId, attachmentId, mimeType, localUri, failureDetail = detail, role = role))
            if (!canceled && failure !is RemoteTransferException && ::keys.isInitialized)
                runCatching { enqueue(WireMessageType.TRANSFER_FAILED, 2,
                    TransferWire.failure(FileTransferFailure(id, detail))).await() }
            throw failure
        } finally {
            outgoing.remove(id)
            state.fail(CancellationException("文件传输已结束"))
            ownedAttempt?.close()
        }
        }
    }

    fun switchQueuedToBluetooth(template: TransferItem): Boolean {
        if (!template.canSwitchToBluetooth || !negotiation.supports(BtxCapabilities.TRANSFER_ATTEMPT_STREAMS)) return false
        val uri=template.localUri?.let(Uri::parse) ?: return false
        val request=mtp.requestSwitch(template.id) ?: return false
        scope.launch(transferStreams.contextFor(template.id)) {
            try {
                request()
                attempts.awaitRelease(template.id)
                if (!retryFile(uri,template.copy(status=TransferStatus.CANCELED,queuedForUsb=false),forceBluetooth=true))
                    throw IOException("上次传输仍在结束，请稍后重试")
            } catch (error: Exception) {
                onDiagnostic(DiagnosticLevel.ERROR,"Transfer",error.message ?: "USB route change failed")
            }
        }
        return true
    }

    fun cancelTransfer(id: UUID, reason: String = "用户取消") {
        val captured = transferStreams.contextFor(id)
        mtp.cancel(id)
        synchronized(receiveDecisionLock) {
            pendingConfirmations.remove(id)?.let { (job, item) ->
                job.cancel(); onTransfer(item.copy(status = TransferStatus.CANCELED, failureDetail = reason))
            }
        }
        scope.launch(captured) {
            outgoing[id]?.fail(CancellationException(reason))
            pendingOffers.remove(id)?.let { pending ->
                onTransfer(pending.item.copy(status = TransferStatus.CANCELED, failureDetail = reason))
            }
            cancelIncoming(id, reason, TransferStatus.CANCELED)
            if (negotiation.supports(BtxCapabilities.TRANSFER_CONTROL)) {
                enqueue(WireMessageType.TRANSFER_CONTROL, 0,
                    TransferControlCodec.encode(TransferControl(id, TransferControlAction.CANCEL,
                        System.currentTimeMillis(), reason))).await()
            } else enqueue(WireMessageType.TRANSFER_FAILED, 2,
                TransferWire.failure(FileTransferFailure(id, reason))).await()
        }
    }

    fun pauseTransfer(id: UUID) = changeLocalPause(id, true)

    fun resumeTransfer(id: UUID) = changeLocalPause(id, false)

    private fun changeLocalPause(id: UUID, paused: Boolean) {
        scope.launch(transferStreams.contextFor(id)) {
            val progress = outgoing[id]?.progress ?: incoming[id]?.progress ?: mtp.progress(id) ?: return@launch
            if (!progress.setPaused(local = true, paused = paused)) return@launch
            if (negotiation.supports(BtxCapabilities.TRANSFER_CONTROL))
                enqueue(WireMessageType.TRANSFER_CONTROL, 0,
                    TransferControlCodec.encode(TransferControl(id,
                        if (paused) TransferControlAction.PAUSE else TransferControlAction.RESUME,
                        System.currentTimeMillis(), if (paused) "用户暂停" else "用户继续"))).await()
        }
    }

    fun close() {
        if (!closed.compareAndSet(false, true)) return
        onClosed("设备会话已结束")
        mtp.close()
        val receivers = synchronized(receiveDecisionLock) {
            receiveClosed = true
            pendingConfirmations.values.forEach { it.first.cancel() }
            pendingConfirmations.clear()
            pendingOffers.clear()
            finishing.values.forEach { it.cancel() }
            finishing.clear()
            incoming.values.toList().also { values ->
                values.forEach { it.publication.cancel() }
                incoming.clear()
            }
        }
        scope.launch(kotlinx.coroutines.NonCancellable + transferStreams.context()) { receivers.forEach { runCatching { it.receiver.close() } } }

        val stopped = CancellationException("设备会话已结束")
        outgoing.values.forEach { it.fail(stopped) }
        outgoing.clear()
        failPendingWrites(stopped)
        runCatching { connection.close() }
        scope.cancel()
    }

    private suspend fun writerLoop() {
        while (currentCoroutineContext().isActive) {
            val item = outbound.poll(500, TimeUnit.MILLISECONDS) ?: continue
            try {
                val frame = BtxFrame(item.type, 0, item.streamId, sendSequence++, item.payload)
                BtxRecordCodec.write(connection.output, frame, keys.sendKey(), keys.sendNoncePrefix())
                item.done.complete(Unit)
            } catch (failure: Throwable) {
                item.done.completeExceptionally(failure)
                runCatching { connection.close() }
                throw failure
            }
        }
    }

    private fun failPendingWrites(failure: Throwable) {
        while (true) {
            val pending = outbound.poll() ?: break
            pending.done.completeExceptionally(failure)
        }
    }

    private fun enqueue(type: WireMessageType, streamId: Int, payload: ByteArray): CompletableDeferred<Unit> {
        val result = CompletableDeferred<Unit>()
        synchronized(receiveDecisionLock) {
            if (closed.get()) result.completeExceptionally(CancellationException("设备会话已结束"))
            else {
                val route = transferRouting(type, payload)
                val stream = if (route == null) streamId else transferStreams.streamFor(route.first, streamId,
                    negotiation.supports(BtxCapabilities.TRANSFER_ATTEMPT_STREAMS))
                outbound.put(Outbound(type.priority(), order.getAndIncrement(), type, stream, payload, result))
            }
        }
        return result
    }

    private suspend fun readLoop(replay: ReplayGuard) {
        while (true) {
            val frame = BtxRecordCodec.read(connection.input, keys.receiveKey(), keys.receiveNoncePrefix(), replay)
            usbLiveness?.received()
            val route = transferRouting(frame.type(), frame.payload())
            val binding = route?.let { (id, starts) -> transferStreams.receive(id, frame.streamId(), starts,
                incoming.containsKey(id) || outgoing.containsKey(id) || pendingOffers.containsKey(id) || pendingConfirmations.containsKey(id) || mtp.progress(id) != null,
                negotiation.supports(BtxCapabilities.TRANSFER_ATTEMPT_STREAMS), listenerRole) }
            if (route != null && binding == null) continue
            withContext(transferStreams.context(binding)) { dispatchTransferFrame(frame) }
        }
    }

    private fun transferRouting(type: WireMessageType, payload: ByteArray): Pair<UUID, Boolean>? = when (type) {
        WireMessageType.TRANSFER_OFFER -> TransferWire.readOffer(payload).id to true
        WireMessageType.TRANSFER_CONTROL -> TransferControlCodec.decode(payload).transferId() to false
        WireMessageType.MTP_CONTROL -> org.json.JSONObject(String(payload, Charsets.UTF_8)).let { packet ->
            if (!packet.has("id") || packet.isNull("id")) null else UUID.fromString(packet.getString("id")) to (packet.getString("op") == "queue") }
        WireMessageType.TRANSFER_ACCEPT, WireMessageType.TRANSFER_EXTENT, WireMessageType.TRANSFER_FINISH,
        WireMessageType.TRANSFER_EXTENT_ACK, WireMessageType.TRANSFER_COMPLETE, WireMessageType.TRANSFER_REJECT,
        WireMessageType.TRANSFER_FAILED -> TransferWire.readId(payload) to false
        else -> null
    }

    private suspend fun dispatchTransferFrame(frame: BtxFrame) {
            when (frame.type()) {
                WireMessageType.CHAT -> receiveChat(frame.payload())
                WireMessageType.CHAT_RECEIPT -> if (negotiation.supports(BtxCapabilities.MESSAGE_RECEIPTS))
                    onReceipt(MessagePayloadCodec.decodeReceipt(frame.payload()))
                WireMessageType.PING -> enqueue(WireMessageType.PONG, 0, frame.payload())
                WireMessageType.GOAWAY -> { onClosed("对端已结束连接。"); throw CancellationException("对端已结束连接。") }
                WireMessageType.TRANSFER_OFFER -> receiveOffer(TransferWire.readOffer(frame.payload()))
                WireMessageType.TRANSFER_ACCEPT -> {
                    val accept = TransferWire.readAccept(frame.payload())
                    val transfer = outgoing[accept.id]
                    if (transfer == null) {
                        onDiagnostic(DiagnosticLevel.WARNING, "Transfer",
                            "收到未知文件的 Accept（id=${accept.id.toString().take(8)}）")
                    } else {
                        transfer.accepted.complete(accept.nextExtent)
                    }
                }
                WireMessageType.TRANSFER_EXTENT -> receiveExtent(TransferWire.readExtent(frame.payload()))
                WireMessageType.TRANSFER_FINISH -> beginFinalization(TransferWire.readId(frame.payload()))
                WireMessageType.TRANSFER_EXTENT_ACK -> {
                    val acknowledgement = TransferWire.readExtentAck(frame.payload())
                    outgoing[acknowledgement.id]?.acknowledgeExtent(acknowledgement.index)
                }
                WireMessageType.TRANSFER_COMPLETE -> {
                    outgoing[TransferWire.readId(frame.payload())]?.completed?.complete(Unit)
                }
                WireMessageType.TRANSFER_REJECT -> handleTransferFailure(
                    FileTransferFailure(TransferWire.readId(frame.payload()), "对端拒绝了文件传输"))
                WireMessageType.TRANSFER_FAILED -> handleTransferFailure(TransferWire.readFailure(frame.payload()))
                WireMessageType.TRANSFER_CONTROL -> handleTransferControl(TransferControlCodec.decode(frame.payload()))
                WireMessageType.MTP_CONTROL -> {
                    require(negotiation.supports(BtxCapabilities.MTP_FILES))
                    mtp.receive(frame.payload())
                }
                else -> Unit
            }
    }

    private fun receiveChat(payload: ByteArray) {
        if (negotiation.supports(BtxCapabilities.STRUCTURED_MESSAGES) && MessagePayloadCodec.isStructured(payload)) {
            val envelope = MessagePayloadCodec.decode(payload)
            onEnvelope(envelope, false)
            if (envelope.kind() == ChatPayloadKind.TEXT || envelope.kind() == ChatPayloadKind.SYSTEM || envelope.body().isNotBlank()) {
                onMessage(ChatItem(id = envelope.messageId(), text = envelope.body(), outgoing = false,
                    timestamp = Instant.ofEpochMilli(envelope.createdAt()), status = MessageStatus.RECEIVED))
            }
            if (negotiation.supports(BtxCapabilities.MESSAGE_RECEIPTS)) {
                enqueue(WireMessageType.CHAT_RECEIPT, 1,
                    MessagePayloadCodec.encodeReceipt(ChatReceipt(envelope.messageId(), ReceiptState.DELIVERED,
                        System.currentTimeMillis())))
            }
            return
        }
        onMessage(ChatItem(text = payload.toString(Charsets.UTF_8), outgoing = false, status = MessageStatus.RECEIVED))
    }

    private fun handleTransferControl(control: TransferControl) {
        if (!negotiation.supports(BtxCapabilities.TRANSFER_CONTROL))
            throw IOException("对端发送了未协商的传输控制消息")
        if (control.action() == TransferControlAction.PAUSE || control.action() == TransferControlAction.RESUME) {
            val progress = outgoing[control.transferId()]?.progress ?: incoming[control.transferId()]?.progress ?: mtp.progress(control.transferId())
            progress?.setPaused(local = false, paused = control.action() == TransferControlAction.PAUSE)
            onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                "收到对端传输控制（id=${control.transferId().toString().take(8)}，action=${control.action()}）")
            return
        }
        if (control.action() != TransferControlAction.CANCEL) {
            onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                "已收到暂不支持的控制指令（id=${control.transferId().toString().take(8)}，action=${control.action()}）")
            return
        }
        synchronized(receiveDecisionLock) {
            pendingConfirmations.remove(control.transferId())?.let { (job, item) ->
                job.cancel(); onTransfer(item.copy(status = TransferStatus.CANCELED, failureDetail = control.reason()))
            }
        }
        pendingOffers.remove(control.transferId())?.let { onTransfer(it.item.copy(status = TransferStatus.CANCELED, failureDetail = control.reason())) }
        outgoing[control.transferId()]?.fail(CancellationException(control.reason().ifBlank { "对端取消传输" }))
        mtp.cancel(control.transferId())
        cancelIncoming(control.transferId(), control.reason(), TransferStatus.CANCELED)
        onDiagnostic(DiagnosticLevel.WARNING, "Transfer",
            "对端取消文件传输（id=${control.transferId().toString().take(8)}）")
    }

    private fun receiveOffer(offer: FileOffer) {
        mtp.validateOffer(offer)
        synchronized(receiveDecisionLock) {
        if (receiveClosed) return
        try {
            if (incoming.containsKey(offer.id) || pendingOffers.containsKey(offer.id) || pendingConfirmations.containsKey(offer.id))
                throw IOException("重复文件 Offer")
            if (needsCharging(offer.size)) throw IOException("请连接充电器后再接收此大文件")
            if (offer.size > maxReceiveBytes) {
                val item = incomingItem(offer, TransferStatus.OFFERED).copy(
                    failureDetail = "超过当前接收限制（$maxReceiveBytes B）；30 秒内提高限制可继续接收")
                pendingOffers[offer.id] = PendingOffer(offer, item, System.currentTimeMillis() + OFFER_TIMEOUT_MS)
                onTransfer(item)
                onDiagnostic(DiagnosticLevel.WARNING, "Transfer",
                    "暂缓超限 Offer（id=${offer.id.toString().take(8)}，bytes=${offer.size}，limit=$maxReceiveBytes）")
                scope.launch(transferStreams.context()) {
                    delay(OFFER_TIMEOUT_MS)
                    expirePendingOffer(offer.id)
                }
                return
            }
            if (!autoAcceptFiles && offer.role != AttachmentRole.IMAGE_PREVIEW) {
                val item = incomingItem(offer, TransferStatus.OFFERED)
                onTransfer(item)
                val job = scope.launch(transferStreams.context(), start = kotlinx.coroutines.CoroutineStart.LAZY) {
                    try {
                        val accepted = kotlinx.coroutines.withTimeoutOrNull(OFFER_TIMEOUT_MS) { onReceiveConfirmation(item) } ?: false
                        if (!accepted) throw IOException("未确认接收文件")
                        if (offer.size > maxReceiveBytes) throw IOException("文件超过当前接收大小限制")
                        if (needsCharging(offer.size)) throw IOException("请连接充电器后再接收此大文件")
                        val decisionContext = currentCoroutineContext()
                        synchronized(receiveDecisionLock) {
                            decisionContext.ensureActive()
                            if (!receiveClosed && pendingConfirmations[offer.id]?.first === decisionContext[Job]) acceptOffer(offer)
                        }
                    } catch (failure: CancellationException) {
                        throw failure
                    } catch (failure: Exception) {
                        val detail = failure.message ?: "接收未完成"
                        val decisionContext = currentCoroutineContext()
                        synchronized(receiveDecisionLock) {
                            decisionContext.ensureActive()
                            if (!receiveClosed && pendingConfirmations[offer.id]?.first === decisionContext[Job]) {
                                onTransfer(item.copy(status = TransferStatus.REJECTED, failureDetail = detail))
                                enqueue(WireMessageType.TRANSFER_FAILED, 2, TransferWire.failure(FileTransferFailure(offer.id, detail)))
                            }
                        }
                    } finally {
                        val ownJob = currentCoroutineContext()[Job]
                        pendingConfirmations.computeIfPresent(offer.id) { _, pending -> if (pending.first === ownJob) null else pending }
                    }
                }
                synchronized(receiveDecisionLock) {
                    if (receiveClosed) { job.cancel(); return }
                    pendingConfirmations[offer.id] = job to item
                    job.start()
                }
                return
            }
            synchronized(receiveDecisionLock) { if (!receiveClosed) acceptOffer(offer) }
        } catch (failure: Throwable) {
            val detail = failure.message ?: failure.javaClass.simpleName
            onTransfer(TransferItem(offer.id,
                offer.name.substringAfterLast('/').substringAfterLast('\\').ifBlank { "received.bin" },
                offer.size, outgoing = false, status = TransferStatus.FAILED,
                messageId = offer.messageId, attachmentId = offer.attachmentId,
                mimeType = offer.mimeType, failureDetail = detail, role = offer.role))
            onDiagnostic(DiagnosticLevel.ERROR, "Transfer",
                "拒绝文件 Offer（id=${offer.id.toString().take(8)}）：$detail")
            enqueue(WireMessageType.TRANSFER_FAILED, 2,
                TransferWire.failure(FileTransferFailure(offer.id, detail)))
        }
    }
        }

    private fun incomingItem(offer: FileOffer, status: TransferStatus) = TransferItem(
        offer.id,
        offer.name.substringAfterLast('/').substringAfterLast('\\').ifBlank { "received.bin" },
        offer.size,
        outgoing = false,
        status = status,
        messageId = offer.messageId,
        attachmentId = offer.attachmentId,
        mimeType = offer.mimeType,
        role = offer.role,
    )

    private fun acceptOffer(offer: FileOffer, existing: TransferItem? = null) {
        val safeName = offer.name.substringAfterLast('/').substringAfterLast('\\').ifBlank { "received.bin" }
        val receiver = TransferReceiver(context.filesDir.resolve("received").resolve(offer.id.toString()).toPath(), safeName,
            offer.size, offer.extentSize, offer.hash, null, offer.id.toString().replace("-", ""))
        val transfer = IncomingTransfer(offer, safeName, receiver, mtp.progress(offer.id) ?: TransferPauseController(onTransfer))
        incoming[offer.id] = transfer
        transfer.progress.report((existing ?: incomingItem(offer, TransferStatus.OFFERED)).copy(
            status = TransferStatus.OFFERED, failureDetail = null))
        val nextExtent = (receiver.contiguousCommittedOffset() / offer.extentSize).toInt()
        enqueue(WireMessageType.TRANSFER_ACCEPT, 2,
            if (negotiation.supports(BtxCapabilities.RESUME_STATE))
                TransferWire.accept(FileTransferAccept(offer.id, nextExtent))
            else TransferWire.id(offer.id))
        onDiagnostic(DiagnosticLevel.INFO, "Transfer",
            "已接受文件 Offer（id=${offer.id.toString().take(8)}，bytes=${offer.size}，block=${offer.extentSize}）")
    }

    private fun reconsiderPendingOffers() {
        scope.launch(transferStreams.context()) {
            pendingOffers.entries.toList().forEach { (id, pending) ->
                if (pending.offer.size > maxReceiveBytes || needsCharging(pending.offer.size) || pending.expiresAt <= System.currentTimeMillis()) return@forEach
                if (!pendingOffers.remove(id, pending)) return@forEach
                runCatching { synchronized(receiveDecisionLock) { if (!receiveClosed) receiveOffer(pending.offer) } }
                    .onFailure { failure ->
                        val detail = failure.message ?: failure.javaClass.simpleName
                        onTransfer(pending.item.copy(status = TransferStatus.FAILED, failureDetail = detail))
                        enqueue(WireMessageType.TRANSFER_FAILED, 2,
                            TransferWire.failure(FileTransferFailure(id, detail)))
                    }
            }
        }
    }

    private fun expirePendingOffer(id: UUID) {
        val pending = pendingOffers.remove(id) ?: return
        val detail = "文件 Offer 已过期，请对端重新发送"
        onTransfer(pending.item.copy(status = TransferStatus.FAILED, failureDetail = detail))
        enqueue(WireMessageType.TRANSFER_FAILED, 2,
            TransferWire.failure(FileTransferFailure(id, detail)))
    }

    private fun receiveExtent(extent: FileExtent) {
        val transfer = incoming[extent.id]
        if (transfer == null) {
            onDiagnostic(DiagnosticLevel.WARNING, "Transfer",
                "忽略未知文件的数据块（id=${extent.id.toString().take(8)}，index=${extent.index}）")
            enqueue(WireMessageType.TRANSFER_FAILED, 2,
                TransferWire.failure(FileTransferFailure(extent.id, "未知文件传输")))
            return
        }
        try {
            transfer.receiver.accept(extent.index, extent.data, extent.hash)
            val completed = transfer.receiver.contiguousCommittedOffset()
            transfer.progress.report(TransferItem(extent.id, transfer.name, transfer.offer.size, completed, false,
                TransferStatus.TRANSFERRING,
                transfer.offer.messageId, transfer.offer.attachmentId, transfer.offer.mimeType,
                role = transfer.offer.role))
            enqueue(WireMessageType.TRANSFER_EXTENT_ACK, 2,
                TransferWire.extentAck(FileExtentAck(extent.id, extent.index)))
        } catch (failure: Throwable) {
            incoming.remove(extent.id)

            val committed = runCatching { transfer.receiver.contiguousCommittedOffset() }.getOrDefault(0L)
            runCatching { transfer.receiver.close() }
            transfer.progress.report(TransferItem(extent.id, transfer.name, transfer.offer.size, committed,
                outgoing = false, status = TransferStatus.FAILED, messageId = transfer.offer.messageId,
                attachmentId = transfer.offer.attachmentId, mimeType = transfer.offer.mimeType,
                role = transfer.offer.role, failureDetail = failure.message ?: "文件保存未完成"))
            val detail = failure.message ?: failure.javaClass.simpleName
            onDiagnostic(DiagnosticLevel.ERROR, "Transfer",
                "接收文件块失败（id=${extent.id.toString().take(8)}，index=${extent.index}）：$detail")
            enqueue(WireMessageType.TRANSFER_FAILED, 2,
                TransferWire.failure(FileTransferFailure(extent.id, detail)))
        }
    }

    private fun cancelIncoming(id: UUID, reason: String, status: TransferStatus) {
        val transfer = synchronized(receiveDecisionLock) {
            val value = incoming[id] ?: return
            if (!value.publication.cancel()) return // A completed commit wins over a late cancel.
            incoming.remove(id, value)
            finishing.remove(id)?.cancel()

            value
        }
        val committed = runCatching { transfer.receiver.contiguousCommittedOffset() }.getOrDefault(0L)
        transfer.progress.report(TransferItem(id, transfer.name, transfer.offer.size, committed, false, status,
            transfer.offer.messageId, transfer.offer.attachmentId, transfer.offer.mimeType,
            role = transfer.offer.role, failureDetail = reason))
        scope.launch(kotlinx.coroutines.NonCancellable + transferStreams.context()) { runCatching { transfer.receiver.close() } }
    }

    private fun beginFinalization(id: UUID): Unit = synchronized(receiveDecisionLock) {
        if (receiveClosed || finishing.containsKey(id)) return
        val transfer = incoming[id]
        if (transfer == null) {
            enqueue(WireMessageType.TRANSFER_FAILED, 2, TransferWire.failure(FileTransferFailure(id, "未知文件传输")))
            return
        }
        val job = scope.launch(transferStreams.context(), start = kotlinx.coroutines.CoroutineStart.LAZY) {
            try { finishTransfer(id, transfer) }
            finally { finishing.remove(id, currentCoroutineContext()[Job]) }
        }
        finishing[id] = job
        job.start()
        Unit
    }

    private suspend fun finishTransfer(id: UUID, transfer: IncomingTransfer) {
        try {
            transfer.publication.active {
                transfer.progress.report(TransferItem(id, transfer.name, transfer.offer.size, transfer.offer.size, false, TransferStatus.VERIFYING,
                    transfer.offer.messageId, transfer.offer.attachmentId, transfer.offer.mimeType, role = transfer.offer.role))
            }
            val target = transfer.receiver.finish()
            currentCoroutineContext().ensureActive()
            transfer.publication.checkpoint()
            transfer.publication.active {
                transfer.progress.report(incomingItem(transfer.offer, TransferStatus.COMMITTING).copy(completedBytes = transfer.offer.size))
            }
            val published = if (!com.bluelink.android.domain.ReceivePolicy.saveToDirectory(transfer.offer.role,
                transfer.offer.mimeType, autoSaveImages, autoSaveOtherAttachments)) transfer.publication.commit { Uri.fromFile(target.toFile()) }
                else ReceivedFileStore.publish(context, target, transfer.name, transfer.offer.mimeType,
                    downloadDestination, duplicateFilePolicy, onFileConflict, transfer.publication)
            incoming.remove(id)

            transfer.progress.report(TransferItem(id, transfer.name, transfer.offer.size, transfer.offer.size, false, TransferStatus.COMPLETED,
                transfer.offer.messageId, transfer.offer.attachmentId, transfer.offer.mimeType,
                published.toString(), role = transfer.offer.role))
            onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                "文件接收与校验完成（id=${id.toString().take(8)}，bytes=${transfer.offer.size}）")
            enqueue(WireMessageType.TRANSFER_COMPLETE, 2, TransferWire.id(id))
        } catch (canceled: CancellationException) {
            if (incoming[id] === transfer) cancelIncoming(id, "接收已取消", TransferStatus.CANCELED)
            if (currentCoroutineContext().isActive) {
                if (negotiation.supports(BtxCapabilities.TRANSFER_CONTROL)) enqueue(WireMessageType.TRANSFER_CONTROL, 0,
                    TransferControlCodec.encode(TransferControl(id, TransferControlAction.CANCEL, System.currentTimeMillis(), "接收已取消")))
                else enqueue(WireMessageType.TRANSFER_FAILED, 2, TransferWire.failure(FileTransferFailure(id, "接收已取消")))
            }
            throw canceled
        } catch (failure: Exception) {
            if (!incoming.remove(id, transfer)) return

            val committed = runCatching { transfer.receiver.contiguousCommittedOffset() }.getOrDefault(0L)
            runCatching { transfer.receiver.close() }
            transfer.progress.report(TransferItem(id, transfer.name, transfer.offer.size, committed,
                outgoing = false, status = TransferStatus.FAILED, messageId = transfer.offer.messageId,
                attachmentId = transfer.offer.attachmentId, mimeType = transfer.offer.mimeType,
                role = transfer.offer.role, failureDetail = failure.message ?: "文件保存未完成"))
            val detail = failure.message ?: failure.javaClass.simpleName
            onDiagnostic(DiagnosticLevel.ERROR, "Transfer",
                "文件校验或保存失败（id=${id.toString().take(8)}）：$detail")
            enqueue(WireMessageType.TRANSFER_FAILED, 2,
                TransferWire.failure(FileTransferFailure(id, detail)))
        } finally {
            transfer.receiver.close()
        }
    }

    private fun handleTransferFailure(failure: FileTransferFailure) {
        mtp.cancel(failure.id, TransferStatus.FAILED)
        val exception = RemoteTransferException(if (failure.reason.isBlank()) "对端报告文件传输失败"
            else "对端报告文件传输失败：${failure.reason}")
        outgoing[failure.id]?.fail(exception)
        cancelIncoming(failure.id, failure.reason, TransferStatus.FAILED)
        onDiagnostic(DiagnosticLevel.ERROR, "Transfer",
            "收到对端失败通知（id=${failure.id.toString().take(8)}）：${failure.reason}")
    }

    private suspend fun createSnapshot(resolver: ContentResolver, uri: Uri, id: UUID,
                                       persistent: Boolean = false, sourceName: String = ""): OutgoingSnapshot =
        withContext(Dispatchers.IO) {
            val directory = if (persistent) context.filesDir.resolve("attachments/sent")
                else context.cacheDir.resolve("outgoing")
            directory.mkdirs()
            val suffix = sourceName.substringAfterLast('.', "").takeIf {
                it.length in 1..12 && it.all(Char::isLetterOrDigit)
            }?.let { ".${it.lowercase()}" }.orEmpty()
            val file = directory.resolve(if (persistent) "$id$suffix" else "${UUID.randomUUID()}.part")
            if (!persistent) com.bluelink.android.files.OwnedTemporaryFiles.register(file,id)
            try {
                val digest = MessageDigest.getInstance("SHA-256")
                var size = 0L
                resolver.openInputStream(uri).use { input ->
                    requireNotNull(input) { "无法读取所选文件" }
                    file.outputStream().buffered(128 * 1024).use { output ->
                        val buffer = ByteArray(128 * 1024)
                        while (true) {
                            val read = input.read(buffer)
                            if (read < 0) break
                            if (read == 0) continue
                            output.write(buffer, 0, read)
                            digest.update(buffer, 0, read)
                            size = Math.addExact(size, read.toLong())
                        }
                    }
                }
                OutgoingSnapshot(file, size, digest.digest(), persistent)
            } catch (failure: Throwable) {
                runCatching { file.delete() }
                if (!persistent) com.bluelink.android.files.OwnedTemporaryFiles.release(file)
                throw failure
            }
        }

    private suspend fun createImagePreview(source: File, id: UUID): ImagePreviewSnapshot = withContext(Dispatchers.IO) {
        val directory = context.filesDir.resolve("attachments/previews").apply { mkdirs() }
        var target: File? = null
        try {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeFile(source.absolutePath, bounds)
            var sample = 1
            while (bounds.outWidth / sample > 2560 || bounds.outHeight / sample > 2560) sample *= 2
            val decoded = BitmapFactory.decodeFile(source.absolutePath,
                BitmapFactory.Options().apply { inSampleSize = sample }) ?: throw IOException("无法解码图片预览")
            val preserveAlpha = decoded.hasAlpha()
            val extension = if (preserveAlpha) ".png" else ".jpg"
            val mimeType = if (preserveAlpha) "image/png" else "image/jpeg"
            target = directory.resolve("${id}.preview$extension")
            var maxDimension = 1280
            var quality = 84
            val output = ByteArrayOutputStream()
            var encoded: ByteArray
            while (true) {
                val scale = minOf(1f, maxDimension.toFloat() / maxOf(decoded.width, decoded.height))
                val width = maxOf(1, (decoded.width * scale).toInt())
                val height = maxOf(1, (decoded.height * scale).toInt())
                val scaled = if (width == decoded.width && height == decoded.height) decoded
                    else Bitmap.createScaledBitmap(decoded, width, height, true)
                output.reset()
                val format = if (preserveAlpha) Bitmap.CompressFormat.PNG else Bitmap.CompressFormat.JPEG
                check(scaled.compress(format, quality, output)) { "无法压缩图片预览" }
                encoded = output.toByteArray()
                if (scaled !== decoded) scaled.recycle()
                val reachedMinimum = if (preserveAlpha) maxDimension <= 320 else maxDimension <= 640 && quality <= 52
                if (encoded.size <= 300 * 1024 || reachedMinimum) break
                if (preserveAlpha) maxDimension = maxOf(320, (maxDimension * .76f).toInt())
                else if (quality > 56) quality -= 12 else {
                    maxDimension = maxOf(640, maxDimension - 240)
                    quality = 76
                }
            }
            decoded.recycle()
            target.writeBytes(encoded)
            ImagePreviewSnapshot(OutgoingSnapshot(target, target.length(),
                MessageDigest.getInstance("SHA-256").digest(encoded), persistent = true), extension, mimeType)
        } catch (failure: Throwable) {
            target?.let { runCatching { it.delete() } }
            throw failure
        }
    }

    private fun mimeTypeFor(name: String): String = when (name.substringAfterLast('.', "").lowercase()) {
        "jpg", "jpeg" -> "image/jpeg"
        "png" -> "image/png"
        "gif" -> "image/gif"
        "webp" -> "image/webp"
        "pdf" -> "application/pdf"
        "txt" -> "text/plain"
        "json" -> "application/json"
        "zip" -> "application/zip"
        else -> "application/octet-stream"
    }

    private data class IncomingTransfer(val offer: FileOffer, val name: String, val receiver: TransferReceiver,
        val progress: TransferPauseController,
        val publication: com.bluelink.android.files.PublicationGuard = com.bluelink.android.files.PublicationGuard())
    private data class PendingOffer(val offer: FileOffer, val item: TransferItem, val expiresAt: Long)
    private data class OutgoingSnapshot(
        val file: File,
        val size: Long,
        val hash: ByteArray,
        val persistent: Boolean = false,
    )
    private data class ImagePreviewSnapshot(
        val snapshot: OutgoingSnapshot,
        val extension: String,
        val mimeType: String,
    )
    private class RemoteTransferException(message: String) : IOException(message)
    private class OutgoingTransfer(notify: (TransferItem) -> Unit) {
        val accepted = CompletableDeferred<Int>()
        val completed = CompletableDeferred<Unit>()
        private val acknowledgements = ConcurrentHashMap<Int, CompletableDeferred<Unit>>()
        val progress = TransferPauseController(notify)
        suspend fun awaitResumed() = progress.awaitResumed()

        fun expectExtent(index: Int): CompletableDeferred<Unit> {
            val acknowledgement = CompletableDeferred<Unit>()
            check(acknowledgements.putIfAbsent(index, acknowledgement) == null) {
                "数据块 $index 已在等待确认"
            }
            return acknowledgement
        }

        fun acknowledgeExtent(index: Int) {
            acknowledgements[index]?.complete(Unit)
        }

        fun removeExtent(index: Int) {
            acknowledgements.remove(index)
        }

        fun fail(failure: Throwable) {
            progress.fail(failure)
            accepted.completeExceptionally(failure)
            completed.completeExceptionally(failure)
            acknowledgements.values.forEach { it.completeExceptionally(failure) }
        }
    }
    private data class Outbound(
        val priority: Int, val order: Long, val type: WireMessageType, val streamId: Int,
        val payload: ByteArray, val done: CompletableDeferred<Unit>,
    ) : Comparable<Outbound> {
        override fun compareTo(other: Outbound): Int = compareValuesBy(this, other, Outbound::priority, Outbound::order)
    }
}

private fun InputStream.skipExactly(byteCount: Long) {
    var remaining = byteCount
    while (remaining > 0) {
        val skipped = skip(remaining)
        if (skipped > 0) {
            remaining -= skipped
            continue
        }
        if (read() < 0) throw IOException("快照长度小于断点位置：remaining=$remaining")
        remaining--
    }
}
