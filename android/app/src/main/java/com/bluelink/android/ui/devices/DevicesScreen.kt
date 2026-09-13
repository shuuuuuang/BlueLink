package com.bluelink.android.ui.devices

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.ExperimentalMaterialApi
import androidx.compose.material.pullrefresh.PullRefreshIndicator
import androidx.compose.material.pullrefresh.pullRefresh
import androidx.compose.material.pullrefresh.rememberPullRefreshState
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import android.content.Context
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/** Figma 365:4/81/153, 379:3/83/160/237, 562:3402/3493/3584/3676. */
@OptIn(ExperimentalMaterialApi::class)
@Composable
fun DevicesScreen(
    modifier: Modifier,
    devices: List<NearbyDevice>,
    conversations: List<ConversationSummary>,
    transfers: List<TransferItem>,
    state: ConnectionState,
    discoveryState: DiscoveryState,
    access: BluetoothAccessState,
    selectedPeerId: String?,
    connectingDeviceKey: String?,
    discover: () -> Unit,
    connect: (NearbyDevice) -> Unit,
    permissionAction: () -> Unit,
    openConversation: (String) -> Unit,
    longPressConversation: (ConversationSummary) -> Unit,
    longPressNearby: (NearbyDevice) -> Unit,
) = DeviceScreenTheme {
    val context = LocalContext.current
    var query by rememberSaveable { mutableStateOf("") }
    var connectedExpanded by rememberSaveable { mutableStateOf(true) }
    var offlineExpanded by rememberSaveable { mutableStateOf(true) }
    var nearbyExpanded by rememberSaveable { mutableStateOf(true) }
    val projection = remember(conversations, devices, access, query) {
        DeviceScreenState.project(conversations, devices, access, query)
    }
    val canRefresh = access.canUseBluetooth && !discoveryState.scanning
    val refreshState = rememberPullRefreshState(discoveryState.scanning, onRefresh = {
        if (canRefresh) discover()
    })
    Column(modifier.fillMaxSize().background(DeviceColors.Canvas)) {
        DeviceSearch(query, { query = it }, access)
        Box(Modifier.fillMaxSize().pullRefresh(refreshState, enabled = canRefresh).semantics {
            if (canRefresh) customActions = listOf(CustomAccessibilityAction(context.getString(R.string.device_refresh)) {
                discover(); true
            })
        }) {
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 18.dp, bottom = 24.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (projection.noResults) {
                    item("no-results") {
                        Surface(shape = RoundedCornerShape(14.dp), border = androidx.compose.foundation.BorderStroke(1.dp, DeviceColors.Border)) {
                            Column(Modifier.fillMaxWidth().heightIn(min = 202.dp).padding(28.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                                FigmaIcon(R.drawable.figma_search_empty, size = 64.dp)
                                Spacer(Modifier.height(13.dp))
                                Text(context.getString(R.string.device_no_results), fontSize = 17.sp, lineHeight = 25.sp)
                                Spacer(Modifier.height(7.dp))
                                Text(context.getString(R.string.device_search_hint), color = DeviceColors.Secondary, fontSize = 13.sp)
                            }
                        }
                    }
                } else {
                    if (!projection.searching || projection.connected.isNotEmpty()) {
                        item("connected-header") { DeviceGroup(context.getString(R.string.device_connected), projection.connected.size, connectedExpanded,
                            toggle = { connectedExpanded = !connectedExpanded }) }
                        if (connectedExpanded) {
                            if (projection.connected.isEmpty()) item("connected-empty") {
                                EmptyDeviceState(context.getString(R.string.device_no_connected), if (access == BluetoothAccessState.OFF) context.getString(R.string.device_reconnect_bluetooth) else context.getString(R.string.device_history_available))
                            }
                            items(projection.connected, key = { "connected-${it.peerId}" }) { summary ->
                                val transfer = DeviceActions.transfer(summary.peerId, transfers)
                                HistoryDeviceCard(summary, transfer, selectedPeerId == summary.peerId,
                                    { openConversation(summary.peerId) }, { longPressConversation(summary) })
                            }
                        }
                    }
                    if (!projection.searching || projection.offline.isNotEmpty()) {
                        item("offline-header") { DeviceGroup(context.getString(R.string.device_offline), projection.offline.size, offlineExpanded,
                            toggle = { offlineExpanded = !offlineExpanded }) }
                        if (offlineExpanded) {
                            if (projection.offline.isEmpty()) item("offline-empty") {
                                EmptyDeviceState(context.getString(R.string.device_no_offline), context.getString(R.string.device_history_here))
                            }
                            items(projection.offline, key = { "offline-${it.peerId}" }) { summary ->
                                HistoryDeviceCard(summary, null, selectedPeerId == summary.peerId,
                                    { openConversation(summary.peerId) }, { longPressConversation(summary) })
                            }
                        }
                    }
                    if (!projection.searching || projection.nearby.isNotEmpty()) {
                        item("nearby-header") {
                            DeviceGroup(context.getString(R.string.device_nearby), projection.nearby.size, nearbyExpanded,
                                toggle = { nearbyExpanded = !nearbyExpanded })
                        }
                        if (nearbyExpanded) {
                            when (access) {
                                BluetoothAccessState.REQUIRED, BluetoothAccessState.DENIED -> item("permissions") {
                                    BluetoothPermissionCard(access == BluetoothAccessState.DENIED, permissionAction)
                                }
                                BluetoothAccessState.OFF -> item("bluetooth-off") { BluetoothOffCard() }
                                BluetoothAccessState.READY -> {
                                    if (projection.nearby.isEmpty()) item("nearby-empty") {
                                        EmptyDeviceState(if (discoveryState.scanning) context.getString(R.string.device_scanning_nearby) else context.getString(R.string.device_no_nearby),
                                            if (discoveryState.scanning) context.getString(R.string.device_keep_bluetooth) else context.getString(R.string.device_check_peer))
                                    }
                                    items(projection.nearby, key = { "nearby-${it.stableKey.ifBlank { it.address }}" }) { device ->
                                        val target = connectingDeviceKey == device.stableKey.ifBlank { device.address } &&
                                            state.transportAddress.equals(device.address, ignoreCase = true)
                                        NearbyDeviceCard(device, scanning = discoveryState.scanning,
                                            connecting = target && state.phase in setOf(ConnectionPhase.CONNECTING, ConnectionPhase.SECURE_HANDSHAKE, ConnectionPhase.TRUST_REQUIRED),
                                            failed = target && state.phase == ConnectionPhase.DISCONNECTED,
                                            connect = { connect(device) }, longPress = { longPressNearby(device) })
                                    }
                                }
                            }
                        }
                    }
                }
            }
            PullRefreshIndicator(discoveryState.scanning, refreshState, Modifier.align(Alignment.TopCenter)
                .semantics { if (discoveryState.scanning) contentDescription = context.getString(R.string.device_scanning_nearby) },
                backgroundColor = DeviceColors.Surface, contentColor = DeviceColors.Blue)
        }
    }
}

