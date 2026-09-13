package com.bluelink.android.bluetooth

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattServer
import android.bluetooth.BluetoothGattServerCallback
import android.bluetooth.BluetoothGattService
import android.bluetooth.BluetoothProfile
import android.bluetooth.BluetoothServerSocket
import android.bluetooth.BluetoothSocket
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.content.pm.PackageManager
import android.os.ParcelUuid
import com.bluelink.android.domain.DiscoveryState
import com.bluelink.android.domain.DiagnosticLevel
import com.bluelink.android.domain.DeviceProjectionPolicy
import com.bluelink.android.domain.NearbyDevice
import com.bluelink.android.domain.PeerPlatform
import com.bluelink.core.BtxConstants
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import java.io.Closeable
import java.io.IOException
import java.util.Locale

data class BluetoothConnection(val socket: BluetoothSocket, val peerName: String)

@SuppressLint("MissingPermission")
class BluetoothRepository(
    private val context: Context,
    identityPeerId: String,
    private val diagnostic: (DiagnosticLevel, String, String) -> Unit = { _, _, _ -> },
    hasActiveConnection: (String) -> Boolean = { false },
) : Closeable {
    private val adapter: BluetoothAdapter? = context.getSystemService(BluetoothManager::class.java).adapter
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val _devices = MutableStateFlow<List<NearbyDevice>>(emptyList())
    val devices: StateFlow<List<NearbyDevice>> = _devices.asStateFlow()
    private val nearbyTracker = NearbyDeviceTracker()
    private val _discovery = MutableStateFlow(DiscoveryState())
    val discovery: StateFlow<DiscoveryState> = _discovery.asStateFlow()
    private var server: BluetoothServerSocket? = null
    private var gattServer: BluetoothGattServer? = null
    private var connectRequestHandler: (suspend (BluetoothConnection) -> Unit)? = null
    private val dialGate = PeerDialGate(hasActiveConnection)
    private var serverJob: Job? = null
    @Volatile private var discoveryDetail = "下拉刷新查找附近运行蓝联的设备"
    @Volatile private var presenceError: String? = null
    @Volatile private var advertising = false
    @Volatile private var advertisingStarting = false
    @Volatile private var advertisingWithName = false
    @Volatile private var discoverable = false // Wait for persisted settings before advertising.
    @Volatile var localDisplayName: String = ""
    val deviceDisplayName: String get() = localDisplayName.ifBlank {
        runCatching { adapter?.name }.getOrNull().orEmpty().ifBlank { "Android" }
    }

    fun setDiscoverable(enabled: Boolean) {
        discoverable = enabled
        if (enabled) startPresence() else stopAdvertising()
    }

    private fun stopAdvertising() {
        runCatching { adapter?.bluetoothLeAdvertiser?.stopAdvertising(advertiseCallback) }
        advertising = false
        advertisingStarting = false
        refreshDiscovery()
    }
    @Volatile private var localPresenceId = identityPeerId.take(16).chunked(2)
        .map { it.toInt(16).toByte() }.toByteArray()
    private val localPresenceIdHex get() = localPresenceId.joinToString("") { "%02X".format(Locale.ROOT, it) }
    private val localRendezvousIdHex get() = localPresenceId.copyOfRange(0, 6)
        .joinToString("") { "%02X".format(Locale.ROOT, it) }

    /** Called with runtime session admission paused and Bluetooth endpoints stopped. */
    fun updateIdentity(peerId: String) {
        localPresenceId = peerId.take(16).chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    }

    private val gattServerCallback = object : BluetoothGattServerCallback() {
        override fun onServiceAdded(status: Int, service: BluetoothGattService) {
            if (service.uuid != BtxConstants.BLE_RENDEZVOUS_UUID) return
            if (status == BluetoothGatt.GATT_SUCCESS)
                log(DiagnosticLevel.INFO, "Android GATT Rendezvous 服务已就绪")
            else log(DiagnosticLevel.ERROR, "添加 GATT Rendezvous 服务失败（$status）")
        }

        override fun onConnectionStateChange(device: BluetoothDevice, status: Int, newState: Int) {
            log(if (status == BluetoothGatt.GATT_SUCCESS) DiagnosticLevel.INFO else DiagnosticLevel.WARNING,
                "GATT 客户端 ${redact(device.address)} 状态=$newState，status=$status")
        }

        override fun onCharacteristicReadRequest(
            device: BluetoothDevice,
            requestId: Int,
            offset: Int,
            characteristic: BluetoothGattCharacteristic,
        ) {
            if (characteristic.uuid != BtxConstants.TRANSPORT_OFFER_UUID) {
                gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_REQUEST_NOT_SUPPORTED, offset, null)
                return
            }
            val offer = localPeerInfo()
            if (offset !in 0..offer.size) {
                gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_INVALID_OFFSET, offset, null)
                return
            }
            log(DiagnosticLevel.INFO, "Windows ${redact(device.address)} 读取 Android 设备信息")
            gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_SUCCESS, offset,
                offer.copyOfRange(offset, offer.size))
        }

        override fun onCharacteristicWriteRequest(
            device: BluetoothDevice,
            requestId: Int,
            characteristic: BluetoothGattCharacteristic,
            preparedWrite: Boolean,
            responseNeeded: Boolean,
            offset: Int,
            value: ByteArray,
        ) {
            val supported = characteristic.uuid == BtxConstants.CONNECT_REQUEST_UUID && !preparedWrite && offset == 0
            val parsed = if (supported) runCatching { parseTransportOffer(value) } else null
            if (responseNeeded) gattServer?.sendResponse(device, requestId, when {
                !supported -> BluetoothGatt.GATT_REQUEST_NOT_SUPPORTED
                parsed?.isSuccess == true -> BluetoothGatt.GATT_SUCCESS
                else -> BluetoothGatt.GATT_FAILURE
            }, offset, null)
            if (!supported) return
            val offer = parsed!!.getOrElse { failure ->
                log(DiagnosticLevel.ERROR,
                    "Connect Request 格式无效：${failure.message ?: failure.javaClass.simpleName}")
                return
            }
            log(DiagnosticLevel.INFO, "收到 Windows ${offer.name.ifBlank { redact(offer.classicAddress) }} 的回连请求")
            connectBack(offer)
        }
    }

    private var stopActiveScan: (() -> Unit)? = null
    private val discoveryScan = SingleDiscoveryScan(scope,
        startRadio = { accept, failed ->
            val scanner = requireNotNull(adapter?.bluetoothLeScanner) { "BLE 扫描器不可用" }
            val callback = object : ScanCallback() {
                override fun onScanResult(callbackType: Int, result: ScanResult) = accept { put(result) }
                override fun onBatchScanResults(results: MutableList<ScanResult>) = accept { results.forEach(::put) }
                override fun onScanFailed(errorCode: Int) = failed("BLE 扫描失败（错误 $errorCode）")
            }
            stopActiveScan = { scanner.stopScan(callback) }
            val filters = listOf(
                ScanFilter.Builder().setManufacturerData(COMPANY_ID, PRESENCE_PREFIX, PRESENCE_PREFIX_MASK).build(),
                ScanFilter.Builder().setServiceUuid(ParcelUuid(BtxConstants.BLE_RENDEZVOUS_UUID)).build(),
                ScanFilter.Builder().setServiceData(ParcelUuid(BtxConstants.BLE_RENDEZVOUS_UUID),
                    RENDEZVOUS_PREFIX, RENDEZVOUS_PREFIX_MASK).build(),
            )
            val settings = ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
                .setReportDelay(0).setLegacy(false).build()
            scanner.startScan(filters, settings, callback)
        },
        stopRadio = { stopActiveScan?.invoke(); stopActiveScan = null },
        onStarted = {
            publishDevices(nearbyTracker.beginScan())
            log(DiagnosticLevel.INFO, "开始 BLE 扫描")
            updateDiscovery(true, "正在查找附近的 BlueLink 设备…")
        },
        onTick = ::expireDevices,
        onFinished = { completed, failure ->
            if (completed) publishDevices(nearbyTracker.completeScanWindow(System.currentTimeMillis()))
            val detail = failure ?: if (completed) {
                if (_devices.value.isEmpty()) "附近没有发现运行蓝联的设备" else "发现 ${_devices.value.size} 台附近设备"
            } else "已停止附近设备扫描"
            log(if (failure == null) DiagnosticLevel.INFO else DiagnosticLevel.ERROR, "BLE 扫描结束：$detail")
            updateDiscovery(false, detail)
        },
    )

    private val advertiseCallback = object : AdvertiseCallback() {
        override fun onStartSuccess(settingsInEffect: AdvertiseSettings) {
            if (!discoverable) { stopAdvertising(); return }
            advertisingStarting = false
            advertising = true
            presenceError = null
            log(DiagnosticLevel.INFO, "Android Presence 广播已启动")
            refreshDiscovery()
        }
        override fun onStartFailure(errorCode: Int) {
            advertisingStarting = false
            advertising = false
            if (errorCode == AdvertiseCallback.ADVERTISE_FAILED_DATA_TOO_LARGE && advertisingWithName) {
                log(DiagnosticLevel.WARNING, "系统蓝牙名过长，改用 GATT 读取设备名")
                startAdvertising(includeName = false)
                return
            }
            presenceError = advertiseFailure(errorCode)
            log(DiagnosticLevel.ERROR, requireNotNull(presenceError))
            refreshDiscovery()
        }
    }

    fun available(): Boolean = adapter?.isEnabled == true

    fun startPresence(onConnectionRequested: (suspend (BluetoothConnection) -> Unit)? = null) {
        if (onConnectionRequested != null) connectRequestHandler = onConnectionRequested
        val bluetooth = adapter ?: run {
            presenceError = "此设备不支持蓝牙"
            log(DiagnosticLevel.ERROR, requireNotNull(presenceError))
            refreshDiscovery()
            return
        }
        if (!hasPermission(Manifest.permission.BLUETOOTH_ADVERTISE)) {
            presenceError = "缺少附近设备的蓝牙广播权限"
            log(DiagnosticLevel.WARNING, requireNotNull(presenceError))
            refreshDiscovery()
            return
        }
        if (!bluetooth.isEnabled) {
            presenceError = "蓝牙未开启，Presence 广播未启动"
            log(DiagnosticLevel.WARNING, requireNotNull(presenceError))
            refreshDiscovery()
            return
        }
        if (advertising || advertisingStarting) return
        if (bluetooth.bluetoothLeAdvertiser == null) {
            presenceError = "本机不支持 BLE Peripheral 广播"
            log(DiagnosticLevel.ERROR, requireNotNull(presenceError))
            refreshDiscovery()
            return
        }
        ensureGattServer()
        if (gattServer == null) return
        startAdvertising(includeName = true)
    }

    private fun startAdvertising(includeName: Boolean) {
        if (!discoverable) return
        val bluetooth = adapter ?: return
        val advertiser = bluetooth.bluetoothLeAdvertiser ?: return
        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_BALANCED)
            .setConnectable(true)
            .setTimeout(0)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_MEDIUM)
            .build()
        val presence = AdvertiseData.Builder()
            .addServiceData(ParcelUuid(BtxConstants.BLE_RENDEZVOUS_UUID), rendezvousPayload(PeerPlatform.ANDROID, localPresenceId))
            .build()
        val scanResponse = if (includeName) AdvertiseData.Builder().setIncludeDeviceName(true).build() else null
        advertisingWithName = includeName
        advertisingStarting = true
        runCatching { advertiser.startAdvertising(settings, presence, scanResponse, advertiseCallback) }
            .onFailure { failure ->
                advertisingStarting = false
                advertising = false
                presenceError = "蓝联 Presence 广播启动异常：${failure.message ?: failure.javaClass.simpleName}"
                log(DiagnosticLevel.ERROR, requireNotNull(presenceError))
                refreshDiscovery()
            }
    }

    @Synchronized
    fun startDiscovery() {
        val bluetooth = adapter ?: run {
            publishDevices(nearbyTracker.unavailable())
            log(DiagnosticLevel.ERROR, "此设备不支持蓝牙，无法启动扫描")
            updateDiscovery(false, "此设备不支持蓝牙")
            return
        }
        when {
            !hasPermission(Manifest.permission.BLUETOOTH_SCAN) ||
                !hasPermission(Manifest.permission.BLUETOOTH_CONNECT) -> {
                publishDevices(nearbyTracker.unavailable())
                log(DiagnosticLevel.WARNING, "缺少蓝牙扫描或连接权限")
                updateDiscovery(false, "需要蓝牙扫描和连接权限")
                return
            }
            !bluetooth.isEnabled -> {
                publishDevices(nearbyTracker.unavailable())
                log(DiagnosticLevel.WARNING, "蓝牙未开启，无法启动扫描")
                updateDiscovery(false, "请先打开蓝牙")
                return
            }
        }
        if (bluetooth.bluetoothLeScanner == null) {
            publishDevices(nearbyTracker.unavailable())
            log(DiagnosticLevel.ERROR, "BLE 扫描器不可用")
            updateDiscovery(false, "BLE 扫描器不可用")
            return
        }

        discoveryScan.start()
    }

    suspend fun connect(device: NearbyDevice, onStage: (String) -> Unit,
                        onConnected: suspend (BluetoothConnection) -> Unit): Unit = withContext(Dispatchers.IO) {
        val bluetooth = requireNotNull(adapter) { "Bluetooth is unavailable" }
        require(hasPermission(Manifest.permission.BLUETOOTH_CONNECT)) { "缺少蓝牙连接权限" }
        require(device.rendezvousAvailable && device.connectable) { "该 Windows 广播没有可连接的 BlueLink Rendezvous" }
        stopDiscoveryForConnection()
        log(DiagnosticLevel.INFO, "已停止扫描，准备连接 Windows ${redact(device.address)}")
        onStage("正在连接 BLE Rendezvous")
        val offer = try {
            resolveTransportOffer(bluetooth.getRemoteDevice(device.address), onStage)
        } catch (failure: kotlinx.coroutines.CancellationException) { throw failure }
        catch (failure: Exception) {
            throw IOException("无法从对端取得 RFCOMM 连接参数：${failure.message}", failure)
        }
        log(DiagnosticLevel.INFO, "已读取 Transport Offer，设备名=${offer.name}，Classic=${redact(offer.classicAddress)}")
        publishDevices(nearbyTracker.rename(device.address, offer.name.ifBlank { device.name }))
        onStage("正在请求配对并连接 RFCOMM")
        dialTransportOffer(offer.copy(name = offer.name.ifBlank { device.name }), onConnected)
    }

    private suspend fun dialTransportOffer(offer: TransportOffer,
                                          onConnected: suspend (BluetoothConnection) -> Unit) {
        val lease = dialGate.tryAcquire(offer.classicAddress)
        if (lease == null) {
            log(DiagnosticLevel.INFO, "同一设备已有拨号或会话，复用现有连接")
            return
        }
        var socket: BluetoothSocket? = null
        var handedOff = false
        try {
            val connectedSocket = requireNotNull(adapter).getRemoteDevice(offer.classicAddress)
                .createRfcommSocketToServiceRecord(BtxConstants.RFCOMM_SERVICE_UUID)
            socket = connectedSocket
            connectedSocket.connect()
            log(DiagnosticLevel.INFO, "RFCOMM 通道已建立")
            // Retain the dial lease until the session is registered, closing the hand-off race.
            onConnected(BluetoothConnection(connectedSocket, offer.name.ifBlank { "Windows ${redact(offer.classicAddress)}" }))
            handedOff = true
        } finally {
            if (!handedOff) {
                try { socket?.close() } catch (_: IOException) { }
            }
            lease.close()
        }
    }

    @Synchronized
    private fun stopDiscoveryForConnection() {
        discoveryScan.stop()
    }

    fun startServer(onAccepted: suspend (BluetoothSocket) -> Unit) {
        if (serverJob?.isActive == true || !hasPermission(Manifest.permission.BLUETOOTH_CONNECT)) return
        serverJob = scope.launch {
            try {
                server = adapter?.listenUsingRfcommWithServiceRecord("BlueLink BTX/1", BtxConstants.RFCOMM_SERVICE_UUID)
                log(DiagnosticLevel.INFO, "Android RFCOMM 监听器已启动")
                while (currentCoroutineContext().isActive) {
                    val socket = server?.accept() ?: break
                    log(DiagnosticLevel.INFO, "收到 RFCOMM 入站连接 ${redact(socket.remoteDevice.address)}")
                    launch { onAccepted(socket) }
                }
            } catch (failure: Throwable) {
                if (currentCoroutineContext().isActive)
                    log(DiagnosticLevel.ERROR,
                        "RFCOMM 监听失败：${failure.message ?: failure.javaClass.simpleName}")
            }
        }
    }

    fun stopServer() {
        serverJob?.cancel()
        runCatching { server?.close() }
        server = null
    }

    @Synchronized
    fun suspendBackgroundWork() {
        discoveryScan.stop()
        if (advertising) runCatching { adapter?.bluetoothLeAdvertiser?.stopAdvertising(advertiseCallback) }
        advertisingStarting = false
        advertising = false
        runCatching { gattServer?.clearServices() }
        runCatching { gattServer?.close() }
        gattServer = null
        stopServer()
    }

    override fun close() {
        log(DiagnosticLevel.INFO, "正在停止 Android 蓝牙运行时")
        suspendBackgroundWork()
        scope.cancel()
    }

    private fun put(result: ScanResult) {
        val record = result.scanRecord ?: return
        val now = System.currentTimeMillis()
        val rendezvousUuid = ParcelUuid(BtxConstants.BLE_RENDEZVOUS_UUID)
        val rendezvousPresence = record.getServiceData(rendezvousUuid)?.let(::parseRendezvous)
        val rendezvous = record.serviceUuids?.contains(rendezvousUuid) == true ||
            rendezvousPresence != null
        val parsed = record.getManufacturerSpecificData(COMPANY_ID)?.let(::parsePresence)
        if (parsed == null && !rendezvous) return
        if (parsed != null && parsed.protocolMajor != BtxConstants.PROTOCOL_MAJOR) return
        if (parsed?.presenceId == localPresenceIdHex) return
        if (rendezvousPresence?.presenceId == localRendezvousIdHex) return
        val discoveryId = parsed?.presenceId ?: rendezvousPresence?.presenceId.orEmpty()
        val presenceAddress = result.device.address
        val observedPlatform = rendezvousPresence?.platform ?: parsed?.platform ?: PeerPlatform.UNKNOWN
        val suffix = parsed?.presenceId?.takeLast(4) ?: rendezvousPresence?.presenceId?.takeLast(4)
            ?: presenceAddress.replace(":", "").takeLast(4)
        val observedName = record.deviceName
            ?: runCatching { result.device.name }.getOrNull()
            ?: if (observedPlatform == PeerPlatform.WINDOWS) "Windows 设备 $suffix"
            else "BlueLink ${observedPlatform.name.lowercase(Locale.ROOT)} $suffix"
        val normalizedIdentity = DeviceProjectionPolicy.normalizeIdentity(discoveryId)
        val previous = nearbyTracker.snapshot().firstOrNull {
            it.address.equals(presenceAddress, true) ||
                (normalizedIdentity != null &&
                    DeviceProjectionPolicy.normalizeIdentity(it.discoveryId) == normalizedIdentity)
        }
        val bonded =
            adapter?.bondedDevices.orEmpty().any { it.address.equals(presenceAddress, true) }
        val values = nearbyTracker.observe(NearbyDeviceTracker.Observation(
            name = observedName,
            address = presenceAddress,
            bonded = bonded,
            discoveryId = discoveryId,
            rssi = result.rssi.toShort(),
            platform = observedPlatform,
            rendezvousAvailable = rendezvous,
            connectable = rendezvous && result.isConnectable,
            observedAtEpochMs = now,
        ))
        publishDevices(values)
        val value = values.firstOrNull {
            it.address.equals(presenceAddress, true) ||
                (normalizedIdentity != null &&
                    DeviceProjectionPolicy.normalizeIdentity(it.discoveryId) == normalizedIdentity)
        } ?: return
        updateDiscovery(true, "正在扫描 · 已发现 ${_devices.value.size} 台 BlueLink 设备")
        if (previous == null) {
            log(DiagnosticLevel.INFO,
                "发现 ${value.platform} 设备 ${value.name}（${redact(presenceAddress)}，${result.rssi} dBm，connectable=${value.connectable}）")
        } else if (previous.name != value.name || previous.connectable != value.connectable) {
            log(DiagnosticLevel.INFO, "设备信息已更新：${value.name}，connectable=${value.connectable}")
        }
    }

    private fun expireDevices() {
        publishDevices(nearbyTracker.expire(System.currentTimeMillis()))
    }

    private fun publishDevices(values: List<NearbyDevice>) {
        _devices.value = values
    }

    private fun hasPermission(permission: String): Boolean =
        context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED

    private fun updateDiscovery(scanning: Boolean, detail: String) {
        discoveryDetail = detail
        refreshDiscovery(scanning)
    }

    private fun log(level: DiagnosticLevel, message: String) =
        diagnostic(level, "Bluetooth", message)

    private fun redact(address: String): String =
        address.replace(":", "").takeLast(4).padStart(4, '*').let { "**:**:**:**:${it.take(2)}:${it.takeLast(2)}" }

    private fun refreshDiscovery(scanning: Boolean = _discovery.value.scanning) {
        val warning = presenceError
        _discovery.value = DiscoveryState(scanning,
            if (warning.isNullOrBlank()) discoveryDetail else "$discoveryDetail · $warning")
    }

    private fun ensureGattServer() {
        if (gattServer != null) return
        if (!hasPermission(Manifest.permission.BLUETOOTH_CONNECT)) {
            presenceError = "缺少蓝牙连接权限，GATT Rendezvous 未启动"
            log(DiagnosticLevel.WARNING, requireNotNull(presenceError))
            return
        }
        val manager = context.getSystemService(BluetoothManager::class.java)
        val value = manager.openGattServer(context, gattServerCallback) ?: run {
            presenceError = "无法创建 Android GATT Server"
            log(DiagnosticLevel.ERROR, requireNotNull(presenceError))
            return
        }
        val service = BluetoothGattService(BtxConstants.BLE_RENDEZVOUS_UUID,
            BluetoothGattService.SERVICE_TYPE_PRIMARY)
        service.addCharacteristic(BluetoothGattCharacteristic(BtxConstants.TRANSPORT_OFFER_UUID,
            BluetoothGattCharacteristic.PROPERTY_READ, BluetoothGattCharacteristic.PERMISSION_READ))
        service.addCharacteristic(BluetoothGattCharacteristic(BtxConstants.CONNECT_REQUEST_UUID,
            BluetoothGattCharacteristic.PROPERTY_WRITE, BluetoothGattCharacteristic.PERMISSION_WRITE))
        gattServer = value
        if (!value.addService(service)) {
            log(DiagnosticLevel.ERROR, "无法提交 Android GATT Rendezvous 服务")
            value.close()
            gattServer = null
        }
    }

    private fun connectBack(offer: TransportOffer) {
        val handler = connectRequestHandler
        if (handler == null) {
            log(DiagnosticLevel.WARNING, "收到回连请求，但会话运行时尚未就绪")
            return
        }
        scope.launch {
            stopDiscoveryForConnection()
            try {
                log(DiagnosticLevel.INFO, "正在响应 Windows 请求建立 RFCOMM")
                dialTransportOffer(offer, handler)
            } catch (failure: kotlinx.coroutines.CancellationException) { throw failure }
            catch (failure: Exception) {
                log(DiagnosticLevel.ERROR,
                    "Windows 回连失败：${failure.javaClass.simpleName}: ${failure.message ?: "无详细信息"}")
            }
        }
    }

    private fun advertiseFailure(errorCode: Int): String = when (errorCode) {
        AdvertiseCallback.ADVERTISE_FAILED_DATA_TOO_LARGE -> "Presence 广播数据超过设备限制"
        AdvertiseCallback.ADVERTISE_FAILED_TOO_MANY_ADVERTISERS -> "系统没有可用的 BLE 广播实例"
        AdvertiseCallback.ADVERTISE_FAILED_ALREADY_STARTED -> "Presence 广播已经启动"
        AdvertiseCallback.ADVERTISE_FAILED_INTERNAL_ERROR -> "系统启动 Presence 广播时发生内部错误"
        AdvertiseCallback.ADVERTISE_FAILED_FEATURE_UNSUPPORTED -> "本机不支持 BLE Peripheral 广播"
        else -> "蓝联 Presence 广播失败（错误 $errorCode）"
    }

    private suspend fun resolveTransportOffer(
        device: android.bluetooth.BluetoothDevice,
        onStage: (String) -> Unit,
    ): TransportOffer {
        val value = CompletableDeferred<ByteArray>()
        val callback = object : BluetoothGattCallback() {
            override fun onConnectionStateChange(gatt: BluetoothGatt, status: Int, newState: Int) {
                when {
                    status != BluetoothGatt.GATT_SUCCESS -> value.completeExceptionally(IOException("GATT 连接失败（$status）"))
                    newState == BluetoothProfile.STATE_CONNECTED -> {
                        onStage("正在发现 BlueLink GATT 服务")
                        if (!gatt.discoverServices()) value.completeExceptionally(IOException("无法启动 GATT 服务发现"))
                    }
                    newState == BluetoothProfile.STATE_DISCONNECTED && !value.isCompleted ->
                        value.completeExceptionally(IOException("GATT 连接已断开"))
                }
            }

            override fun onServicesDiscovered(gatt: BluetoothGatt, status: Int) {
                if (status != BluetoothGatt.GATT_SUCCESS) {
                    value.completeExceptionally(IOException("GATT 服务发现失败（$status）"))
                    return
                }
                val characteristic = gatt.getService(BtxConstants.BLE_RENDEZVOUS_UUID)
                    ?.getCharacteristic(BtxConstants.TRANSPORT_OFFER_UUID)
                onStage("正在读取 Windows 设备名和 RFCOMM 参数")
                if (characteristic == null || !gatt.readCharacteristic(characteristic))
                    value.completeExceptionally(IOException("对端未公开 BlueLink Rendezvous 服务"))
            }

            override fun onCharacteristicRead(gatt: BluetoothGatt, characteristic: BluetoothGattCharacteristic,
                                              bytes: ByteArray, status: Int) {
                completeRead(characteristic, bytes, status)
            }

            @Deprecated("API 32 callback")
            override fun onCharacteristicRead(gatt: BluetoothGatt, characteristic: BluetoothGattCharacteristic,
                                              status: Int) {
                @Suppress("DEPRECATION")
                completeRead(characteristic, characteristic.value ?: byteArrayOf(), status)
            }

            private fun completeRead(characteristic: BluetoothGattCharacteristic, bytes: ByteArray, status: Int) {
                if (characteristic.uuid != BtxConstants.TRANSPORT_OFFER_UUID || value.isCompleted) return
                if (status == BluetoothGatt.GATT_SUCCESS) value.complete(bytes)
                else value.completeExceptionally(IOException("读取 Transport Offer 失败（$status）"))
            }
        }
        val gatt = device.connectGatt(context, false, callback, android.bluetooth.BluetoothDevice.TRANSPORT_LE)
        try {
            return parseTransportOffer(withTimeout(12_000) { value.await() }, requireClassicAddress = true)
        } finally {
            runCatching { gatt.disconnect() }
            gatt.close()
        }
    }

    private fun localPeerInfo(): ByteArray = transportOffer(
        "00:00:00:00:00:00",
        deviceDisplayName,
    )

    private fun transportOffer(classicAddress: String, displayName: String): ByteArray {
        var safeName = displayName.trim()
        while (safeName.toByteArray(Charsets.UTF_8).size > 80) safeName = safeName.dropLast(1)
        val name = safeName.toByteArray(Charsets.UTF_8)
        val address = classicAddress.split(':').map { it.toInt(16).toByte() }
        require(address.size == 6) { "Classic Bluetooth 地址无效" }
        return ByteArray(8 + name.size).also { value ->
            value[0] = BtxConstants.PROTOCOL_MAJOR.toByte()
            address.forEachIndexed { index, byte -> value[1 + index] = byte }
            value[7] = name.size.toByte()
            name.copyInto(value, destinationOffset = 8)
        }
    }

    private fun parseTransportOffer(value: ByteArray, requireClassicAddress: Boolean = true): TransportOffer {
        if (value.size < 8 || (value[0].toInt() and 0xff) != BtxConstants.PROTOCOL_MAJOR)
            throw IOException("Transport Offer 格式无效")
        var classicAddress = 0L
        for (index in 0 until 6)
            classicAddress = (classicAddress shl 8) or (value[1 + index].toLong() and 0xff)
        if (requireClassicAddress && classicAddress == 0L)
            throw IOException("对端未提供 Classic Bluetooth 地址")
        val nameLength = value[7].toInt() and 0xff
        if (value.size < 8 + nameLength) throw IOException("Transport Offer 设备名长度无效")
        val name = if (nameLength == 0) "" else String(value, 8, nameLength, Charsets.UTF_8).trim()
        return TransportOffer(formatAddress(classicAddress), name)
    }

    private data class Presence(val protocolMajor: Int, val platform: PeerPlatform, val presenceId: String)
    private data class TransportOffer(val classicAddress: String, val name: String)

    companion object {
        const val COMPANY_ID = 0xFFFF
        private val PRESENCE_PREFIX = byteArrayOf(0x42, 0x4c, BtxConstants.PROTOCOL_MAJOR.toByte())
        private val PRESENCE_PREFIX_MASK = byteArrayOf(0xff.toByte(), 0xff.toByte(), 0xff.toByte())
        private val RENDEZVOUS_PREFIX = byteArrayOf(BtxConstants.PROTOCOL_MAJOR.toByte(), 2)
        private val RENDEZVOUS_PREFIX_MASK = byteArrayOf(0xff.toByte(), 0xff.toByte())
        private fun presencePayload(platform: PeerPlatform, presenceId: ByteArray): ByteArray = ByteArray(12).also { value ->
            value[0] = 0x42
            value[1] = 0x4c
            value[2] = BtxConstants.PROTOCOL_MAJOR.toByte()
            value[3] = when (platform) { PeerPlatform.ANDROID -> 1; PeerPlatform.WINDOWS -> 2; else -> 0 }
            presenceId.copyInto(value, destinationOffset = 4)
        }

        private fun rendezvousPayload(platform: PeerPlatform, presenceId: ByteArray): ByteArray = ByteArray(8).also { value ->
            value[0] = BtxConstants.PROTOCOL_MAJOR.toByte()
            value[1] = when (platform) { PeerPlatform.ANDROID -> 1; PeerPlatform.WINDOWS -> 2; else -> 0 }
            presenceId.copyInto(value, destinationOffset = 2, endIndex = 6)
        }

        private fun parsePresence(value: ByteArray): Presence? {
            if (value.size < 12 || value[0] != 0x42.toByte() || value[1] != 0x4c.toByte()) return null
            val platform = when (value[3].toInt()) { 1 -> PeerPlatform.ANDROID; 2 -> PeerPlatform.WINDOWS; else -> PeerPlatform.UNKNOWN }
            val id = value.copyOfRange(4, 12).joinToString("") { "%02X".format(Locale.ROOT, it) }
            return Presence(value[2].toInt() and 0xff, platform, id)
        }

        private fun parseRendezvous(value: ByteArray): Presence? {
            if (value.size < 8 || (value[0].toInt() and 0xff) != BtxConstants.PROTOCOL_MAJOR) return null
            val platform = when (value[1].toInt()) { 1 -> PeerPlatform.ANDROID; 2 -> PeerPlatform.WINDOWS; else -> PeerPlatform.UNKNOWN }
            val id = value.copyOfRange(2, 8).joinToString("") { "%02X".format(Locale.ROOT, it) }
            return Presence(value[0].toInt() and 0xff, platform, id)
        }

        private fun formatAddress(value: Long): String = (5 downTo 0).joinToString(":") { index ->
            "%02X".format(Locale.ROOT, (value ushr (index * 8)) and 0xff)
        }
    }
}
