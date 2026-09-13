package com.bluelink.android

import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.os.ParcelUuid
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.bluelink.core.BtxConstants
import kotlinx.coroutines.delay
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/** Bounded, read-only observation of the real app runtime and BlueLink BLE advertisements. */
@SuppressLint("MissingPermission")
@Composable
internal fun PhysicalDiscoveryAcceptance(close: () -> Unit) {
    val context = LocalContext.current
    val runtime = (context.applicationContext as BlueLinkApplication).runtime
    var status by remember { mutableStateOf("Observing BlueLink discovery…") }
    LaunchedEffect(Unit) {
        runtime.onAppForegrounded()
        val scanner = context.getSystemService(BluetoothManager::class.java).adapter?.bluetoothLeScanner
        val packets = AtomicInteger()
        val received = ConcurrentHashMap<String, JSONObject>()
        val rendezvous = ParcelUuid(BtxConstants.BLE_RENDEZVOUS_UUID)
        val callback = object : ScanCallback() {
            override fun onScanResult(callbackType: Int, result: ScanResult) {
                packets.incrementAndGet()
                val record = result.scanRecord ?: return
                if (record.serviceUuids?.contains(rendezvous) != true && record.getServiceData(rendezvous) == null) return
                val suffix = result.device.address.takeLast(5)
                received["$suffix:${record.getServiceData(rendezvous)?.size ?: 0}:${record.serviceUuids}"] = JSONObject().put("suffix", suffix).put("connectable", result.isConnectable)
                    .put("legacy", result.isLegacy).put("rssi", result.rssi)
                    .put("serviceUuids", record.serviceUuids.toString())
                    .put("serviceDataBytes", record.getServiceData(rendezvous)?.size ?: 0)
            }
            override fun onScanFailed(errorCode: Int) { status = "Observation scan failed: $errorCode" }
        }
        val target = File(context.cacheDir, "acceptance/discovery.json").apply { parentFile?.mkdirs() }
        try {
            scanner?.startScan(null, ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
                .setLegacy(false).build(), callback)
            repeat(20) {
                delay(1000)
                target.writeText(JSONObject().put("packets", packets.get()).put("blueLinkPackets", JSONArray(received.values))
                    .put("discovery", runtime.bluetooth.discovery.value.toString())
                    .put("devices", JSONArray(runtime.bluetooth.devices.value.map { device ->
                        JSONObject().put("suffix", device.address.takeLast(5)).put("platform", device.platform)
                            .put("connectable", device.connectable).put("rendezvous", device.rendezvousAvailable)
                    }))
                    .put("events", JSONArray(runtime.diagnostics.value.map { entry ->
                        JSONObject().put("time", entry.timestamp.toString()).put("component", entry.component).put("message", entry.message)
                    })).toString(2))
            }
            status = "Observation saved: ${received.size} BlueLink endpoints"
        } finally { runCatching { scanner?.stopScan(callback) } }
    }
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("BlueLink QA · Physical discovery")
        Text(status)
        Button(onClick = close) { Text("Return to BlueLink") }
    }
}
