package com.bluelink.android.usb

import android.app.PendingIntent
import android.content.*
import android.content.pm.PackageManager
import android.hardware.usb.UsbAccessory
import android.hardware.usb.UsbManager
import android.os.ParcelFileDescriptor
import com.bluelink.android.domain.*
import com.bluelink.android.transport.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.FileInputStream
import java.io.FileOutputStream
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean

/** Only opens the BlueLink accessory. AOA mode is negotiated by the Windows host. */
internal class UsbAccessoryController(private val context: Context,
    private val attach: (PeerConnection) -> UUID?, private val disconnect: (UUID) -> Unit,
    private val liveSessions: () -> List<ManagedSessionState>) {
    private val manager = context.getSystemService(UsbManager::class.java)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val lock = Mutex()
    private val mutableState = MutableStateFlow(UsbSnapshot())
    val state = mutableState.asStateFlow()
    private var enabled = false
    private var running = true
    private var accessory: UsbAccessory? = null
    private var blocked: UsbAccessory? = null
    private var sessionId: UUID? = null
    private var attempted = false
    private var requestId: String? = null
    private var sessions = emptyList<ManagedSessionState>()
    private val action = "${context.packageName}.USB_ACCESSORY_PERMISSION"
    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            scope.launch { lock.withLock {
                if (intent.action == action) {
                    if (requestId == null || requestId != intent.getStringExtra("requestId")) return@withLock
                    requestId = null
                    val value = accessory ?: return@withLock
                    if (!manager.hasPermission(value)) {
                        blocked = value; mutableState.value = mutableState.value.copy(stage = UsbStage.PERMISSION_DENIED)
                        return@withLock
                    }
                }
                refreshLocked()
            } }
        }
    }
    init {
        context.registerReceiver(receiver, IntentFilter(action).apply {
            addAction(UsbManager.ACTION_USB_ACCESSORY_ATTACHED)
            addAction(UsbManager.ACTION_USB_ACCESSORY_DETACHED)
        }, Context.RECEIVER_NOT_EXPORTED)
    }
    fun configure(value: Boolean, canRun: Boolean) { scope.launch { lock.withLock {
        val changed = enabled != value || running != canRun
        enabled = value; running = canRun
        if (!enabled || !running) {
            requestId = null; blocked = null
            sessionId?.let(disconnect); sessionId = null
            mutableState.value = mutableState.value.copy(stage = if (!enabled) UsbStage.OFF else UsbStage.WAITING, permissionGranted = false)
        } else if (changed) { blocked = null; refreshLocked() }
    } } }
    fun refresh(retry: Boolean = false) { scope.launch { lock.withLock {
        if (retry) blocked = null
        refreshLocked()
    } } }
    fun observe() { scope.launch { lock.withLock {
        val values = liveSessions()
        sessions = values
        if (!enabled || !running) return@withLock
        val current = values.firstOrNull { it.sessionId == sessionId }
        if (current != null) {
            attempted = true
            val ready = current.phase == ConnectionPhase.CONNECTED
            mutableState.value = mutableState.value.copy(stage = if (ready) UsbStage.READY else UsbStage.NEGOTIATING,
                peerId = current.peerId, peerName = current.peerName, permissionGranted = true,
                bluetoothAvailable = UsbStatePolicy.bluetoothAvailable(values, current.peerId))
        } else if (sessionId != null) {
            sessionId = null; blocked = accessory
            mutableState.value = mutableState.value.copy(stage = UsbStatePolicy.disconnected(values, state.value.peerId),
                bluetoothAvailable = UsbStatePolicy.bluetoothAvailable(values, state.value.peerId))
        } else if (enabled && state.value.stage in setOf(UsbStage.FALLBACK, UsbStage.UNAVAILABLE)) {
            mutableState.value = mutableState.value.copy(stage = UsbStatePolicy.disconnected(values, state.value.peerId),
                bluetoothAvailable = UsbStatePolicy.bluetoothAvailable(values, state.value.peerId))
        }
    } } }
    private fun refreshLocked() {
        if (!enabled || !running) return
        if (!context.packageManager.hasSystemFeature(PackageManager.FEATURE_USB_ACCESSORY)) {
            mutableState.value = UsbSnapshot(UsbStage.UNSUPPORTED); return
        }
        try {
            val current = manager.accessoryList?.firstOrNull { UsbStatePolicy.isBlueLink(it.manufacturer, it.model) }
            if (accessory != current) {
                requestId = null; blocked = null
                sessionId?.let(disconnect); sessionId = null; accessory = current
                if (current != null) mutableState.value = UsbSnapshot(stage = UsbStage.WAITING)
            }
            if (current == null) {
                mutableState.value = mutableState.value.copy(stage = if (attempted) UsbStatePolicy.disconnected(sessions, state.value.peerId) else UsbStage.WAITING,
                    permissionGranted = false, bluetoothAvailable = UsbStatePolicy.bluetoothAvailable(sessions, state.value.peerId))
                return
            }
            if (sessionId != null || blocked == current || requestId != null) return
            if (!manager.hasPermission(current)) {
                val id = UUID.randomUUID().toString(); requestId = id
                mutableState.value = mutableState.value.copy(stage = UsbStage.AUTHORIZATION, permissionGranted = false)
                val permission = PendingIntent.getBroadcast(context, id.hashCode(), Intent(action).setPackage(context.packageName)
                    .putExtra("requestId", id), PendingIntent.FLAG_ONE_SHOT or PendingIntent.FLAG_IMMUTABLE)
                manager.requestPermission(current, permission)
                return
            }
            attempted = true
            mutableState.value = mutableState.value.copy(stage = UsbStage.NEGOTIATING, permissionGranted = true)
            val descriptor = manager.openAccessory(current) ?: error("USB accessory unavailable")
            val connection = AccessoryConnection(descriptor, "usb:BlueLink:${current.serial.orEmpty()}")
            sessionId = try { attach(connection) } catch (failure: Exception) { connection.close(); throw failure }
            if (sessionId == null) {
                connection.close(); blocked = current
                mutableState.value = mutableState.value.copy(stage = UsbStage.UNAVAILABLE)
            }
        } catch (_: SecurityException) {
            blocked = accessory; requestId = null
            mutableState.value = mutableState.value.copy(stage = UsbStage.PERMISSION_DENIED, permissionGranted = false)
        } catch (_: Exception) {
            blocked = accessory; requestId = null
            mutableState.value = mutableState.value.copy(stage = UsbStage.UNAVAILABLE)
        }
    }
    private class AccessoryConnection(private val descriptor: ParcelFileDescriptor, override val address: String) : PeerConnection {
        private val streams = InterruptibleAccessoryStreams(FileInputStream(descriptor.fileDescriptor).channel,
            FileOutputStream(descriptor.fileDescriptor).channel) { descriptor.close() }
        override val input = streams.input
        override val output = streams.output
        override val name = "Windows PC"
        override val platform = PeerPlatform.WINDOWS
        override val transport = SessionTransport.USB
        override val listenerRole = true
        private val closed = AtomicBoolean()
        override fun close() { if (closed.compareAndSet(false, true)) {
            runCatching { streams.close() }
        } }
    }
}
