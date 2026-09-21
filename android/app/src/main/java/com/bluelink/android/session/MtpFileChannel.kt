package com.bluelink.android.session

import android.content.Context
import android.content.BroadcastReceiver
import android.content.Intent
import android.content.IntentFilter
import com.bluelink.android.usb.MtpAvailabilityGate
import com.bluelink.android.usb.MtpProbeSignal
import com.bluelink.android.usb.mtpAvailable
import android.util.Base64
import com.bluelink.android.domain.TransferStatus
import com.bluelink.android.usb.MtpSpool
import com.bluelink.core.MtpFileCipher
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import java.io.InputStream
import java.io.IOException
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

internal class MtpFileChannel(private val context: Context, private val scope: CoroutineScope, private val transferStreams: TransferStreamFence,
    private val send: suspend (ByteArray) -> Unit,
    private val queued: (FileOffer) -> TransferPauseController,
    private val import: suspend (UUID, InputStream, ByteArray) -> Unit,
    private val failure: (UUID, String) -> Unit,
    private val changed: () -> Unit) {
    private val probe = MtpProbeSignal()
    private val usbAvailability = MtpAvailabilityGate()
    private val helloPending = java.util.concurrent.atomic.AtomicBoolean(false)
    private val invalidateUsb = java.util.concurrent.atomic.AtomicBoolean(false)
    private var receiverRegistered = false
    private val usbReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            fun extra(key: String) = if (intent.hasExtra(key)) intent.getBooleanExtra(key, false) else null
            val available = mtpAvailable(extra("connected"), extra("configured"), extra("mtp"), extra("unlocked"))
            android.util.Log.i("BlueLinkMtp", "USB state: connected=${extra("connected")}, configured=${extra("configured")}, mtp=${extra("mtp")}, unlocked=${extra("unlocked")}")
            if (usbAvailability.update(available)) {
                if (!available) invalidateUsb.set(true)
                android.util.Log.i("BlueLinkMtp", "USB MTP availability changed: $available")
                requestProbe()
            }
        }
    }
    fun requestProbe() { helloPending.set(true); probe.request() }
    var allowAttemptSwitch = false
    @Volatile var enabled = false
        set(value) { if (field != value) { field = value; requestProbe() } }
    @Volatile var ready = false
        private set
    private var spool: MtpSpool? = null
    private val jobs = ConcurrentHashMap<UUID, Entry>()
    private var lastReady = 0L
    private var pump: Job? = null
    fun start() {
        try {
            context.registerReceiver(usbReceiver, IntentFilter("android.hardware.usb.action.USB_STATE"), Context.RECEIVER_NOT_EXPORTED)
            receiverRegistered = true
        } catch (error: Exception) { android.util.Log.w("BlueLinkMtp", "USB notifications unavailable; polling remains active", error) }
        pump = scope.launch(transferStreams.context()) {
            while (isActive) {
                try {
                    if (helloPending.getAndSet(false) && enabled) sendPacket(JSONObject().put("op", "hello"))
                    if (invalidateUsb.getAndSet(false)) {
                        setReady(false); interruptJobs()
                        // The directory belongs to this authenticated Bluetooth session. Keep it across USB
                        // reconfiguration; Windows drops its object IDs and must verify the proof again.
                        sendPacket(JSONObject().put("op", "disabled"))
                    }
                    val directoryGranted = MtpSpool.hasGrant(context)
                    if (enabled && usbAvailability.stable && directoryGranted) {
                        if (spool == null) {
                            val started = android.os.SystemClock.elapsedRealtime()
                            spool = MtpSpool.open(context)
                            android.util.Log.i("BlueLinkMtp", "USB directory prepared in ${android.os.SystemClock.elapsedRealtime() - started} ms")
                        }
                        if (ready && android.os.SystemClock.elapsedRealtime() - lastReady > 12_000) setReady(false)
                        announce()
                    } else if ((!enabled || !directoryGranted) && spool != null) {
                        sendPacket(JSONObject().put("op", "disabled")); closeSpool()
                    }
                } catch (error: CancellationException) { throw error }
                catch (_: Exception) { setReady(false) }
                probe.await(enabled && usbAvailability.available, ready)
            }
        }
    }
    private suspend fun announce() {
        val current = spool ?: return
        sendPacket(JSONObject().put("op", "announce").put("epoch", current.epoch)
            .put("path", JSONArray(current.path)).put("proof", encode(current.proof)))
    }
    suspend fun receive(payload: ByteArray) {
        require(payload.size <= 16 * 1024)
        val packet = JSONObject(String(payload, Charsets.UTF_8))
        when (packet.getString("op")) {
            "hello" -> { if (enabled && usbAvailability.stable) { if (spool != null) announce() else probe.request(fastRetry = false) }; return }
            "disabled" -> { setReady(false); interruptJobs(); return }
        }
        val current = spool ?: return
        if (packet.optString("epoch") != current.epoch) return
        if (packet.getString("op") == "ready") { if (enabled && usbAvailability.stable) { lastReady = android.os.SystemClock.elapsedRealtime(); setReady(true) }; return }
        val id = UUID.fromString(packet.getString("id"))
        if (packet.getString("op") == "queue") {
            require(enabled && jobs.size < 256)
            val offer = TransferWire.readOffer(decode(packet.getString("offer")))
            require(offer.id == id)
            check(jobs.putIfAbsent(id, Entry(offer, queued(offer), false, current)) == null)
            return
        }
        val entry = jobs[id] ?: return
        when (packet.getString("op")) {
            "start" -> { scope.launch(transferStreams.context()) {
                entry.route.start(); entry.active = true; entry.start.complete(Unit)
            } }
            "switch" -> {
                require(allowAttemptSwitch) { "Unnegotiated route control" }
                if (entry.outgoing || entry.worker != null || entry.progress.item?.status != TransferStatus.QUEUED)
                    sendPacket(entry.packet("switch-denied"))
                else {
                    jobs.remove(id,entry)
                    entry.progress.item?.let { entry.progress.report(it.copy(status=TransferStatus.CANCELED)) }
                    sendPacket(entry.packet("switched"))
                }
            }
            "switched" -> entry.route.resolve(true)
            "switch-denied" -> entry.route.resolve(false)
            "progress" -> if (entry.active) entry.progress.item?.let {
                entry.progress.report(it.copy(status = TransferStatus.TRANSFERRING,
                    completedBytes = packet.getLong("bytes").coerceIn(0, entry.offer.size)))
            }
            "data" -> if (entry.outgoing) entry.data.complete(Unit)
            "end" -> if (!entry.outgoing) {
                if (entry.progress.item?.status == TransferStatus.QUEUED)
                    entry.progress.item?.let { entry.progress.report(it.copy(status = TransferStatus.CANCELED)) }
                jobs.remove(id); entry.worker?.cancel()
            }
            "blob" -> {
                require(!entry.outgoing && entry.active && entry.worker == null)
                entry.worker = scope.launch(transferStreams.context()) { receiveBlob(entry, packet) }
            }
        }
    }
    fun progress(id: UUID): TransferPauseController? = jobs[id]?.progress
    fun validateOffer(offer: FileOffer) {
        jobs[offer.id]?.let { require(!it.outgoing && it.active && TransferWire.offer(it.offer).contentEquals(TransferWire.offer(offer))) }
    }
    suspend fun runOutgoing(offer: FileOffer, progress: TransferPauseController, body: suspend () -> Unit, onQueued: () -> Unit = {}, allowSwitch: Boolean = false) {
        while (jobs.size >= 128) delay(250)
        val current = spool ?: throw IOException("USB 文件通道不可用")
        check(ready && enabled)
        val entry = Entry(offer, progress, true, current)
        check(jobs.putIfAbsent(offer.id, entry) == null)
        entry.worker = currentCoroutineContext()[Job]
        try {
            progress.item?.let { progress.report(it.copy(status = TransferStatus.QUEUED, completedBytes = 0, queuedForUsb = allowSwitch)) }
            sendPacket(JSONObject().put("op", "queue").put("epoch", current.epoch).put("id", offer.id.toString())
                .put("offer", encode(TransferWire.offer(offer))))
            onQueued()
            entry.start.await()
            progress.item?.let { progress.report(it.copy(queuedForUsb=false)) }
            progress.awaitResumed()
            body()
        } finally {
            withContext(NonCancellable) { runCatching { sendPacket(entry.packet("end")) } }
            jobs.remove(offer.id)
        }
    }
    fun requestSwitch(id: UUID): (suspend () -> Unit)? {
        val entry=jobs[id]?.takeIf { it.outgoing } ?: return null
        val decision=entry.route.request() ?: return null
        return {
            val accepted = try {
                sendPacket(entry.packet("switch"))
                withTimeout(30_000) { decision.await() }
            } catch (error: Exception) {
                entry.route.resolve(true); entry.worker?.cancel(); throw error
            }
            check(accepted) { "对端已开始传输，无法切换通道。" }
            entry.worker?.cancel(CancellationException("改用蓝牙"))
            entry.worker?.join()
        }
    }
    suspend fun sendBlob(id: UUID, input: InputStream) {
        val entry = jobs[id] ?: throw IOException("USB 队列已结束")
        val name = UUID.randomUUID().toString().replace("-", "") + ".blm"
        val key = ByteArray(32).also { SecureRandom().nextBytes(it) }
        val coroutine = currentCoroutineContext()
        try {
            val uri = entry.spool.createBlob(name,id)
            context.contentResolver.openOutputStream(uri, "wt")!!.buffered(MtpFileCipher.CHUNK_SIZE).use { output ->
                MtpFileCipher.encrypt(input, output, id, entry.offer.size, key) {
                    coroutine.ensureActive()
                    runBlocking(coroutine) { entry.progress.awaitResumed() }
                }
            }
            sendPacket(entry.packet("blob").put("blob", name).put("key", encode(key)))
            entry.data.await()
        } finally { key.fill(0); runCatching { entry.spool.deleteBlob(name) } }
    }
    private suspend fun receiveBlob(entry: Entry, packet: JSONObject) {
        var name = ""
        var key = byteArrayOf()
        try {
            name = packet.getString("blob")
            key = decode(packet.getString("key"))
            require(key.size == 32)
            val uri = entry.spool.find(name) ?: throw IOException("USB 中转文件尚未可见")
            context.contentResolver.openInputStream(uri)!!.buffered(MtpFileCipher.CHUNK_SIZE).use {
                import(entry.offer.id, it, key)
            }
            sendPacket(entry.packet("data"))
        } catch (error: Exception) {
            withContext(NonCancellable + transferStreams.context()) {
                failure(entry.offer.id, if (error is CancellationException) "USB 传输已取消" else "USB 文件读取或校验失败，请重试")
            }
        } finally { key.fill(0); runCatching { entry.spool.deleteBlob(name) } }
    }
    fun cancel(id: UUID, status: TransferStatus = TransferStatus.CANCELED) {
        jobs[id]?.let { entry ->
            val error = CancellationException("USB 传输已取消")
            entry.progress.item?.let { entry.progress.report(it.copy(status = status)) }
            entry.progress.fail(error); entry.start.completeExceptionally(error); entry.data.completeExceptionally(error)
            entry.worker?.cancel(error)
            if (!entry.outgoing) jobs.remove(id)
        }
    }
    private fun interruptJobs() {
        jobs.values.toList().filter { it.progress.item?.status in com.bluelink.android.domain.HistoryQuery.activeStatuses }.forEach {
            cancel(it.offer.id, TransferStatus.FAILED)
            failure(it.offer.id, "USB 文件通道已断开，请重试传输")
        }
    }
    fun close() {
        if (receiverRegistered) { runCatching { context.unregisterReceiver(usbReceiver) }; receiverRegistered = false }
        pump?.cancel(); closeSpool()
    }
    private fun closeSpool() {
        val workers = jobs.values.mapNotNull { it.worker }.distinct()
        setReady(false); jobs.keys.toList().forEach { cancel(it) }
        val old = spool; spool = null
        scope.launch(NonCancellable + Dispatchers.IO + transferStreams.context()) {
            workers.joinAll()
            old?.close()
        }
    }
    private fun setReady(value: Boolean) { if (ready != value) { ready = value; android.util.Log.i("BlueLinkMtp", "USB file ready: $value"); changed() } }
    private suspend fun sendPacket(packet: JSONObject) = send(packet.toString().toByteArray(Charsets.UTF_8))
    private class Entry(val offer: FileOffer, val progress: TransferPauseController, val outgoing: Boolean, val spool: MtpSpool) {
        val route=QueuedRouteGate()
        var active = false
        var worker: Job? = null
        val start = CompletableDeferred<Unit>()
        val data = CompletableDeferred<Unit>()
        fun packet(op: String) = JSONObject().put("op", op).put("id", offer.id.toString()).put("epoch", spool.epoch)
    }
    private fun encode(bytes: ByteArray) = Base64.encodeToString(bytes, Base64.NO_WRAP)
    private fun decode(value: String) = Base64.decode(value, Base64.NO_WRAP)
}
