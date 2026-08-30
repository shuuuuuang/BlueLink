package com.bluelink.android.session

import android.bluetooth.BluetoothSocket
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
    private val socket: BluetoothSocket,
    private val listenerRole: Boolean,
    private val identityStore: IdentityStore,
    private val onTrustRequired: suspend (peerId: String, publicKey: ByteArray, safetyCode: String) -> Boolean,
    private val onMessage: (ChatItem) -> Unit,
    private val onTransfer: (TransferItem) -> Unit,
    private val onReady: (String) -> Unit,
    private val onClosed: (String) -> Unit,
    private val onDiagnostic: (DiagnosticLevel, String, String) -> Unit = { _, _, _ -> },
    private val onEnvelope: (ChatEnvelope, Boolean) -> Unit = { _, _ -> },
    private val onReceipt: (ChatReceipt) -> Unit = {},
    initialMaxReceiveBytes: Long = Long.MAX_VALUE,
    initialDownloadDestination: String = "downloads://BlueLink",
    initialAutoAcceptFiles: Boolean = true,
) {
    companion object {
        private const val FILE_EXTENT_SIZE = 64 * 1024
        private const val OFFER_TIMEOUT_MS = 30_000L
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val outbound = PriorityBlockingQueue<Outbound>()
    private val order = AtomicLong()
    private val incoming = mutableMapOf<UUID, IncomingTransfer>()
    private val pendingOffers = ConcurrentHashMap<UUID, PendingOffer>()
    private val outgoing = ConcurrentHashMap<UUID, OutgoingTransfer>()
    private val pausedIncoming = ConcurrentHashMap.newKeySet<UUID>()
    private lateinit var keys: SessionKeys
    private var negotiation = ProtocolGreeting.Negotiation(1, 0, BtxCapabilities.NONE)
    private var writer: Job? = null
    private var sendSequence = 0L
    @Volatile var maxReceiveBytes: Long = initialMaxReceiveBytes
        set(value) { field = value; reconsiderPendingOffers() }
    @Volatile var downloadDestination: String = initialDownloadDestination
    @Volatile var autoAcceptFiles: Boolean = initialAutoAcceptFiles
        set(value) { field = value; if (value) reconsiderPendingOffers() }
    val peerName: String
        get() = try {
            socket.remoteDevice.name ?: socket.remoteDevice.address
        } catch (_: SecurityException) {
            "附近设备"
        }

    suspend fun run() = withContext(Dispatchers.IO) {
        var stage = "安全握手"
        try {
            onDiagnostic(DiagnosticLevel.INFO, "Handshake",
                "安全握手开始（role=${if (listenerRole) "listener" else "dialer"}）")
            keys = handshake()
            onDiagnostic(DiagnosticLevel.INFO, "Handshake", "安全握手密钥派生与信任检查完成")
            stage = "协议会话"
            writer = scope.launch {
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
                    runCatching { socket.close() }
                }
            }
            enqueue(WireMessageType.PROTOCOL_HELLO, 0, ProtocolGreeting.current().encode()).await()
            onDiagnostic(DiagnosticLevel.INFO, "Protocol", "已发送加密 PROTOCOL_HELLO")
            val replay = ReplayGuard(0)
            val remoteHello = BtxRecordCodec.read(socket.inputStream, keys.receiveKey(), keys.receiveNoncePrefix(), replay)
            if (remoteHello.type() != WireMessageType.PROTOCOL_HELLO)
                throw IOException("对端未先发送 PROTOCOL_HELLO")
            negotiation = ProtocolGreeting.current().negotiate(ProtocolGreeting.decode(remoteHello.payload()))
            onDiagnostic(DiagnosticLevel.INFO, "Protocol",
                "已验证对端 PROTOCOL_HELLO（BTX=${negotiation.major()}.${negotiation.minor()}，caps=0x${negotiation.capabilities().toString(16)}）")
            onReady(keys.remotePeerId().hex())
            readLoop(replay)
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

    fun sendChat(text: String, messageId: UUID = UUID.randomUUID(), onSent: (Boolean) -> Unit) {
        if (text.isBlank() || !::keys.isInitialized) return
        scope.launch {
            val content = text.trim()
            val payload = if (negotiation.supports(BtxCapabilities.STRUCTURED_MESSAGES)) {
                MessagePayloadCodec.encode(ChatEnvelope(messageId, ChatPayloadKind.TEXT,
                    System.currentTimeMillis(), content, emptyList()))
            } else content.toByteArray(Charsets.UTF_8)
            runCatching { enqueue(WireMessageType.CHAT, 1, payload).await() }
                .onSuccess { onSent(true) }.onFailure { onSent(false) }
        }
    }

    fun sendFile(uri: Uri, name: String, size: Long) {
        scope.launch {
            val snapshots = mutableListOf<OutgoingSnapshot>()
            var queuedOriginal: TransferItem? = null
            var originalStarted = false
            try {
                val originalId = UUID.randomUUID()
                onDiagnostic(DiagnosticLevel.INFO, "Transfer", "正在创建稳定文件快照（id=${originalId.toString().take(8)}）")
                val original = createSnapshot(context.contentResolver, uri, originalId,
                    persistent = true, sourceName = name).also(snapshots::add)
                val stableOriginalUri = Uri.fromFile(original.file).toString()
                onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                    "文件快照完成（id=${originalId.toString().take(8)}，metadataBytes=${size.coerceAtLeast(0L)}，actualBytes=${original.size}）")
                val mimeType = context.contentResolver.getType(uri) ?: mimeTypeFor(name)
                val structured = negotiation.supports(BtxCapabilities.STRUCTURED_MESSAGES or BtxCapabilities.ATTACHMENT_METADATA)
                if (!structured) {
                    transmitSnapshot(original, originalId, name, mimeType, null, null,
                        AttachmentRole.FILE, stableOriginalUri)
                    return@launch
                }
                val messageId = UUID.randomUUID()
                val originalAttachmentId = UUID.randomUUID()
                val originalRole = if (mimeType.startsWith("image/")) AttachmentRole.IMAGE_ORIGINAL else AttachmentRole.FILE
                queuedOriginal = TransferItem(originalId, name, original.size, outgoing = true,
                    status = TransferStatus.QUEUED, messageId = messageId, attachmentId = originalAttachmentId,
                    mimeType = mimeType, localUri = stableOriginalUri, role = originalRole)
                onTransfer(queuedOriginal)
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
                enqueue(WireMessageType.CHAT, 1, MessagePayloadCodec.encode(envelope)).await()
                onEnvelope(envelope, true)
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
                    originalRole, stableOriginalUri)
            } catch (failure: Throwable) {
                val detail = failure.message?.takeIf { it.isNotBlank() } ?: failure.javaClass.simpleName
                if (!originalStarted) queuedOriginal?.let {
                    onTransfer(it.copy(status = if (failure is CancellationException) TransferStatus.CANCELED
                        else TransferStatus.FAILED, failureDetail = detail))
                }
                onDiagnostic(if (failure is CancellationException) DiagnosticLevel.WARNING else DiagnosticLevel.ERROR,
                    "Transfer", "文件发送${if (failure is CancellationException) "已取消" else "失败"}：$detail")
            } finally {
                snapshots.forEach { snapshot -> snapshot.file.let { file ->
                    if (!snapshot.persistent && file.exists() && !file.delete())
                        onDiagnostic(DiagnosticLevel.WARNING, "Transfer", "发送缓存稍后由系统清理")
                } }
            }
        }
    }

    fun retryFile(uri: Uri, template: TransferItem) {
        scope.launch {
            var snapshot: OutgoingSnapshot? = null
            try {
                snapshot = createSnapshot(context.contentResolver, uri, template.id)
                transmitSnapshot(snapshot, template.id, template.name, template.mimeType,
                    template.messageId, template.attachmentId, template.role, uri.toString())
            } catch (failure: Throwable) {
                val detail = failure.message?.takeIf { it.isNotBlank() } ?: failure.javaClass.simpleName
                onDiagnostic(if (failure is CancellationException) DiagnosticLevel.WARNING else DiagnosticLevel.ERROR,
                    "Transfer", "文件重试${if (failure is CancellationException) "已取消" else "失败"}：$detail")
            } finally {
                snapshot?.file?.let { file -> if (file.exists() && !file.delete())
                    onDiagnostic(DiagnosticLevel.WARNING, "Transfer", "重试缓存稍后由系统清理") }
            }
        }
    }

    private suspend fun transmitSnapshot(prepared: OutgoingSnapshot, id: UUID, name: String, mimeType: String,
                                         messageId: UUID?, attachmentId: UUID?, role: AttachmentRole,
                                         localUri: String) {
        val state = OutgoingTransfer()
        var committedBytes = 0L
        fun report(item: TransferItem) { state.item = item; onTransfer(item) }
        outgoing[id] = state
        try {
            val offer = if (messageId != null && attachmentId != null)
                FileOffer(id, name, prepared.size, FILE_EXTENT_SIZE, prepared.hash,
                    messageId, attachmentId, mimeType, role)
            else FileOffer(id, name, prepared.size, FILE_EXTENT_SIZE, prepared.hash)
            report(TransferItem(id, name, prepared.size, outgoing = true, status = TransferStatus.OFFERED,
                messageId = messageId, attachmentId = attachmentId, mimeType = mimeType,
                localUri = localUri, role = role))
            enqueue(WireMessageType.TRANSFER_OFFER, 2, TransferWire.offer(offer)).await()
            val startIndex = state.accepted.await()
            committedBytes = minOf(prepared.size, startIndex.toLong() * FILE_EXTENT_SIZE)
            prepared.file.inputStream().buffered(FILE_EXTENT_SIZE).use { input ->
                if (committedBytes > 0) {
                    input.skipExactly(committedBytes)
                    report(TransferItem(id, name, prepared.size, committedBytes, true,
                        TransferStatus.RESUMING, messageId, attachmentId, mimeType, localUri, role = role))
                    onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                        "从持久化断点继续（id=${id.toString().take(8)}，extent=$startIndex，bytes=$committedBytes）")
                }
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
            check(committedBytes == prepared.size) { "快照读取长度发生变化：expected=${prepared.size}, actual=$committedBytes" }
            enqueue(WireMessageType.TRANSFER_FINISH, 2, TransferWire.id(id)).await()
            report(TransferItem(id, name, prepared.size, prepared.size, true, TransferStatus.VERIFYING,
                messageId, attachmentId, mimeType, localUri, role = role))
            state.completed.await()
            report(TransferItem(id, name, prepared.size, prepared.size, true, TransferStatus.COMPLETED,
                messageId, attachmentId, mimeType, localUri, role = role))
        } catch (failure: Throwable) {
            val canceled = failure is CancellationException
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
        }
    }

    fun cancelTransfer(id: UUID, reason: String = "用户取消") {
        scope.launch {
            outgoing[id]?.fail(CancellationException(reason))
            pendingOffers.remove(id)?.let { pending ->
                onTransfer(pending.item.copy(status = TransferStatus.CANCELED, failureDetail = reason))
            }
            incoming.remove(id)?.let { transfer ->
                pausedIncoming.remove(id)
                val committed = runCatching { transfer.receiver.contiguousCommittedOffset() }.getOrDefault(0L)
                runCatching { transfer.receiver.close() }
                onTransfer(TransferItem(id, transfer.name, transfer.offer.size,
                    committed, false, TransferStatus.CANCELED, transfer.offer.messageId,
                    transfer.offer.attachmentId, transfer.offer.mimeType, role = transfer.offer.role))
            }
            if (negotiation.supports(BtxCapabilities.TRANSFER_CONTROL)) {
                enqueue(WireMessageType.TRANSFER_CONTROL, 0,
                    TransferControlCodec.encode(TransferControl(id, TransferControlAction.CANCEL,
                        System.currentTimeMillis(), reason))).await()
            } else enqueue(WireMessageType.TRANSFER_FAILED, 2,
                TransferWire.failure(FileTransferFailure(id, reason))).await()
        }
    }

    fun pauseTransfer(id: UUID) {
        scope.launch {
            outgoing[id]?.let { state ->
                state.pause()
                state.item?.copy(status = TransferStatus.PAUSED)?.let {
                    state.item = it
                    onTransfer(it)
                }
            }
            incoming[id]?.let { transfer ->
                pausedIncoming.add(id)
                val committed = runCatching { transfer.receiver.contiguousCommittedOffset() }.getOrDefault(0L)
                onTransfer(TransferItem(id, transfer.name, transfer.offer.size, committed, false,
                    TransferStatus.PAUSED, transfer.offer.messageId, transfer.offer.attachmentId,
                    transfer.offer.mimeType, role = transfer.offer.role))
            }
            if (negotiation.supports(BtxCapabilities.TRANSFER_CONTROL))
                enqueue(WireMessageType.TRANSFER_CONTROL, 0,
                    TransferControlCodec.encode(TransferControl(id, TransferControlAction.PAUSE,
                        System.currentTimeMillis(), "用户暂停"))).await()
        }
    }

    fun resumeTransfer(id: UUID) {
        scope.launch {
            outgoing[id]?.let { state ->
                state.item?.copy(status = TransferStatus.RESUMING)?.let {
                    state.item = it
                    onTransfer(it)
                }
                state.resume()
            }
            incoming[id]?.let { transfer ->
                pausedIncoming.remove(id)
                val committed = runCatching { transfer.receiver.contiguousCommittedOffset() }.getOrDefault(0L)
                onTransfer(TransferItem(id, transfer.name, transfer.offer.size, committed, false,
                    TransferStatus.RESUMING, transfer.offer.messageId, transfer.offer.attachmentId,
                    transfer.offer.mimeType, role = transfer.offer.role))
            }
            if (negotiation.supports(BtxCapabilities.TRANSFER_CONTROL))
                enqueue(WireMessageType.TRANSFER_CONTROL, 0,
                    TransferControlCodec.encode(TransferControl(id, TransferControlAction.RESUME,
                        System.currentTimeMillis(), "用户继续"))).await()
        }
    }

    fun close() {
        incoming.values.forEach { runCatching { it.receiver.close() } }
        incoming.clear()
        pausedIncoming.clear()
        val stopped = CancellationException("蓝牙会话已结束")
        outgoing.values.forEach { it.fail(stopped) }
        outgoing.clear()
        runCatching { socket.close() }
        scope.cancel()
    }

    private suspend fun handshake(): SessionKeys {
        onDiagnostic(DiagnosticLevel.INFO, "Handshake", "正在加载设备身份并创建 Handshake Hello")
        val local = HandshakeHello.create(identityStore.identity)
        val remote = if (listenerRole) {
            onDiagnostic(DiagnosticLevel.INFO, "Handshake", "正在读取对端 Handshake Hello")
            readHello().also {
                onDiagnostic(DiagnosticLevel.INFO, "Handshake", "已读取对端 Hello，正在发送本机 Hello")
                writeHello(local)
            }
        } else {
            onDiagnostic(DiagnosticLevel.INFO, "Handshake", "正在发送本机 Handshake Hello")
            writeHello(local)
            onDiagnostic(DiagnosticLevel.INFO, "Handshake", "本机 Hello 已发送，正在读取对端 Hello")
            readHello()
        }
        onDiagnostic(DiagnosticLevel.INFO, "Handshake", "双方 Hello 已交换，正在派生会话密钥")
        val derived = local.derive(remote)
        when (identityStore.matchesTrustedKey(derived.remotePeerId(), derived.remoteIdentityPublicKey())) {
            false -> throw SecurityException("已信任设备的身份密钥发生变化")
            true -> Unit
            null -> {
                onDiagnostic(DiagnosticLevel.WARNING, "Handshake", "首次连接，等待用户核对安全代码")
                val accepted = onTrustRequired(derived.remotePeerId().hex(), derived.remoteIdentityPublicKey(),
                    derived.formattedSafetyCode())
                if (!accepted) throw SecurityException("用户未确认安全代码")
                identityStore.trust(derived.remotePeerId(), derived.remoteIdentityPublicKey())
                onDiagnostic(DiagnosticLevel.INFO, "Handshake", "用户已确认并固定对端身份")
            }
        }
        return derived
    }

    private fun writeHello(hello: HandshakeHello) {
        val encoded = hello.encode()
        require(encoded.size <= 4096)
        DataOutputStream(socket.outputStream).apply { writeInt(encoded.size); write(encoded); flush() }
    }

    private fun readHello(): HandshakeHello {
        val input = DataInputStream(socket.inputStream)
        val length = input.readInt()
        if (length !in 1..4096) throw IOException("握手消息长度无效")
        return HandshakeHello.decode(input.readNBytes(length))
    }

    private suspend fun writerLoop() {
        while (currentCoroutineContext().isActive) {
            val item = outbound.poll(500, TimeUnit.MILLISECONDS) ?: continue
            try {
                val frame = BtxFrame(item.type, 0, item.streamId, sendSequence++, item.payload)
                BtxRecordCodec.write(socket.outputStream, frame, keys.sendKey(), keys.sendNoncePrefix())
                item.done.complete(Unit)
            } catch (failure: Throwable) {
                item.done.completeExceptionally(failure)
                runCatching { socket.close() }
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
        outbound.put(Outbound(type.priority(), order.getAndIncrement(), type, streamId, payload, result))
        return result
    }

    private fun readLoop(replay: ReplayGuard) {
        while (true) {
            val frame = BtxRecordCodec.read(socket.inputStream, keys.receiveKey(), keys.receiveNoncePrefix(), replay)
            when (frame.type()) {
                WireMessageType.CHAT -> receiveChat(frame.payload())
                WireMessageType.CHAT_RECEIPT -> if (negotiation.supports(BtxCapabilities.MESSAGE_RECEIPTS))
                    onReceipt(MessagePayloadCodec.decodeReceipt(frame.payload()))
                WireMessageType.PING -> enqueue(WireMessageType.PONG, 0, frame.payload())
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
                WireMessageType.TRANSFER_FINISH -> finishTransfer(TransferWire.readId(frame.payload()))
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
                else -> Unit
            }
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
        if (control.action() == TransferControlAction.PAUSE) {
            outgoing[control.transferId()]?.let { state ->
                state.pause()
                state.item?.copy(status = TransferStatus.PAUSED)?.let {
                    state.item = it
                    onTransfer(it)
                }
            }
            onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                "对端暂停文件传输（id=${control.transferId().toString().take(8)}）")
            return
        }
        if (control.action() == TransferControlAction.RESUME) {
            outgoing[control.transferId()]?.let { state ->
                state.item?.copy(status = TransferStatus.RESUMING)?.let {
                    state.item = it
                    onTransfer(it)
                }
                state.resume()
            }
            onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                "对端继续文件传输（id=${control.transferId().toString().take(8)}）")
            return
        }
        if (control.action() != TransferControlAction.CANCEL) {
            onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                "已收到暂不支持的控制指令（id=${control.transferId().toString().take(8)}，action=${control.action()}）")
            return
        }
        outgoing[control.transferId()]?.fail(CancellationException(control.reason().ifBlank { "对端取消传输" }))
        incoming.remove(control.transferId())?.let { transfer ->
            pausedIncoming.remove(control.transferId())
            val committed = runCatching { transfer.receiver.contiguousCommittedOffset() }.getOrDefault(0L)
            runCatching { transfer.receiver.close() }
            onTransfer(TransferItem(control.transferId(), transfer.name, transfer.offer.size, committed,
                false, TransferStatus.CANCELED, transfer.offer.messageId, transfer.offer.attachmentId,
                transfer.offer.mimeType, role = transfer.offer.role))
        }
        onDiagnostic(DiagnosticLevel.WARNING, "Transfer",
            "对端取消文件传输（id=${control.transferId().toString().take(8)}）")
    }

    private fun receiveOffer(offer: FileOffer) {
        try {
            if (incoming.containsKey(offer.id) || pendingOffers.containsKey(offer.id))
                throw IOException("重复文件 Offer")
            if (!autoAcceptFiles) throw IOException("对端已关闭自动接收文件")
            if (offer.size > maxReceiveBytes) {
                val item = incomingItem(offer, TransferStatus.REJECTED).copy(
                    failureDetail = "超过当前接收限制（$maxReceiveBytes B）；30 秒内提高限制可继续接收")
                pendingOffers[offer.id] = PendingOffer(offer, item, System.currentTimeMillis() + OFFER_TIMEOUT_MS)
                onTransfer(item)
                onDiagnostic(DiagnosticLevel.WARNING, "Transfer",
                    "暂缓超限 Offer（id=${offer.id.toString().take(8)}，bytes=${offer.size}，limit=$maxReceiveBytes）")
                scope.launch {
                    delay(OFFER_TIMEOUT_MS)
                    expirePendingOffer(offer.id)
                }
                return
            }
            acceptOffer(offer)
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
        val receiver = TransferReceiver(context.filesDir.resolve("received").toPath(), safeName,
            offer.size, offer.extentSize, offer.hash, null, offer.id.toString().replace("-", ""))
        incoming[offer.id] = IncomingTransfer(offer, safeName, receiver)
        onTransfer((existing ?: incomingItem(offer, TransferStatus.OFFERED)).copy(
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
        if (!autoAcceptFiles) return
        scope.launch {
            pendingOffers.entries.toList().forEach { (id, pending) ->
                if (pending.offer.size > maxReceiveBytes || pending.expiresAt <= System.currentTimeMillis()) return@forEach
                if (!pendingOffers.remove(id, pending)) return@forEach
                runCatching { acceptOffer(pending.offer, pending.item) }
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
            onTransfer(TransferItem(extent.id, transfer.name, transfer.offer.size, completed, false,
                if (pausedIncoming.contains(extent.id)) TransferStatus.PAUSED else TransferStatus.TRANSFERRING,
                transfer.offer.messageId, transfer.offer.attachmentId, transfer.offer.mimeType,
                role = transfer.offer.role))
            enqueue(WireMessageType.TRANSFER_EXTENT_ACK, 2,
                TransferWire.extentAck(FileExtentAck(extent.id, extent.index)))
        } catch (failure: Throwable) {
            incoming.remove(extent.id)
            pausedIncoming.remove(extent.id)
            val committed = runCatching { transfer.receiver.contiguousCommittedOffset() }.getOrDefault(0L)
            runCatching { transfer.receiver.close() }
            onTransfer(TransferItem(extent.id, transfer.name, transfer.offer.size, committed,
                outgoing = false, status = TransferStatus.FAILED, messageId = transfer.offer.messageId,
                attachmentId = transfer.offer.attachmentId, mimeType = transfer.offer.mimeType,
                role = transfer.offer.role))
            val detail = failure.message ?: failure.javaClass.simpleName
            onDiagnostic(DiagnosticLevel.ERROR, "Transfer",
                "接收文件块失败（id=${extent.id.toString().take(8)}，index=${extent.index}）：$detail")
            enqueue(WireMessageType.TRANSFER_FAILED, 2,
                TransferWire.failure(FileTransferFailure(extent.id, detail)))
        }
    }

    private fun finishTransfer(id: UUID) {
        val transfer = incoming[id]
        if (transfer == null) {
            enqueue(WireMessageType.TRANSFER_FAILED, 2,
                TransferWire.failure(FileTransferFailure(id, "未知文件传输")))
            return
        }
        onTransfer(TransferItem(id, transfer.name, transfer.offer.size, transfer.offer.size, false, TransferStatus.VERIFYING,
            transfer.offer.messageId, transfer.offer.attachmentId, transfer.offer.mimeType, role = transfer.offer.role))
        try {
            val target = transfer.receiver.finish()
            val published = if (transfer.offer.role == AttachmentRole.IMAGE_PREVIEW) Uri.fromFile(target.toFile())
                else ReceivedFileStore.publish(context, target, transfer.name, transfer.offer.mimeType,
                    downloadDestination)
            incoming.remove(id)
            pausedIncoming.remove(id)
            onTransfer(TransferItem(id, transfer.name, transfer.offer.size, transfer.offer.size, false, TransferStatus.COMPLETED,
                transfer.offer.messageId, transfer.offer.attachmentId, transfer.offer.mimeType,
                published.toString(), role = transfer.offer.role))
            onDiagnostic(DiagnosticLevel.INFO, "Transfer",
                "文件接收与校验完成（id=${id.toString().take(8)}，bytes=${transfer.offer.size}）")
            enqueue(WireMessageType.TRANSFER_COMPLETE, 2, TransferWire.id(id))
        } catch (failure: Throwable) {
            incoming.remove(id)
            pausedIncoming.remove(id)
            val committed = runCatching { transfer.receiver.contiguousCommittedOffset() }.getOrDefault(0L)
            runCatching { transfer.receiver.close() }
            onTransfer(TransferItem(id, transfer.name, transfer.offer.size, committed,
                outgoing = false, status = TransferStatus.FAILED, messageId = transfer.offer.messageId,
                attachmentId = transfer.offer.attachmentId, mimeType = transfer.offer.mimeType,
                role = transfer.offer.role))
            val detail = failure.message ?: failure.javaClass.simpleName
            onDiagnostic(DiagnosticLevel.ERROR, "Transfer",
                "文件完成校验失败（id=${id.toString().take(8)}）：$detail")
            enqueue(WireMessageType.TRANSFER_FAILED, 2,
                TransferWire.failure(FileTransferFailure(id, detail)))
        }
    }

    private fun handleTransferFailure(failure: FileTransferFailure) {
        val exception = RemoteTransferException(if (failure.reason.isBlank()) "对端报告文件传输失败"
            else "对端报告文件传输失败：${failure.reason}")
        outgoing[failure.id]?.fail(exception)
        incoming.remove(failure.id)?.let { transfer ->
            pausedIncoming.remove(failure.id)
            val committed = runCatching { transfer.receiver.contiguousCommittedOffset() }.getOrDefault(0L)
            runCatching { transfer.receiver.close() }
            onTransfer(TransferItem(failure.id, transfer.name, transfer.offer.size, committed,
                outgoing = false, status = TransferStatus.FAILED, messageId = transfer.offer.messageId,
                attachmentId = transfer.offer.attachmentId, mimeType = transfer.offer.mimeType,
                role = transfer.offer.role))
        }
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
            val file = directory.resolve("$id${if (persistent) suffix else ".part"}")
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

    private data class IncomingTransfer(val offer: FileOffer, val name: String, val receiver: TransferReceiver)
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
    private class OutgoingTransfer {
        val accepted = CompletableDeferred<Int>()
        val completed = CompletableDeferred<Unit>()
        private val acknowledgements = ConcurrentHashMap<Int, CompletableDeferred<Unit>>()
        private val pauseLock = Any()
        private var resumeSignal = CompletableDeferred<Unit>().also { it.complete(Unit) }
        @Volatile var item: TransferItem? = null

        fun pause() = synchronized(pauseLock) {
            if (resumeSignal.isCompleted) resumeSignal = CompletableDeferred()
        }

        fun resume() = synchronized(pauseLock) { resumeSignal.complete(Unit) }

        suspend fun awaitResumed() {
            val signal = synchronized(pauseLock) { resumeSignal }
            signal.await()
        }

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
            synchronized(pauseLock) { resumeSignal.completeExceptionally(failure) }
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
