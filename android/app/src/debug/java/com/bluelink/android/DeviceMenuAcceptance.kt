package com.bluelink.android

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.bluelink.android.domain.*
import com.bluelink.android.session.TransferPauseController
import com.bluelink.android.ui.components.BlueLinkConfirmation
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.devices.*
import java.util.UUID

/** Production menus and pause controller with isolated memory data; no real connection or history writes. */
@Composable
internal fun DeviceMenuAcceptance(scene: String, close: () -> Unit) {
    val context = LocalContext.current
    val nearby = remember { NearbyDevice("BlueLink QA Nearby", "00:11:22:33:44:55", false,
        discoveryId = "AABBCCDDEEFF", rssi = -58, platform = PeerPlatform.WINDOWS,
        rendezvousAvailable = true, connectable = true, stableKey = "qa-nearby") }
    var peer by remember { mutableStateOf(ConversationSummary("qa-device-menu", "BlueLink QA PC", PeerPlatform.WINDOWS,
        if (scene.endsWith("offline") || scene.endsWith("zero")) DeviceAvailability.OFFLINE else DeviceAvailability.CONNECTED, isTrusted = true, unreadCount = if (scene.endsWith("unread")) 7 else 0)) }
    var transfer by remember { mutableStateOf<TransferItem?>(null) }
    val controller = remember {
        TransferPauseController { transfer = it }.also { controller ->
            if (scene.substringAfter("device-actions-") in setOf("receiving", "sending", "paused", "remote", "verifying")) {
                controller.report(TransferItem(UUID.nameUUIDFromBytes("qa-device-transfer".toByteArray()), "BlueLink-QA-task.pdf", 1000000,
                    680000, scene.endsWith("sending"), TransferStatus.TRANSFERRING, peerId = peer.peerId))
                if (scene.endsWith("paused")) controller.setPaused(true, true)
                if (scene.endsWith("remote")) controller.setPaused(false, true)
                if (scene.endsWith("verifying")) controller.report(transfer!!.copy(status = TransferStatus.VERIFYING))
            }
        }
    }
    var menu by remember { mutableStateOf(scene.startsWith("device-actions-") && !scene.endsWith("nearby")) }
    var nearbyMenu by remember { mutableStateOf(scene.endsWith("nearby")) }
    var info by remember { mutableStateOf<ConversationSummary?>(null) }
    var confirmation by remember { mutableStateOf<DeviceAction?>(null) }
    var view by remember { mutableIntStateOf(0) }
    var searchRequested by remember { mutableStateOf(false) }
    var lastAction by remember { mutableStateOf("") }
    val rows = listOfNotNull(transfer)
    val peers = if (scene.startsWith("device-state-")) listOf(peer,
        peer.copy(peerId = "qa-offline-menu", peerName = "BlueLink QA Offline", availability = DeviceAvailability.OFFLINE,
            unreadCount = 2, lastConnectedAt = 1788854400000)) else listOf(peer)
    val connecting = scene.endsWith("connecting")
    val failed = scene.endsWith("failed")
    val connection = ConnectionState(when {
        connecting -> ConnectionPhase.CONNECTING
        failed -> ConnectionPhase.DISCONNECTED
        else -> ConnectionPhase.CONNECTED
    }, transportAddress = nearby.address)
    Column(Modifier.fillMaxSize().background(DeviceColors.Canvas)) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp)) {
            Text("BlueLink · QA $lastAction", Modifier.weight(1f))
            TextButton(onClick = close) { Text(context.getString(R.string.close)) }
        }
        if (view == 0) {
            DeviceHomeHeader(BluetoothAccessState.READY, if (peer.availability == DeviceAvailability.CONNECTED) 1 else 0)
            DevicesScreen(Modifier.weight(1f), listOf(nearby), peers, rows,
                connection, DiscoveryState(scanning = scene.endsWith("scanning")), BluetoothAccessState.READY,
                peer.peerId, if (connecting || failed) nearby.stableKey else null, {}, { lastAction = "CONNECT" }, {}, { view = 1 }, { menu = true }, { nearbyMenu = true })
        } else ConversationScreen(Modifier.weight(1f), peer.peerId, emptyList(),
            ConnectionState(ConnectionPhase.CONNECTED), listOf(peer), rows, true, "downloads://BlueLink",
            { view = 0 }, { menu = true }, {}, {}, {}, {}, {}, {}, {}, {}, requestedTab = if (view == 2) 1 else 0,
            searchRequested = searchRequested, searchRequestHandled = { searchRequested = false })
    }
    if (menu) DeviceActionSheet(peer, DeviceActions.transfer(peer.peerId, rows), false, { menu = false },
        searchConversation = if (view != 0) { { searchRequested = true } } else null) { action ->
        lastAction = action.name
        when (action) {
            DeviceAction.OPEN -> view = 1
            DeviceAction.TRANSFERS -> view = 2
            DeviceAction.INFO -> info = peer
            DeviceAction.PAUSE -> controller.setPaused(true, true)
            DeviceAction.RESUME -> controller.setPaused(true, false)
            DeviceAction.CLEAR, DeviceAction.REMOVE_TRUST -> confirmation = action
            DeviceAction.DISCONNECT -> peer = peer.copy(availability = DeviceAvailability.OFFLINE)
            DeviceAction.CONNECT -> Unit
        }
    }
    if (nearbyMenu) NearbyDeviceActionSheet(nearby, true, { nearbyMenu = false }, { lastAction = "CONNECT" }, {
        info = ConversationSummary(nearby.discoveryId, nearby.name, nearby.platform, DeviceAvailability.CONNECTABLE)
    })
    info?.let { DeviceDetailsPrompt(it) { info = null } }
    confirmation?.let { action ->
        val title = context.getString(if (action == DeviceAction.CLEAR) R.string.content_clear_conversation else R.string.content_remove_trust)
        val message = context.getString(if (action == DeviceAction.CLEAR) R.string.content_clear_conversation_body else R.string.content_remove_trust_body, peer.peerName)
        BlueLinkConfirmation(title, message, context.getString(R.string.content_keep_files),
            context.getString(if (action == DeviceAction.CLEAR) R.string.content_clear else R.string.content_remove),
            dismiss = { confirmation = null }, confirm = { confirmation = null; lastAction = "QA_CONFIRMED" })
    }
}