@Composable
private fun DeviceSearch(query: String, update: (String) -> Unit, access: BluetoothAccessState) {
    val context = LocalContext.current
    val permissionMissing = access == BluetoothAccessState.REQUIRED || access == BluetoothAccessState.DENIED
    BasicTextField(value = query, onValueChange = update, singleLine = true,
        textStyle = TextStyle(fontFamily = DeviceFont, fontSize = 13.sp, lineHeight = 20.sp, color = DeviceColors.Ink),
        cursorBrush = SolidColor(DeviceColors.Blue),
        modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 12.dp).fillMaxWidth().heightIn(min = 42.dp)
            .semantics { contentDescription = if (access.canUseBluetooth) context.getString(R.string.device_search) else context.getString(R.string.device_search_history) },
        decorationBox = { input ->
            Row(Modifier.fillMaxWidth().alpha(if (permissionMissing && query.isEmpty()) .55f else 1f)
                .background(DeviceColors.Surface, RoundedCornerShape(10.dp)).border(1.dp, DeviceColors.Border, RoundedCornerShape(10.dp))
                .padding(horizontal = 12.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                FigmaIcon(R.drawable.figma_search, size = 20.dp)
                Spacer(Modifier.width(10.dp))
                Box(Modifier.weight(1f)) {
                    if (query.isEmpty()) Text(if (access.canUseBluetooth) context.getString(R.string.device_search) else context.getString(R.string.device_search_history),
                        color = Color(0xFF94A3B8), fontSize = 13.sp, lineHeight = 20.sp)
                    input()
                }
            }
        })
}

