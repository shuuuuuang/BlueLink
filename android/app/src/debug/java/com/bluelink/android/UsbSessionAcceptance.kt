package com.bluelink.android

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.bluelink.android.domain.*
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.devices.*
import com.bluelink.android.usb.*
import java.util.UUID

/** In-memory composition of production screens. No repository, permissions, or USB controller. */
@Composable
internal fun UsbSessionAcceptance(scene: String, close: () -> Unit) {
    var enabled by remember { mutableStateOf(scene != "usb-session-off") }
    val ready = scene !in setOf("usb-session-bt", "usb-session-fallback", "usb-session-unrelated", "usb-session-verifying", "usb-session-unverified")
    val state = if (ready) ConnectionPhase.CONNECTED else ConnectionPhase.TRUST_REQUIRED
    val usb = ManagedSessionState(UUID(0, 1), "qa-usb-a", "DESKTOP-NAVI", "usb:qa", state, "", 0, SessionTransport.USB)
    val bt = usb.copy(sessionId = UUID(0, 2), phase = ConnectionPhase.CONNECTED, transport = SessionTransport.BLUETOOTH)
    val peers = listOf(
        ConversationSummary("qa-usb-a", if (scene == "usb-session-long") "DESKTOP-NAVI 非常长的设备名称检查闪电与未读徽标" else "DESKTOP-NAVI", PeerPlatform.WINDOWS,
            DeviceAvailability.CONNECTED, unreadCount = 3, usbReady = UsbStatePolicy.isReady(enabled, "qa-usb-a", listOf(usb, bt)),
            transport = if (ready) SessionTransport.USB else SessionTransport.BLUETOOTH),
        ConversationSummary("qa-bt-b", "Bluetooth PC", PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED, unreadCount = 7),
        ConversationSummary("qa-offline", "Offline phone", PeerPlatform.ANDROID, DeviceAvailability.OFFLINE))
    var selected by remember { mutableStateOf(if (scene in setOf("usb-session-list", "usb-session-long")) null else
        if (scene in setOf("usb-session-bt", "usb-session-unrelated")) "qa-bt-b" else "qa-usb-a") }
    val notice = UsbSnapshot(if (scene == "usb-session-verifying") UsbStage.NEGOTIATING else
        if (scene in setOf("usb-session-fallback", "usb-session-unrelated")) UsbStage.FALLBACK else UsbStage.WAITING,
        peerId = "qa-usb-a", bluetoothAvailable = true)
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        if (selected == null) {
            DeviceHomeHeader(BluetoothAccessState.READY, 2)
            DevicesScreen(Modifier.weight(1f), emptyList(), peers, emptyList(), ConnectionState(ConnectionPhase.CONNECTED),
                DiscoveryState(), BluetoothAccessState.READY, null, null, {}, {}, {}, { selected = it }, {}, {})
        } else ConversationScreen(Modifier.weight(1f), selected, emptyList(), ConnectionState(ConnectionPhase.CONNECTED),
            peers, emptyList(), true, "downloads://BlueLink", { selected = null }, {}, {}, {}, {}, {}, {}, {}, {}, {},
            usbEnabled = enabled, usbSnapshot = notice)
        Row(Modifier.fillMaxWidth().height(40.dp), horizontalArrangement = Arrangement.SpaceEvenly) {
            TextButton(onClick = { enabled = !enabled }) { Text("QA USB " + if (enabled) "ON" else "OFF") }
            TextButton(onClick = close) { Text("QA Close") }
        }
    }
}