@Composable
private fun DeviceGroup(label: String, count: Int, expanded: Boolean, toggle: () -> Unit) {
    val context = LocalContext.current
    Row(Modifier.fillMaxWidth().heightIn(min = 42.dp), verticalAlignment = Alignment.CenterVertically) {
        Row(Modifier.weight(1f).clickable(role = Role.Button, onClick = toggle)
            .semantics { stateDescription = if (expanded) context.getString(R.string.device_expanded) else context.getString(R.string.device_collapsed) }
            .padding(start = 4.dp, top = 4.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(label, color = DeviceColors.Ink, fontSize = 14.sp)
            Spacer(Modifier.width(6.dp))
            Text("($count)", color = DeviceColors.Secondary, fontSize = 12.sp)
        }
        Box(Modifier.padding(start = 8.dp).size(28.dp).clickable(role = Role.Button, onClick = toggle)
            .semantics { contentDescription = context.getString(if (expanded) R.string.content_collapse else R.string.content_expand, label) }, contentAlignment = Alignment.Center) {
            FigmaIcon(R.drawable.figma_chevron, Modifier.rotate(if (expanded) 0f else -90f), size = 20.dp)
        }
    }
}

@Composable
private fun EmptyDeviceState(title: String, detail: String) {
    val outline = DeviceColors.Border
    Row(Modifier.fillMaxWidth().heightIn(min = 72.dp).background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(12.dp))
        .drawBehind {
            drawRoundRect(outline, cornerRadius = CornerRadius(12.dp.toPx()),
                style = Stroke(1.dp.toPx(), pathEffect = PathEffect.dashPathEffect(floatArrayOf(4.dp.toPx(), 4.dp.toPx()))))
        }.padding(horizontal = 16.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(38.dp).background(DeviceColors.Canvas, RoundedCornerShape(10.dp)), contentAlignment = Alignment.Center) {
            FigmaIcon(R.drawable.figma_generic)
        }
        Spacer(Modifier.width(14.dp))
        Column(Modifier.weight(1f)) {
            Text(title, fontSize = 13.sp, lineHeight = 22.sp)
            Text(detail, fontSize = 11.sp, lineHeight = 18.sp, color = DeviceColors.Secondary)
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun HistoryDeviceCard(summary: ConversationSummary, transfer: TransferItem?, selected: Boolean,
                              open: () -> Unit, longPress: () -> Unit) {
    val context = LocalContext.current
    val haptics = LocalHapticFeedback.current
    val online = summary.availability == DeviceAvailability.CONNECTED
    Surface(color = if (selected) DeviceColors.Selected else DeviceColors.Surface, shape = RoundedCornerShape(12.dp),
        border = androidx.compose.foundation.BorderStroke(1.dp, if (selected) DeviceColors.Blue else DeviceColors.Border),
        modifier = Modifier.fillMaxWidth().combinedClickable(onClick = open, onLongClick = {
            haptics.performHapticFeedback(HapticFeedbackType.LongPress); longPress()
        })) {
        Row(Modifier.heightIn(min = if (transfer == null) 76.dp else 88.dp).padding(horizontal = 12.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically) {
            PlatformIcon(summary.platform, online || selected)
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                DeviceNameWithUsb(summary.peerName, online && summary.usbReady)
                val detail = when {
                    transfer != null -> deviceTransferDetail(transfer, context)
                    online && selected -> context.getString(R.string.device_current_conversation)
                    online -> context.getString(R.string.device_connected)
                    summary.availability == DeviceAvailability.CONNECTABLE -> context.getString(R.string.device_nearby_connectable)
                    summary.lastConnectedAt != null -> context.getString(R.string.device_last_connection, formatLastConnection(summary.lastConnectedAt, context))
                    else -> context.getString(R.string.device_offline_history)
                }
                Text(detail, fontSize = 11.sp, lineHeight = 20.sp, color = DeviceColors.Secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (transfer != null) {
                    Spacer(Modifier.height(6.dp))
                    LinearProgressIndicator(progress = { transfer.progress.coerceIn(0f, 1f) }, color = DeviceColors.Blue,
                        trackColor = MaterialTheme.colorScheme.outlineVariant, modifier = Modifier.fillMaxWidth().height(4.dp))
                }
            }
            Spacer(Modifier.width(12.dp))
            if (summary.unreadCount > 0) Box(Modifier.height(26.dp).widthIn(min = 26.dp).background(DeviceColors.Blue, CircleShape).padding(horizontal = 6.dp), contentAlignment = Alignment.Center) {
                Text(if (summary.unreadCount > 99) "99+" else summary.unreadCount.toString(), color = MaterialTheme.colorScheme.onPrimary, fontSize = 11.sp, lineHeight = 16.sp)
            } else Box(Modifier.padding(horizontal = 8.dp).size(10.dp).background(
                if (online) DeviceColors.Success else Color(0xFF94A3B8), CircleShape))
        }
    }
}

@Composable
private fun PlatformIcon(platform: PeerPlatform, active: Boolean) {
    Box(Modifier.size(44.dp).background(if (platform == PeerPlatform.ANDROID) DeviceColors.Surface else DeviceColors.Canvas,
        RoundedCornerShape(10.dp)), contentAlignment = Alignment.Center) {
        FigmaIcon(when (platform) {
            PeerPlatform.ANDROID -> R.drawable.figma_phone
            PeerPlatform.WINDOWS -> R.drawable.figma_desktop
            PeerPlatform.UNKNOWN -> R.drawable.figma_generic
        }, tint = if (active) DeviceColors.Blue else DeviceColors.Secondary)
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun NearbyDeviceCard(device: NearbyDevice, scanning: Boolean, connecting: Boolean, failed: Boolean,
                             connect: () -> Unit, longPress: () -> Unit) {
    val context = LocalContext.current
    val canConnect = !connecting && DeviceActions.canConnect(device)
    val color = if (failed) DeviceColors.Error else DeviceColors.Blue
    val haptics = LocalHapticFeedback.current
    Surface(shape = RoundedCornerShape(12.dp), border = androidx.compose.foundation.BorderStroke(1.dp,
        if (failed) DeviceColors.Error else DeviceColors.Border),
        modifier = Modifier.fillMaxWidth().combinedClickable(onClick = { if (canConnect) connect() }, onLongClick = {
            haptics.performHapticFeedback(HapticFeedbackType.LongPress); longPress()
        })) {
        Row(Modifier.heightIn(min = 76.dp).padding(horizontal = 12.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            PlatformIcon(device.platform, true)
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(if (scanning && !connecting && !failed) context.getString(R.string.device_scanning_nearby) else device.name,
                    color = DeviceColors.Ink, fontSize = 14.sp, lineHeight = 24.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(when {
                    connecting -> context.getString(R.string.device_establishing)
                    failed -> context.getString(R.string.device_failed)
                    scanning -> context.getString(R.string.device_keep_bluetooth)
                    else -> "${when(device.platform) { PeerPlatform.ANDROID -> "Android"; PeerPlatform.WINDOWS -> "Windows"; else -> context.getString(R.string.device_generic) }}${device.rssi?.let { " · $it dBm" }.orEmpty()}"
                }, color = if (failed) DeviceColors.Error else DeviceColors.Secondary, fontSize = 11.sp, lineHeight = 20.sp,
                    maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            Spacer(Modifier.width(8.dp))
            Button(onClick = connect, enabled = canConnect, shape = RoundedCornerShape(9.dp),
                border = if (connecting) null else androidx.compose.foundation.BorderStroke(1.dp, if (canConnect) color else DeviceColors.Border),
                colors = ButtonDefaults.buttonColors(containerColor = DeviceColors.Surface, contentColor = color,
                    disabledContainerColor = if (connecting) DeviceColors.Selected else DeviceColors.Surface,
                    disabledContentColor = if (connecting) DeviceColors.Blue else DeviceColors.Secondary),
                contentPadding = PaddingValues(horizontal = 14.dp), modifier = Modifier.height(34.dp).widthIn(min = 58.dp)) {
                Text(if (connecting) context.getString(R.string.device_connecting) else if (failed) context.getString(R.string.device_retry) else context.getString(R.string.device_connect), fontFamily = DeviceFont,
                    fontWeight = FontWeight.Normal, fontSize = 12.sp, lineHeight = 18.sp)
            }
        }
    }
}

@Composable
private fun BluetoothPermissionCard(denied: Boolean, permissionAction: () -> Unit) {
    val context = LocalContext.current
    Surface(shape = RoundedCornerShape(16.dp), border = androidx.compose.foundation.BorderStroke(1.dp, DeviceColors.Border)) {
        Column(Modifier.fillMaxWidth().heightIn(min = 220.dp).padding(start = 20.dp, end = 20.dp, top = 20.dp, bottom = 6.dp),
            horizontalAlignment = Alignment.CenterHorizontally) {
            Box(Modifier.size(64.dp).background(if (denied) DeviceColors.Error.copy(alpha = .08f) else DeviceColors.Selected, RoundedCornerShape(20.dp)),
                contentAlignment = Alignment.Center) { FigmaIcon(R.drawable.figma_permission, tint = if (denied) DeviceColors.Error else DeviceColors.Blue) }
            Spacer(Modifier.height(6.dp))
            Text(if (denied) context.getString(R.string.device_permission_denied) else context.getString(R.string.device_permission_needed), fontSize = 17.sp, lineHeight = 25.sp)
            Box(Modifier.heightIn(min = 38.dp), contentAlignment = Alignment.Center) {
                Text(if (denied) context.getString(R.string.device_permission_denied_body) else context.getString(R.string.device_permission_body),
                    fontSize = 12.sp, lineHeight = 19.sp, color = DeviceColors.Secondary, textAlign = TextAlign.Center)
            }
            Spacer(Modifier.height(7.dp))
            Button(onClick = permissionAction, shape = RoundedCornerShape(10.dp), contentPadding = PaddingValues(0.dp),
                modifier = Modifier.fillMaxWidth().height(40.dp)) {
                Text(if (denied) context.getString(R.string.device_open_settings) else context.getString(R.string.device_allow_permission), fontSize = 13.sp, lineHeight = 20.sp,
                    fontFamily = DeviceFont, fontWeight = FontWeight.Normal)
            }
            Text(if (denied) context.getString(R.string.device_recheck_permission) else context.getString(R.string.device_permission_note),
                fontSize = 9.sp, lineHeight = 14.sp, color = Color(0xFF8FA0B7), textAlign = TextAlign.Center)
        }
    }
}

@Composable
private fun BluetoothOffCard() {
    val context = LocalContext.current
    Surface(shape = RoundedCornerShape(12.dp), border = androidx.compose.foundation.BorderStroke(1.dp, DeviceColors.Border)) {
        Row(Modifier.fillMaxWidth().heightIn(min = 88.dp).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(46.dp).background(DeviceColors.Canvas, RoundedCornerShape(10.dp)), contentAlignment = Alignment.Center) {
                FigmaIcon(R.drawable.figma_bluetooth_off_notice)
            }
            Spacer(Modifier.width(16.dp))
            Column(Modifier.weight(1f)) {
                Text(context.getString(R.string.device_bluetooth_off), fontSize = 14.sp, lineHeight = 24.sp)
                Text(context.getString(R.string.device_bluetooth_off_body), fontSize = 11.sp, lineHeight = 20.sp, color = DeviceColors.Secondary)
            }
        }
    }
}

private fun formatLastConnection(epochMillis: Long, context: Context): String {
    val dateTime = Instant.ofEpochMilli(epochMillis).atZone(ZoneId.systemDefault())
    return if (dateTime.toLocalDate() == java.time.LocalDate.now())
        "${context.getString(R.string.content_today)} ${dateTime.format(DateTimeFormatter.ofPattern("HH:mm"))}"
    else dateTime.format(DateTimeFormatter.ofPattern("MM-dd HH:mm"))
}
