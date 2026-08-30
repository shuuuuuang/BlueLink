package com.bluelink.android

import android.Manifest
import android.app.DownloadManager
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.database.Cursor
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.Send
import androidx.compose.material.icons.rounded.AttachFile
import androidx.compose.material.icons.rounded.Description
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.core.content.FileProvider
import com.bluelink.android.domain.ChatItem
import com.bluelink.android.domain.ChatAttachment
import com.bluelink.android.domain.ConnectionPhase
import com.bluelink.android.domain.ConnectionState
import com.bluelink.android.domain.ConversationSummary
import com.bluelink.android.domain.DeviceAvailability
import com.bluelink.android.domain.DiscoveryState
import com.bluelink.android.domain.DiagnosticEntry
import com.bluelink.android.domain.DiagnosticLevel
import com.bluelink.android.domain.NearbyDevice
import com.bluelink.android.domain.PeerPlatform
import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TransferStatus
import com.bluelink.android.data.local.AppSettings
import com.bluelink.android.files.FileInteraction
import com.bluelink.android.service.BluetoothSessionService
import com.bluelink.android.ui.components.BlueLinkAppHeader
import com.bluelink.android.ui.components.BlueLinkBottomNavigation
import com.bluelink.android.ui.components.BlueLinkLogo
import com.bluelink.android.ui.components.DevicePlatformIcon
import com.bluelink.android.ui.theme.BlueLinkTheme
import com.bluelink.android.ui.theme.Border
import com.bluelink.android.ui.theme.Danger
import com.bluelink.android.ui.theme.Lavender
import com.bluelink.android.ui.theme.SoftBlue
import com.bluelink.android.ui.theme.SoftWarning
import com.bluelink.android.ui.theme.Success
import java.time.ZoneId
import java.time.Duration
import java.time.Instant
import java.time.format.DateTimeFormatter
import java.io.File
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { BlueLinkApp(onPermissionsReady = ::startBluetoothService, fileInfo = ::fileInfo) }
    }

    private fun startBluetoothService() {
        startForegroundService(Intent(this, BluetoothSessionService::class.java))
    }

    override fun onStart() {
        super.onStart()
        (application as BlueLinkApplication).runtime.onAppForegrounded()
    }

    override fun onStop() {
        val runtime = (application as BlueLinkApplication).runtime
        runtime.onAppBackgrounded()
        if (!runtime.keepBackgroundSessionsEnabled())
            stopService(Intent(this, BluetoothSessionService::class.java))
        super.onStop()
    }

    private fun fileInfo(uri: android.net.Uri): Pair<String, Long> {
        var name = "文件"
        var size = 0L
        contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                name = cursor.value(OpenableColumns.DISPLAY_NAME) ?: name
                size = cursor.value(OpenableColumns.SIZE)?.toLongOrNull() ?: 0L
            }
        }
        return name to size
    }

    private fun Cursor.value(column: String): String? = getColumnIndex(column).takeIf { it >= 0 }?.let(::getString)
}

private val Blue = Color(0xFF176BFF)
private val Ink = Color(0xFF162033)
private val Canvas = Color(0xFFF5F7FB)
private val Muted = Color(0xFF687386)

private data class ActionOption(val label: String, val destructive: Boolean = false, val action: () -> Unit)
private data class ConfirmAction(val title: String, val message: String, val confirmLabel: String, val action: () -> Unit)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ActionSheet(title: String, detail: String, actions: List<ActionOption>, dismiss: () -> Unit) {
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = Canvas, shape = RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp)) {
        Column(Modifier.fillMaxWidth().padding(start = 22.dp, end = 22.dp, bottom = 24.dp)) {
            Text(title, color = Ink, fontWeight = FontWeight.Bold, style = MaterialTheme.typography.titleLarge,
                maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
            Text(detail, color = Muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 4.dp, bottom = 14.dp))
            actions.forEach { option ->
                Surface(color = Color.White, shape = RoundedCornerShape(14.dp),
                    modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp).clickable {
                        dismiss(); option.action()
                    }) {
                    Text(option.label, color = if (option.destructive) Danger else Ink,
                        fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(horizontal = 16.dp, vertical = 14.dp))
                }
            }
            TextButton(onClick = dismiss, modifier = Modifier.align(Alignment.End).padding(top = 4.dp)) { Text("取消") }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun BlueLinkApp(
    onPermissionsReady: () -> Unit,
    fileInfo: (android.net.Uri) -> Pair<String, Long>,
    model: MainViewModel = viewModel(),
) {
    val devices by model.devices.collectAsStateWithLifecycle()
    val discovery by model.discovery.collectAsStateWithLifecycle()
    val connection by model.connection.collectAsStateWithLifecycle()
    val messages by model.messages.collectAsStateWithLifecycle()
    val transfers by model.transfers.collectAsStateWithLifecycle()
    val trust by model.trustPrompt.collectAsStateWithLifecycle()
    val diagnostics by model.diagnostics.collectAsStateWithLifecycle()
    val conversations by model.conversations.collectAsStateWithLifecycle()
    val settings by model.settings.collectAsStateWithLifecycle()
    var previewAttachment by remember { mutableStateOf<ChatAttachment?>(null) }
    var actionConversation by remember { mutableStateOf<ConversationSummary?>(null) }
    var actionNearbyDevice by remember { mutableStateOf<NearbyDevice?>(null) }
    var actionMessage by remember { mutableStateOf<ChatItem?>(null) }
    var actionAttachment by remember { mutableStateOf<ChatAttachment?>(null) }
    var actionTransfer by remember { mutableStateOf<TransferItem?>(null) }
    var pendingSaveAttachment by remember { mutableStateOf<ChatAttachment?>(null) }
    var infoConversation by remember { mutableStateOf<ConversationSummary?>(null) }
    var infoTransfer by remember { mutableStateOf<TransferItem?>(null) }
    var confirmAction by remember { mutableStateOf<ConfirmAction?>(null) }
    var selected by remember { mutableIntStateOf(0) }
    var showSettings by remember { mutableStateOf(false) }
    var settingsPage by remember { mutableIntStateOf(0) }
    val context = LocalContext.current
    var bluetoothPermissionsGranted by remember {
        mutableStateOf(
            context.checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) == PackageManager.PERMISSION_GRANTED &&
                context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED
        )
    }
    var showPermissionGuide by remember { mutableStateOf(!bluetoothPermissionsGranted) }
    val permissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
        val connectGranted = result[Manifest.permission.BLUETOOTH_CONNECT] == true ||
            context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED
        val scanGranted = result[Manifest.permission.BLUETOOTH_SCAN] == true ||
            context.checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) == PackageManager.PERMISSION_GRANTED
        bluetoothPermissionsGranted = connectGranted && scanGranted
        if (connectGranted) onPermissionsReady()
        if (bluetoothPermissionsGranted) model.discover()
    }
    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            runCatching { context.contentResolver.takePersistableUriPermission(uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION) }
            val (name, size) = fileInfo(uri)
            model.sendFile(uri, name, size)
        }
    }
    val directoryPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            runCatching { context.contentResolver.takePersistableUriPermission(uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION) }
            model.saveSettings(settings.copy(downloadDirectory = uri.toString()))
        }
    }
    val localUpdatePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) runCatching {
            context.startActivity(Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, "application/vnd.android.package-archive")
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            })
        }.onFailure { Toast.makeText(context, it.message ?: "无法打开安装包", Toast.LENGTH_SHORT).show() }
    }
    val saveCopyPicker = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("*/*")) { uri ->
        val attachment = pendingSaveAttachment
        pendingSaveAttachment = null
        if (uri != null && attachment != null) runCatching { FileInteraction.copyTo(context, attachment, uri) }
            .onSuccess { Toast.makeText(context, "副本已保存", Toast.LENGTH_SHORT).show() }
            .onFailure { Toast.makeText(context, it.message ?: "保存失败", Toast.LENGTH_SHORT).show() }
    }

    LaunchedEffect(Unit) {
        if (context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED)
            onPermissionsReady()
        val missing = BLUETOOTH_PERMISSIONS.filter {
            context.checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED
        }.toTypedArray()
        if (missing.isNotEmpty()) showPermissionGuide = true
    }

    BlueLinkTheme {
        Scaffold(
            containerColor = Canvas,
            topBar = {
                BlueLinkAppHeader(connection, settings = showSettings,
                    onBack = if (showSettings) ({ showSettings = false }) else null,
                    onSettings = if (!showSettings) ({ showSettings = true }) else null)
            },
            bottomBar = {
                if (!showSettings) BlueLinkBottomNavigation(selected) { selected = it }
            },
        ) { padding ->
            if (showSettings) SettingsScreen(Modifier.padding(padding), settings, conversations,
                selectedPage = settingsPage, selectPage = { settingsPage = it },
                save = model::saveSettings, chooseDirectory = { directoryPicker.launch(null) },
                forgetPeer = model::forgetPeer, clearChat = model::clearChatHistory,
                clearTransfers = model::clearTransferHistory, fingerprint = model.identityFingerprint,
                diagnostics = diagnostics,
                checkLocalUpdate = { localUpdatePicker.launch(arrayOf("application/vnd.android.package-archive")) })
            else when (selected) {
                0 -> DevicesScreen(
                    Modifier.padding(padding), devices, conversations, transfers.values.toList(), connection, discovery,
                    discover = {
                        if (bluetoothPermissionsGranted) model.discover()
                        else showPermissionGuide = true
                    }, connect = model::connect,
                    openConversation = { peerId -> model.selectPeer(peerId); selected = 1 },
                    longPressConversation = { actionConversation = it },
                    longPressNearby = { actionNearbyDevice = it },
                )
                1 -> ChatScreen(Modifier.padding(padding), messages, connection, conversations,
                    showImageThumbnails = settings.showImageThumbnails,
                    selectConversation = model::selectPeer, send = model::sendMessage,
                    pickFile = { filePicker.launch(arrayOf("*/*")) },
                    longPressConversation = { actionConversation = it },
                    longPressMessage = { actionMessage = it },
                    longPressAttachment = { actionAttachment = it },
                    openAttachment = { attachment ->
                        if (attachment.isImage) previewAttachment = attachment
                        else runCatching { FileInteraction.open(context, attachment) }
                            .onFailure { Toast.makeText(context, it.message ?: "无法打开文件", Toast.LENGTH_SHORT).show() }
                    })
                2 -> TransferScreen(Modifier.padding(padding), transfers.values.sortedByDescending { it.id },
                    retry = model::retryTransfer, pause = model::pauseTransfer,
                    resume = model::resumeTransfer, cancel = model::cancelTransfer,
                    longPressTransfer = { actionTransfer = it },
                    openFilesSettings = { settingsPage = 1; showSettings = true })
                else -> DiagnosticsScreen(Modifier.padding(padding), diagnostics, connection, discovery,
                    clear = model::clearDiagnostics)
            }
        }

        trust?.let { prompt ->
            AlertDialog(
                onDismissRequest = {},
                title = { Text("确认安全代码") },
                text = {
                    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Text("请确认 ${prompt.peerName} 上显示相同代码。")
                        Text(prompt.safetyCode, style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.Black, color = Blue)
                        Text("对端身份：${prompt.peerId.take(20)}…\n本机身份：${model.identityFingerprint}",
                            color = Muted, style = MaterialTheme.typography.bodySmall)
                        Text("确认后会固定此设备的身份密钥。", color = Muted)
                    }
                },
                confirmButton = { Button(onClick = { model.confirmTrust(true) }) { Text("两边一致") } },
                dismissButton = { TextButton(onClick = { model.confirmTrust(false) }) { Text("拒绝") } },
            )
        }
        previewAttachment?.let { attachment ->
            ImagePreviewDialog(attachment, dismiss = { previewAttachment = null })
        }
        if (showPermissionGuide) PermissionGuideDialog(
            confirm = {
                showPermissionGuide = false
                permissions.launch(BLUETOOTH_PERMISSIONS)
            }, dismiss = { showPermissionGuide = false })

        actionConversation?.let { conversation ->
            val nearby = devices.firstOrNull {
                it.address.equals(conversation.transportAddress, true) ||
                    (it.discoveryId.isNotBlank() && conversation.peerId.startsWith(it.discoveryId, true))
            }
            ActionSheet(conversation.peerName,
                "${conversation.platform.name.lowercase()} · ${conversation.availability.name.lowercase()}",
                buildList {
                    add(ActionOption("打开聊天") { model.selectPeer(conversation.peerId); selected = 1 })
                    if (conversation.availability == DeviceAvailability.CONNECTED)
                        add(ActionOption("断开连接") { model.disconnectPeer(conversation.peerId) })
                    if (conversation.availability == DeviceAvailability.CONNECTABLE && nearby != null)
                        add(ActionOption("连接设备") { model.connect(nearby) })
                    add(ActionOption("查看设备信息") { infoConversation = conversation })
                    add(ActionOption("清空此会话记录", destructive = true) {
                        confirmAction = ConfirmAction("清空会话记录", "只删除本机中与“${conversation.peerName}”的聊天记录，已接收文件不会被删除。", "清空") {
                            model.clearConversation(conversation.peerId)
                        }
                    })
                    add(ActionOption("移除信任", destructive = true) {
                        confirmAction = ConfirmAction("移除信任", "下次连接“${conversation.peerName}”时需要重新核对安全码。", "移除") {
                            model.forgetPeer(conversation.peerId)
                        }
                    })
                }, dismiss = { actionConversation = null })
        }
        actionNearbyDevice?.let { device ->
            ActionSheet(device.name, "${device.platform.name.lowercase()} · ${device.address}",
                listOf(ActionOption("连接设备") { model.connect(device) }),
                dismiss = { actionNearbyDevice = null })
        }
        actionMessage?.let { message ->
            ActionSheet(if (message.text.isBlank()) "文件消息" else message.text.take(32),
                "${if (message.outgoing) "已发送" else "已接收"} · ${DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss").withZone(ZoneId.systemDefault()).format(message.timestamp)}",
                buildList {
                    if (message.text.isNotBlank()) add(ActionOption("复制消息") {
                        val clipboard = context.getSystemService(ClipboardManager::class.java)
                        clipboard.setPrimaryClip(ClipData.newPlainText("BlueLink 消息", message.text))
                    })
                    add(ActionOption("删除本机记录", destructive = true) {
                        confirmAction = ConfirmAction("删除本机记录", "只删除这条消息的本机记录，已接收文件不会被删除。", "删除") {
                            model.deleteMessage(message.id)
                        }
                    })
                }, dismiss = { actionMessage = null })
        }
        actionAttachment?.let { attachment ->
            val owningMessage = messages.firstOrNull { value ->
                value.attachments.any { it.attachmentId == attachment.attachmentId }
            }
            ActionSheet(attachment.fileName, "${formatBytes(attachment.sizeBytes)} · ${attachment.state.lowercase()}",
                buildList {
                    add(ActionOption(if (attachment.isImage) "预览图片" else "选择打开方式") {
                        if (attachment.isImage) previewAttachment = attachment
                        else runCatching { FileInteraction.open(context, attachment) }
                    })
                    add(ActionOption("分享") { runCatching { FileInteraction.share(context, attachment) } })
                    add(ActionOption("保存副本") {
                        pendingSaveAttachment = attachment
                        saveCopyPicker.launch(attachment.fileName)
                    })
                    if (owningMessage != null) add(ActionOption("删除本机记录", destructive = true) {
                        confirmAction = ConfirmAction("删除本机记录",
                            "只删除这条文件消息的本机记录，已接收文件不会被删除。", "删除") {
                            model.deleteMessage(owningMessage.id)
                        }
                    })
                }, dismiss = { actionAttachment = null })
        }
        actionTransfer?.let { transfer ->
            val attachment = transfer.asAttachment()
            ActionSheet(transfer.name,
                "${formatBytes(transfer.completedBytes)} / ${formatBytes(transfer.totalBytes)} · ${transferStatusText(transfer.status)}",
                buildList {
                    if (transfer.status == TransferStatus.COMPLETED && !transfer.localUri.isNullOrBlank()) {
                        add(ActionOption("打开") {
                            runCatching { FileInteraction.open(context, attachment) }
                                .onFailure { Toast.makeText(context, it.message ?: "无法打开文件", Toast.LENGTH_SHORT).show() }
                        })
                        add(ActionOption("分享") { runCatching { FileInteraction.share(context, attachment) } })
                        add(ActionOption("保存副本") {
                            pendingSaveAttachment = attachment
                            saveCopyPicker.launch(transfer.name)
                        })
                    }
                    if (transfer.status in setOf(TransferStatus.FAILED, TransferStatus.REJECTED) &&
                        transfer.outgoing && transfer.localUri != null)
                        add(ActionOption("重试") { model.retryTransfer(transfer) })
                    add(ActionOption("查看传输详情") { infoTransfer = transfer })
                    if (transfer.status !in activeTransferStatuses) add(ActionOption("删除本机记录", destructive = true) {
                        confirmAction = ConfirmAction("删除传输记录",
                            "只删除本机传输记录，不会删除已接收或原始文件。", "删除") {
                            model.deleteTransfer(transfer.id)
                        }
                    })
                }, dismiss = { actionTransfer = null })
        }
        infoConversation?.let { conversation ->
            AlertDialog(onDismissRequest = { infoConversation = null },
                title = { Text("设备信息") },
                text = { Text("设备：${conversation.peerName}\n平台：${conversation.platform.name.lowercase()}\n状态：${conversation.availability.name.lowercase()}\n设备标识：${conversation.peerId}\n蓝牙地址：${conversation.transportAddress}") },
                confirmButton = { TextButton(onClick = { infoConversation = null }) { Text("关闭") } })
        }
        infoTransfer?.let { transfer ->
            AlertDialog(onDismissRequest = { infoTransfer = null },
                title = { Text("传输详情") },
                text = { Text("文件：${transfer.name}\n方向：${if (transfer.outgoing) "发送" else "接收"}\n进度：${formatBytes(transfer.completedBytes)} / ${formatBytes(transfer.totalBytes)}\n状态：${transferStatusText(transfer.status)}\n设备：${transfer.peerId ?: "未知"}${transfer.failureDetail?.let { "\n失败原因：$it" }.orEmpty()}") },
                confirmButton = { TextButton(onClick = { infoTransfer = null }) { Text("关闭") } })
        }
        confirmAction?.let { prompt ->
            AlertDialog(onDismissRequest = { confirmAction = null },
                title = { Text(prompt.title) }, text = { Text(prompt.message) },
                confirmButton = { TextButton(onClick = { confirmAction = null; prompt.action() }) {
                    Text(prompt.confirmLabel, color = Danger)
                } },
                dismissButton = { TextButton(onClick = { confirmAction = null }) { Text("取消") } })
        }
    }
}

@Composable
private fun DevicesScreen(
    modifier: Modifier,
    devices: List<NearbyDevice>,
    conversations: List<ConversationSummary>,
    transfers: List<TransferItem>,
    state: ConnectionState,
    discoveryState: DiscoveryState,
    discover: () -> Unit,
    connect: (NearbyDevice) -> Unit,
    openConversation: (String) -> Unit,
    longPressConversation: (ConversationSummary) -> Unit,
    longPressNearby: (NearbyDevice) -> Unit,
) {
    val canonicalConversations = conversations.distinctBy { it.peerId.lowercase() }
    val connected = canonicalConversations.filter { it.availability == DeviceAvailability.CONNECTED }
    val offline = canonicalConversations.filter { it.availability != DeviceAvailability.CONNECTED }
    val knownAddresses = canonicalConversations.map { it.transportAddress.trim().uppercase() }
        .filter { it.isNotBlank() }.toSet()
    val knownPeerIds = canonicalConversations.map { it.peerId.lowercase() }
    val fresh = devices.distinctBy { device ->
        device.discoveryId.lowercase().ifBlank { device.address.trim().uppercase() }
    }.filterNot { device ->
        device.address.trim().uppercase() in knownAddresses ||
            (device.discoveryId.isNotBlank() && knownPeerIds.any { it.startsWith(device.discoveryId.lowercase()) })
    }
    LazyColumn(modifier.fillMaxSize().padding(horizontal = 18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        item {
            Card(colors = CardDefaults.cardColors(containerColor = Ink), shape = RoundedCornerShape(24.dp)) {
                Row(Modifier.fillMaxWidth().padding(20.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("${connected.size} 台设备在线", color = Color.White,
                            style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                        Text(if (state.phase == ConnectionPhase.CONNECTING || state.phase == ConnectionPhase.SECURE_HANDSHAKE)
                            state.detail ?: "正在恢复可信会话" else "后台会话持续接收消息和文件",
                            color = Color(0xFFAFB9CC), style = MaterialTheme.typography.bodySmall)
                    }
                    Button(onClick = discover, enabled = !discoveryState.scanning) {
                        Text(if (discoveryState.scanning) "扫描中" else "扫描")
                    }
                }
            }
        }
        if (connected.isNotEmpty()) {
            item { DeviceGroupTitle("已连接", "${connected.size} 台") }
            items(connected, key = { "connected-${it.peerId}" }) { summary ->
                val activeTransfer = transfers.firstOrNull { it.peerId == summary.peerId && it.status == TransferStatus.TRANSFERRING }
                ConversationDeviceCard(summary, activeTransfer, onClick = { openConversation(summary.peerId) },
                    onLongClick = { longPressConversation(summary) })
            }
        }
        if (offline.isNotEmpty()) {
            item { DeviceGroupTitle("离线", "仍可查看本地历史") }
            items(offline, key = { "offline-${it.peerId}" }) { summary ->
                ConversationDeviceCard(summary, null, onClick = { openConversation(summary.peerId) },
                    onLongClick = { longPressConversation(summary) })
            }
        }
        if (fresh.isNotEmpty()) {
            item { DeviceGroupTitle("附近新设备", "尚未建立信任的 BlueLink 设备") }
            items(fresh, key = { "nearby-${it.address}" }) { device ->
                NearbyDeviceCard(device, connect, onLongClick = { longPressNearby(device) })
            }
        }
        if (connected.isEmpty() && offline.isEmpty() && fresh.isEmpty())
            item { EmptyCard("尚未发现设备", discoveryState.detail) }
        item { Spacer(Modifier.height(12.dp)) }
    }
}

@Composable
private fun DeviceGroupTitle(title: String, detail: String) {
    Row(Modifier.fillMaxWidth().padding(top = 5.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(title, Modifier.weight(1f), color = Ink, fontWeight = FontWeight.Bold,
            style = MaterialTheme.typography.titleMedium)
        Text(detail, color = Muted, style = MaterialTheme.typography.labelMedium)
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ConversationDeviceCard(summary: ConversationSummary, transfer: TransferItem?, onClick: () -> Unit,
                                   onLongClick: () -> Unit) {
    val haptics = LocalHapticFeedback.current
    Card(colors = CardDefaults.cardColors(containerColor = Color.White), shape = RoundedCornerShape(18.dp),
        modifier = Modifier.combinedClickable(onClick = onClick, onLongClick = {
            haptics.performHapticFeedback(HapticFeedbackType.LongPress); onLongClick()
        })) {
        Column(Modifier.fillMaxWidth().padding(15.dp), verticalArrangement = Arrangement.spacedBy(7.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                DevicePlatformIcon(summary.platform == PeerPlatform.ANDROID, Modifier.size(46.dp))
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(summary.peerName, color = Ink, fontWeight = FontWeight.SemiBold,
                        maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                    Text(when (summary.availability) {
                        DeviceAvailability.CONNECTED -> "端到端加密 · 后台在线"
                        DeviceAvailability.OFFLINE -> "离线 · 点击查看历史"
                        DeviceAvailability.CONNECTABLE -> "附近可连接"
                    }, color = Muted, style = MaterialTheme.typography.bodySmall)
                }
                if (summary.unreadCount > 0) Surface(color = Blue, shape = CircleShape) {
                    Text(summary.unreadCount.toString(), color = Color.White,
                        modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                        style = MaterialTheme.typography.labelSmall)
                } else Box(Modifier.size(9.dp).background(when (summary.availability) {
                    DeviceAvailability.CONNECTED -> Color(0xFF13A663)
                    DeviceAvailability.OFFLINE -> Color(0xFFAAB3C2)
                    DeviceAvailability.CONNECTABLE -> Blue
                }, CircleShape))
            }
            if (transfer != null) {
                LinearProgressIndicator(progress = { transfer.progress.coerceIn(0f, 1f) },
                    modifier = Modifier.fillMaxWidth(), color = Blue)
                Text("正在${if (transfer.outgoing) "发送" else "接收"} ${transfer.name} · ${(transfer.progress * 100).toInt()}%",
                    color = Muted, style = MaterialTheme.typography.labelSmall,
                    maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun NearbyDeviceCard(device: NearbyDevice, connect: (NearbyDevice) -> Unit, onLongClick: () -> Unit) {
    val canConnect = device.platform == PeerPlatform.WINDOWS && device.rendezvousAvailable && device.connectable
    val haptics = LocalHapticFeedback.current
    Card(colors = CardDefaults.cardColors(containerColor = Color.White), shape = RoundedCornerShape(18.dp),
        modifier = Modifier.combinedClickable(enabled = true, onClick = { if (canConnect) connect(device) }, onLongClick = {
            haptics.performHapticFeedback(HapticFeedbackType.LongPress); onLongClick()
        })) {
        Row(Modifier.fillMaxWidth().padding(15.dp), verticalAlignment = Alignment.CenterVertically) {
            DevicePlatformIcon(device.platform == PeerPlatform.ANDROID, Modifier.size(46.dp))
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(device.name, color = Ink, fontWeight = FontWeight.SemiBold, maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                Text("${device.platform.name.lowercase()}${device.rssi?.let { " · $it dBm" }.orEmpty()}",
                    color = Muted, style = MaterialTheme.typography.bodySmall)
            }
            Text(if (canConnect) "连接" else "等待对端", color = if (canConnect) Blue else Muted,
                fontWeight = FontWeight.SemiBold)
        }
    }
}

@Composable
private fun NearbyScreen(modifier: Modifier, devices: List<NearbyDevice>, state: ConnectionState,
                         discoveryState: DiscoveryState,
                         discover: () -> Unit, connect: (NearbyDevice) -> Unit) {
    LazyColumn(modifier.fillMaxSize().padding(horizontal = 18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item {
            Card(colors = CardDefaults.cardColors(containerColor = Ink), shape = RoundedCornerShape(22.dp)) {
                Row(Modifier.fillMaxWidth().padding(20.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(state.peerName ?: "准备连接", color = Color.White, fontWeight = FontWeight.Bold)
                        val sessionActive = state.phase == ConnectionPhase.CONNECTING ||
                            state.phase == ConnectionPhase.SECURE_HANDSHAKE || state.phase == ConnectionPhase.TRUST_REQUIRED ||
                            state.phase == ConnectionPhase.CONNECTED
                        Text(if (sessionActive) state.detail ?: "正在连接" else discoveryState.detail,
                            color = Color(0xFFAFB9CC), style = MaterialTheme.typography.bodySmall)
                    }
                    Button(onClick = discover,
                        enabled = state.phase != ConnectionPhase.CONNECTING && !discoveryState.scanning) {
                        Text(if (discoveryState.scanning) "扫描中" else "扫描")
                    }
                }
            }
        }
        item { Text("附近设备", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold, color = Ink) }
        if (devices.isEmpty()) item { EmptyCard("尚未发现设备", "请确认对端已打开蓝联并允许附近设备权限。") }
        items(devices, key = { it.address }) { device ->
            val sessionBusy = state.phase == ConnectionPhase.CONNECTING ||
                state.phase == ConnectionPhase.SECURE_HANDSHAKE || state.phase == ConnectionPhase.TRUST_REQUIRED ||
                state.phase == ConnectionPhase.CONNECTED
            val canConnect = device.platform == PeerPlatform.WINDOWS &&
                device.rendezvousAvailable && device.connectable && !sessionBusy
            val action = when {
                state.phase == ConnectionPhase.CONNECTED -> "已连接"
                sessionBusy -> "连接中"
                canConnect -> "连接"
                device.platform == PeerPlatform.WINDOWS && device.rendezvousAvailable -> "正在确认"
                else -> "等待对端"
            }
            Card(onClick = { connect(device) }, enabled = canConnect,
                colors = CardDefaults.cardColors(containerColor = Color.White), shape = RoundedCornerShape(18.dp)) {
                Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(44.dp).background(Color(0xFFEAF1FF), RoundedCornerShape(14.dp)), contentAlignment = Alignment.Center) { Text("◈", color = Blue) }
                    Spacer(Modifier.width(12.dp)); Column(Modifier.weight(1f)) {
                        Text(device.name, fontWeight = FontWeight.SemiBold, color = Ink)
                        val platform = when (device.platform) {
                            PeerPlatform.WINDOWS -> "Windows"
                            PeerPlatform.ANDROID -> "Android"
                            PeerPlatform.UNKNOWN -> "BlueLink"
                        }
                        val signal = device.rssi?.let { " · ${it} dBm" }.orEmpty()
                        val availability = when {
                            canConnect && device.bonded -> "系统已配对"
                            canConnect -> "首次连接将配对"
                            device.platform == PeerPlatform.WINDOWS -> "等待可连接 Rendezvous"
                            else -> "等待对端"
                        }
                        Text("$platform · $availability$signal",
                            color = Muted, style = MaterialTheme.typography.bodySmall)
                    }
                    Text(action, color = if (canConnect) Blue else Muted,
                        fontWeight = FontWeight.SemiBold)
                }
            }
        }
        item { Spacer(Modifier.height(8.dp)) }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ChatScreen(modifier: Modifier, messages: List<ChatItem>, state: ConnectionState,
                       conversations: List<ConversationSummary>, selectConversation: (String) -> Unit,
                       showImageThumbnails: Boolean,
                       send: (String) -> Unit, pickFile: () -> Unit,
                       longPressConversation: (ConversationSummary) -> Unit,
                       longPressMessage: (ChatItem) -> Unit,
                       longPressAttachment: (ChatAttachment) -> Unit,
                       openAttachment: (ChatAttachment) -> Unit) {
    var draft by remember { mutableStateOf("") }
    var showNewMessages by remember { mutableStateOf(false) }
    val messageListState = rememberLazyListState()
    val messageListScope = rememberCoroutineScope()
    val haptics = LocalHapticFeedback.current
    LaunchedEffect(messages.size, state.peerName) {
        if (messages.isEmpty()) {
            showNewMessages = false
            return@LaunchedEffect
        }
        val lastVisible = messageListState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: -1
        val wasPinned = lastVisible < 0 || lastVisible >= messages.lastIndex - 1
        if (messages.last().outgoing || wasPinned) {
            messageListState.animateScrollToItem(messages.lastIndex)
            showNewMessages = false
        } else {
            showNewMessages = true
        }
    }
    Column(modifier.fillMaxSize().padding(horizontal = 18.dp)) {
        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(bottom = 10.dp)) {
            items(conversations, key = { it.peerId }) { conversation ->
                val selected = state.peerName == conversation.peerName
                Surface(color = if (selected) Lavender else Color.White,
                    shape = RoundedCornerShape(18.dp),
                    modifier = Modifier
                        .border(1.dp, if (selected) Color(0xFFA9BEF4) else Border, RoundedCornerShape(18.dp))
                        .combinedClickable(onClick = { selectConversation(conversation.peerId) }, onLongClick = {
                            haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                            longPressConversation(conversation)
                        })) {
                    Row(Modifier.padding(horizontal = 13.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(8.dp).background(when (conversation.availability) {
                            DeviceAvailability.CONNECTED -> Success
                            DeviceAvailability.OFFLINE -> Color(0xFFAAB3C2)
                            DeviceAvailability.CONNECTABLE -> Blue
                        }, CircleShape))
                        Spacer(Modifier.width(8.dp))
                        Text(conversation.peerName, color = Ink, fontWeight = FontWeight.SemiBold,
                            maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                            modifier = Modifier.widthIn(max = 150.dp))
                        if (conversation.unreadCount > 0) {
                            Spacer(Modifier.width(8.dp))
                            Surface(color = Blue, shape = CircleShape) {
                                Text(conversation.unreadCount.coerceAtMost(99).toString(), color = Color.White,
                                    modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp),
                                    style = MaterialTheme.typography.labelSmall)
                            }
                        }
                    }
                }
            }
        }
        if (state.phase != ConnectionPhase.CONNECTED && state.peerName != null)
            Surface(color = SoftWarning, shape = RoundedCornerShape(13.dp), modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp)) {
                Text("设备离线 · 当前仅可查看本地历史记录", color = Color(0xFF8A5700),
                    modifier = Modifier.padding(horizontal = 12.dp, vertical = 9.dp),
                    style = MaterialTheme.typography.bodySmall)
            }
        Box(Modifier.weight(1f)) {
            LazyColumn(Modifier.fillMaxSize(), state = messageListState,
                verticalArrangement = Arrangement.spacedBy(3.dp)) {
                if (messages.isEmpty()) item { EmptyCard("端到端加密聊天", "连接设备后，文字与文件都只通过蓝牙发送。") }
                itemsIndexed(messages, key = { _, item -> item.id }) { index, message ->
                    val previous = messages.getOrNull(index - 1)
                    val showTime = previous == null ||
                        previous.timestamp.atZone(ZoneId.systemDefault()).toLocalDate() !=
                            message.timestamp.atZone(ZoneId.systemDefault()).toLocalDate() ||
                        Duration.between(previous.timestamp, message.timestamp).abs() >= Duration.ofMinutes(5)
                    if (showTime) {
                        Box(Modifier.fillMaxWidth().padding(vertical = 10.dp), contentAlignment = Alignment.Center) {
                            Surface(color = Color(0xFFEDF1F7), shape = RoundedCornerShape(50)) {
                                Text(formatConversationTime(message.timestamp), color = Muted, style = MaterialTheme.typography.labelSmall,
                                    modifier = Modifier.padding(horizontal = 11.dp, vertical = 5.dp))
                            }
                        }
                    }
                    MessageBubble(message, showImageThumbnails, openAttachment, longPressMessage, longPressAttachment)
                }
            }
            if (showNewMessages) Surface(color = Blue, shape = RoundedCornerShape(50), shadowElevation = 4.dp,
                modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 8.dp).clickable {
                    if (messages.isNotEmpty()) {
                        showNewMessages = false
                        messageListScope.launch { messageListState.animateScrollToItem(messages.lastIndex) }
                    }
                }) {
                Text("有新消息 · 点此回到底部", color = Color.White,
                    modifier = Modifier.padding(horizontal = 14.dp, vertical = 8.dp),
                    style = MaterialTheme.typography.labelMedium)
            }
        }
        Surface(color = Color.White, shape = RoundedCornerShape(20.dp), shadowElevation = 3.dp,
            modifier = Modifier.fillMaxWidth().padding(vertical = 10.dp)) {
            Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = pickFile, enabled = state.phase == ConnectionPhase.CONNECTED,
                    modifier = Modifier.size(48.dp).background(SoftBlue, RoundedCornerShape(15.dp))) {
                    Icon(Icons.Rounded.AttachFile, "选择文件", tint = if (state.phase == ConnectionPhase.CONNECTED) Blue else Muted)
                }
                Spacer(Modifier.width(7.dp))
                OutlinedTextField(draft, { draft = it },
                    placeholder = { Text(if (state.phase == ConnectionPhase.CONNECTED) "输入消息" else "设备离线，暂不可发送") },
                    enabled = state.phase == ConnectionPhase.CONNECTED,
                    modifier = Modifier.weight(1f), singleLine = true, shape = RoundedCornerShape(15.dp))
                Spacer(Modifier.width(7.dp))
                Button(onClick = { send(draft); draft = "" },
                    enabled = state.phase == ConnectionPhase.CONNECTED && draft.isNotBlank(),
                    modifier = Modifier.size(48.dp), shape = RoundedCornerShape(15.dp),
                    contentPadding = androidx.compose.foundation.layout.PaddingValues(12.dp)) {
                    Icon(Icons.AutoMirrored.Rounded.Send, "发送", Modifier.size(20.dp))
                }
            }
        }
    }
}

private fun formatConversationTime(timestamp: Instant, now: Instant = Instant.now()): String {
    val zone = ZoneId.systemDefault()
    val value = timestamp.atZone(zone)
    val current = now.atZone(zone)
    val date = value.toLocalDate()
    val today = current.toLocalDate()
    val time = DateTimeFormatter.ofPattern("HH:mm").format(value)
    if (date == today) return time
    if (date == today.minusDays(1)) return "昨天 $time"
    val startOfWeek = today.minusDays((today.dayOfWeek.value - 1).toLong())
    if (!date.isBefore(startOfWeek)) {
        val weekday = listOf("周一", "周二", "周三", "周四", "周五", "周六", "周日")[date.dayOfWeek.value - 1]
        return "$weekday $time"
    }
    return if (date.year == today.year)
        DateTimeFormatter.ofPattern("M月d日 HH:mm").format(value)
    else DateTimeFormatter.ofPattern("yyyy年M月d日 HH:mm").format(value)
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun MessageBubble(message: ChatItem, showImageThumbnails: Boolean,
                          openAttachment: (ChatAttachment) -> Unit,
                          longPressMessage: (ChatItem) -> Unit,
                          longPressAttachment: (ChatAttachment) -> Unit) {
    val haptics = LocalHapticFeedback.current
    Row(Modifier.fillMaxWidth().padding(vertical = 3.dp), horizontalArrangement = if (message.outgoing) Arrangement.End else Arrangement.Start) {
        Surface(color = if (message.outgoing) Blue else Color.White,
            shape = if (message.outgoing) RoundedCornerShape(18.dp, 18.dp, 5.dp, 18.dp)
                else RoundedCornerShape(18.dp, 18.dp, 18.dp, 5.dp),
            shadowElevation = if (message.outgoing) 0.dp else 1.dp,
            modifier = Modifier.widthIn(min = 92.dp, max = 340.dp).combinedClickable(onClick = {}, onLongClick = {
                haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                longPressMessage(message)
            })) {
            Column(Modifier.padding(13.dp)) {
                if (message.text.isNotBlank()) Text(message.text, color = if (message.outgoing) Color.White else Ink)
                message.attachments.forEach { attachment ->
                    Spacer(Modifier.height(7.dp))
                    AttachmentCard(attachment, showImageThumbnails, openAttachment, longPressAttachment)
                }
                Text(DateTimeFormatter.ofPattern("HH:mm").withZone(ZoneId.systemDefault()).format(message.timestamp) + " · " +
                    when (message.status) {
                        com.bluelink.android.domain.MessageStatus.LOCAL_QUEUED -> "等待重连"
                        com.bluelink.android.domain.MessageStatus.SENDING -> "发送中"
                        com.bluelink.android.domain.MessageStatus.SENT -> "已发送"
                        com.bluelink.android.domain.MessageStatus.DELIVERED -> "已送达"
                        com.bluelink.android.domain.MessageStatus.READ -> "已读"
                        com.bluelink.android.domain.MessageStatus.RECEIVED -> "已接收"
                        com.bluelink.android.domain.MessageStatus.FAILED -> "发送失败"
                    }, color = if (message.outgoing) Color(0xFFD9E6FF) else Muted,
                    style = MaterialTheme.typography.labelSmall, modifier = Modifier.align(Alignment.End))
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun AttachmentCard(attachment: ChatAttachment, showImageThumbnail: Boolean,
                           openAttachment: (ChatAttachment) -> Unit,
                           onLongClick: (ChatAttachment) -> Unit) {
    val context = LocalContext.current
    val haptics = LocalHapticFeedback.current
    val bitmap by produceState<android.graphics.Bitmap?>(initialValue = null,
        attachment.localUri, attachment.state, showImageThumbnail) {
        value = if (attachment.isImage && showImageThumbnail)
            FileInteraction.loadBitmap(context, attachment, 720) else null
    }
    Surface(color = Color.White.copy(alpha = .96f), shape = RoundedCornerShape(13.dp),
        modifier = Modifier.fillMaxWidth().combinedClickable(onClick = { openAttachment(attachment) }, onLongClick = {
            haptics.performHapticFeedback(HapticFeedbackType.LongPress)
            onLongClick(attachment)
        })) {
        Column {
            if (attachment.isImage && showImageThumbnail && bitmap != null) {
                val imageAspect = (bitmap!!.width.toFloat() / bitmap!!.height.coerceAtLeast(1))
                    .coerceIn(.72f, 2.4f)
                androidx.compose.foundation.Image(bitmap!!.asImageBitmap(), attachment.fileName,
                    Modifier.fillMaxWidth().heightIn(max = 220.dp).aspectRatio(imageAspect)
                        .align(Alignment.CenterHorizontally), contentScale = ContentScale.Fit)
                Column(Modifier.padding(horizontal = 11.dp, vertical = 8.dp)) {
                    Text(attachment.fileName, color = Ink, fontWeight = FontWeight.SemiBold,
                        maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                    Text(formatBytes(attachment.sizeBytes), color = Muted, style = MaterialTheme.typography.labelSmall)
                }
            } else {
                Row(Modifier.padding(11.dp), verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(44.dp).background(SoftBlue, RoundedCornerShape(12.dp)),
                        contentAlignment = Alignment.Center) {
                        Icon(Icons.Rounded.Description, "文件", tint = Blue, modifier = Modifier.size(23.dp))
                    }
                    Spacer(Modifier.width(10.dp))
                    Column(Modifier.weight(1f)) {
                        Text(attachment.fileName, color = Ink, fontWeight = FontWeight.SemiBold,
                            maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                        Text("${formatBytes(attachment.completedBytes)} / ${formatBytes(attachment.sizeBytes)} · ${attachment.state.lowercase()}",
                            color = Muted, style = MaterialTheme.typography.labelSmall)
                    }
                }
            }
            if (attachment.isTransferActive)
                LinearProgressIndicator(progress = { attachment.progress }, modifier = Modifier.fillMaxWidth(), color = Blue)
        }
    }
}

@Composable
private fun ImagePreviewDialog(attachment: ChatAttachment, dismiss: () -> Unit) {
    val context = LocalContext.current
    val saveCopy = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument(attachment.mimeType)) { uri ->
        if (uri != null) runCatching { FileInteraction.copyTo(context, attachment, uri) }
            .onSuccess { Toast.makeText(context, "副本已保存", Toast.LENGTH_SHORT).show() }
            .onFailure { Toast.makeText(context, it.message ?: "保存失败", Toast.LENGTH_SHORT).show() }
    }
    val bitmap by produceState<android.graphics.Bitmap?>(initialValue = null, attachment.localUri) {
        value = FileInteraction.loadBitmap(context, attachment, 2048)
    }
    var scale by remember(attachment.attachmentId) { mutableStateOf(1f) }
    var offsetX by remember(attachment.attachmentId) { mutableStateOf(0f) }
    var offsetY by remember(attachment.attachmentId) { mutableStateOf(0f) }
    AlertDialog(onDismissRequest = dismiss,
        title = { Text(attachment.fileName, maxLines = 1,
            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Box(Modifier.fillMaxWidth().height(420.dp), contentAlignment = Alignment.Center) {
                    if (bitmap == null) Text("正在加载图片…", color = Muted)
                    else androidx.compose.foundation.Image(bitmap!!.asImageBitmap(), attachment.fileName,
                        Modifier.fillMaxSize()
                            .clip(RoundedCornerShape(14.dp))
                            .graphicsLayer {
                                scaleX = scale
                                scaleY = scale
                                translationX = offsetX
                                translationY = offsetY
                            }
                            .pointerInput(attachment.attachmentId) {
                                detectTransformGestures { _, pan, zoom, _ ->
                                    val next = (scale * zoom).coerceIn(1f, 5f)
                                    scale = next
                                    if (next == 1f) {
                                        offsetX = 0f
                                        offsetY = 0f
                                    } else {
                                        offsetX += pan.x
                                        offsetY += pan.y
                                    }
                                }
                            }, contentScale = ContentScale.Fit)
                }
                Text("双指缩放 · 拖动查看细节 · ${formatBytes(attachment.sizeBytes)}",
                    color = Muted, style = MaterialTheme.typography.labelSmall,
                    modifier = Modifier.align(Alignment.CenterHorizontally))
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    TextButton(onClick = { runCatching { FileInteraction.share(context, attachment) } }) { Text("分享") }
                    TextButton(onClick = { saveCopy.launch(attachment.fileName) }) { Text("保存副本") }
                    TextButton(onClick = { runCatching { FileInteraction.open(context, attachment) } }) { Text("打开方式") }
                }
            }
        },
        confirmButton = { TextButton(onClick = dismiss) { Text("关闭") } })
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun TransferScreen(modifier: Modifier, transfers: List<TransferItem>, retry: (TransferItem) -> Unit,
                           pause: (TransferItem) -> Unit, resume: (TransferItem) -> Unit,
                           cancel: (TransferItem) -> Unit,
                           longPressTransfer: (TransferItem) -> Unit,
                           openFilesSettings: () -> Unit) {
    var filter by remember { mutableIntStateOf(0) }
    val visible = transfers.filter { value -> when (filter) {
        1 -> value.status in setOf(TransferStatus.OFFERED, TransferStatus.QUEUED, TransferStatus.TRANSFERRING,
            TransferStatus.PAUSED, TransferStatus.RESUMING, TransferStatus.VERIFYING, TransferStatus.COMMITTING)
        2 -> value.status == TransferStatus.COMPLETED
        3 -> value.status in setOf(TransferStatus.REJECTED, TransferStatus.FAILED, TransferStatus.CANCELED)
        else -> true
    } }
    LazyColumn(modifier.fillMaxSize().padding(horizontal = 18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item { Text("文件传输", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold, color = Ink) }
        item {
            LazyRow(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                items(listOf("全部", "进行中", "已完成", "失败")) { label ->
                    val index = listOf("全部", "进行中", "已完成", "失败").indexOf(label)
                    FilterChip(selected = filter == index, onClick = { filter = index }, label = { Text(label) })
                }
            }
        }
        if (visible.isEmpty()) item { EmptyCard("暂无传输", "从聊天页选择文件。校验和原子提交成功后才会标记为完成。") }
        items(visible, key = { it.id }) { transfer ->
            val haptics = LocalHapticFeedback.current
            Card(colors = CardDefaults.cardColors(containerColor = Color.White), shape = RoundedCornerShape(18.dp),
                modifier = Modifier.combinedClickable(onClick = {}, onLongClick = {
                    haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                    longPressTransfer(transfer)
                })) {
                Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Row { Text(transfer.name, Modifier.weight(1f), fontWeight = FontWeight.SemiBold); Text(if (transfer.outgoing) "发送" else "接收", color = Blue) }
                    LinearProgressIndicator(progress = { transfer.progress.coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth(), color = Blue)
                    val speed = if (transfer.bytesPerSecond > 0) " · ${formatBytes(transfer.bytesPerSecond.toLong())}/s" else ""
                    val remaining = transfer.remainingSeconds?.let { seconds -> " · 约 ${seconds / 60}:${(seconds % 60).toString().padStart(2, '0')}" }.orEmpty()
                    Text("${formatBytes(transfer.completedBytes)} / ${formatBytes(transfer.totalBytes)} · ${transferStatusText(transfer.status)}$speed$remaining", color = Muted, style = MaterialTheme.typography.bodySmall)
                    transfer.failureDetail?.let { Text(it, color = Danger, style = MaterialTheme.typography.bodySmall) }
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        if (transfer.status in setOf(TransferStatus.OFFERED, TransferStatus.QUEUED,
                                TransferStatus.TRANSFERRING, TransferStatus.RESUMING))
                            OutlinedButton(onClick = { pause(transfer) }) { Text("暂停") }
                        if (transfer.status == TransferStatus.PAUSED)
                            Button(onClick = { resume(transfer) }) { Text("继续") }
                        if (transfer.status in setOf(TransferStatus.OFFERED, TransferStatus.QUEUED,
                                TransferStatus.TRANSFERRING, TransferStatus.PAUSED, TransferStatus.RESUMING,
                                TransferStatus.VERIFYING, TransferStatus.COMMITTING, TransferStatus.REJECTED))
                            TextButton(onClick = { cancel(transfer) }) {
                                Text(if (transfer.status == TransferStatus.REJECTED) "拒绝" else "取消", color = Danger) }
                    }
                    if (transfer.status in setOf(TransferStatus.FAILED, TransferStatus.REJECTED)) Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        if (transfer.outgoing && transfer.localUri != null)
                            OutlinedButton(onClick = { retry(transfer) }) { Text("重试") }
                        if (!transfer.outgoing && transfer.failureDetail?.contains("接收限制") == true)
                            TextButton(onClick = openFilesSettings) { Text("调整接收限制") }
                    }
                }
            }
        }
    }
}

private val activeTransferStatuses = setOf(TransferStatus.OFFERED, TransferStatus.QUEUED,
    TransferStatus.TRANSFERRING, TransferStatus.PAUSED, TransferStatus.RESUMING,
    TransferStatus.VERIFYING, TransferStatus.COMMITTING)

private fun TransferItem.asAttachment() = ChatAttachment(
    attachmentId = attachmentId ?: id,
    transferId = id,
    fileName = name,
    mimeType = mimeType,
    sizeBytes = totalBytes,
    localUri = localUri,
    state = status.name,
    completedBytes = completedBytes,
    bytesPerSecond = bytesPerSecond,
)

private fun transferStatusText(value: TransferStatus): String = when (value) {
    TransferStatus.OFFERED -> "等待接受"
    TransferStatus.QUEUED -> "排队等待"
    TransferStatus.TRANSFERRING -> "传输中"
    TransferStatus.PAUSED -> "已暂停"
    TransferStatus.RESUMING -> "正在恢复"
    TransferStatus.VERIFYING -> "正在校验"
    TransferStatus.COMMITTING -> "正在保存"
    TransferStatus.COMPLETED -> "已完成"
    TransferStatus.REJECTED -> "未接收"
    TransferStatus.FAILED -> "失败"
    TransferStatus.CANCELED -> "已取消"
}

@Composable
private fun SettingsScreen(
    modifier: Modifier,
    settings: AppSettings,
    conversations: List<ConversationSummary>,
    selectedPage: Int,
    selectPage: (Int) -> Unit,
    save: (AppSettings) -> Unit,
    chooseDirectory: () -> Unit,
    forgetPeer: (String) -> Unit,
    clearChat: () -> Unit,
    clearTransfers: () -> Unit,
    fingerprint: String,
    diagnostics: List<DiagnosticEntry>,
    checkLocalUpdate: () -> Unit,
) {
    var clearTarget by remember { mutableStateOf<String?>(null) }
    val context = LocalContext.current
    Column(modifier.fillMaxSize().padding(horizontal = 18.dp)) {
        Text("设置", color = Ink, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
        LazyRow(horizontalArrangement = Arrangement.spacedBy(7.dp), modifier = Modifier.padding(vertical = 10.dp)) {
            items(listOf("连接", "文件与存储", "隐私与数据", "关于")) { label ->
                val index = listOf("连接", "文件与存储", "隐私与数据", "关于").indexOf(label)
                FilterChip(selected = selectedPage == index, onClick = { selectPage(index) }, label = { Text(label) })
            }
        }
        LazyColumn(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            when (selectedPage) {
                0 -> {
                    item { SettingsSection("自动连接", "可信设备双方启动并可见后恢复加密会话") {
                        SettingSwitch("自动连接已信任设备", settings.autoConnectTrustedDevices) {
                            save(settings.copy(autoConnectTrustedDevices = it)) }
                        SettingSwitch("启动时扫描附近设备", settings.scanOnStartup) {
                            save(settings.copy(scanOnStartup = it)) }
                        SettingSwitch("保持后台会话", settings.keepBackgroundSessions) {
                            save(settings.copy(keepBackgroundSessions = it)) }
                        Text("最大同时连接数：${settings.maxConcurrentConnections}", color = Ink)
                        Slider(settings.maxConcurrentConnections.toFloat(),
                            onValueChange = { save(settings.copy(maxConcurrentConnections = it.toInt().coerceIn(1, 8))) },
                            valueRange = 1f..8f, steps = 6)
                    } }
                    item { SettingsSection("信任管理", "移除后再次连接需要重新核对安全码") {
                        if (conversations.isEmpty()) Text("暂无已信任设备", color = Muted)
                        conversations.forEach { conversation ->
                            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                                Column(Modifier.weight(1f)) {
                                    Text(conversation.peerName, color = Ink, fontWeight = FontWeight.SemiBold)
                                    Text(conversation.peerId.take(16) + "…", color = Muted,
                                        style = MaterialTheme.typography.labelSmall)
                                }
                                TextButton(onClick = { forgetPeer(conversation.peerId) }) { Text("移除") }
                            }
                        }
                    } }
                }
                1 -> {
                    item { SettingsSection("接收策略", "接收文件会自动下载并在整文件校验后公开") {
                        SettingSwitch("自动下载接收文件", settings.autoDownloadFiles) {
                            save(settings.copy(autoDownloadFiles = it)) }
                        SettingSwitch("限制单个接收文件大小", settings.receiveSizeLimitEnabled) {
                            save(settings.copy(receiveSizeLimitEnabled = it)) }
                        Text("上限：${formatBytes(settings.receiveSizeLimitBytes)}", color = Ink)
                        Slider((settings.receiveSizeLimitBytes / (1024f * 1024f)).coerceIn(10f, 2048f),
                            onValueChange = { save(settings.copy(receiveSizeLimitBytes = it.toLong() * 1024 * 1024)) },
                            valueRange = 10f..2048f)
                        SettingSwitch("在聊天中显示图片缩略图", settings.showImageThumbnails) {
                            save(settings.copy(showImageThumbnails = it)) }
                    } }
                    item { SettingsSection("保存位置", "默认保存到公共 Download/BlueLink；自定义位置通过系统 SAF 授权") {
                        Text(if (settings.downloadDirectory.startsWith("content://")) "已选择自定义目录"
                            else "Download/BlueLink（默认）", color = Ink, fontWeight = FontWeight.SemiBold)
                        OutlinedButton(onClick = chooseDirectory, modifier = Modifier.fillMaxWidth()) { Text("选择保存目录") }
                        Text("可用空间：${availableDownloadSpace()}", color = Muted,
                            style = MaterialTheme.typography.bodySmall)
                        OutlinedButton(onClick = {
                            runCatching { context.startActivity(Intent(DownloadManager.ACTION_VIEW_DOWNLOADS)) }
                        }, modifier = Modifier.fillMaxWidth()) { Text("打开下载目录") }
                        TextButton(onClick = {
                            context.cacheDir.resolve("previews").deleteRecursively()
                            Toast.makeText(context, "缩略图缓存已清理", Toast.LENGTH_SHORT).show()
                        }, modifier = Modifier.fillMaxWidth()) { Text("清理缩略图缓存") }
                    } }
                }
                2 -> {
                    item { SettingsSection("本地记录", "蓝联不使用服务端，以下数据只保存在本机") {
                        SettingSwitch("保存聊天记录", settings.saveChatHistory) {
                            save(settings.copy(saveChatHistory = it)) }
                        SettingSwitch("保存文件传输记录", settings.saveTransferHistory) {
                            save(settings.copy(saveTransferHistory = it)) }
                        SettingSwitch("启用脱敏诊断日志", settings.diagnosticsEnabled) {
                            save(settings.copy(diagnosticsEnabled = it)) }
                        Text("记录保留期限", color = Ink, fontWeight = FontWeight.SemiBold)
                        LazyRow(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                            items(listOf("永久" to "forever", "30 天" to "30d", "90 天" to "90d", "1 年" to "1y")) { choice ->
                                FilterChip(selected = settings.retentionPeriod == choice.second,
                                    onClick = { save(settings.copy(retentionPeriod = choice.second)) },
                                    label = { Text(choice.first) })
                            }
                        }
                    } }
                    item { SettingsSection("数据管理", "清除记录不会删除已经保存到 Download/BlueLink 的文件") {
                        OutlinedButton(onClick = { shareDiagnostics(context, diagnostics) }, modifier = Modifier.fillMaxWidth()) {
                            Text("导出脱敏诊断日志") }
                        OutlinedButton(onClick = { clearTarget = "chat" }, modifier = Modifier.fillMaxWidth()) {
                            Text("清除聊天记录") }
                        OutlinedButton(onClick = { clearTarget = "transfers" }, modifier = Modifier.fillMaxWidth()) {
                            Text("清除传输记录") }
                        OutlinedButton(onClick = { clearTarget = "all" }, modifier = Modifier.fillMaxWidth()) {
                            Text("清除全部本地记录", color = Color(0xFFD92D20)) }
                        Text("本机身份指纹\n$fingerprint", color = Muted,
                            style = MaterialTheme.typography.bodySmall)
                    } }
                }
                else -> item { SettingsSection("关于蓝联", "纯蓝牙 · 无服务器 · 端到端加密") {
                    BlueLinkLogo(Modifier.size(86.dp).align(Alignment.CenterHorizontally))
                    Text("蓝联 BlueLink ${BuildConfig.VERSION_NAME}", color = Ink,
                        style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
                    Text("BTX/1.1 · X25519/Ed25519 · AES-GCM\n无 INTERNET 权限，不使用 Wi‑Fi、局域网或云端回退。",
                        color = Muted, style = MaterialTheme.typography.bodySmall)
                    HorizontalDivider()
                    Text("检查更新仅支持用户选择本地签名安装包。\n开源许可与第三方组件信息随安装包提供。",
                        color = Muted, style = MaterialTheme.typography.bodySmall)
                    OutlinedButton(onClick = checkLocalUpdate, modifier = Modifier.fillMaxWidth()) {
                        Text("选择本地安装包") }
                } }
            }
            item { Spacer(Modifier.height(16.dp)) }
        }
    }
    clearTarget?.let { target -> AlertDialog(onDismissRequest = { clearTarget = null },
        title = { Text("清除本地记录？") },
        text = { Text(when (target) {
            "chat" -> "聊天记录将从本机数据库删除，此操作不可撤销；已接收文件不会被删除。"
            "transfers" -> "传输记录将从本机数据库删除，此操作不可撤销；已接收文件不会被删除。"
            else -> "聊天和传输记录将从本机数据库删除，此操作不可撤销；已接收文件不会被删除。"
        }) },
        confirmButton = { TextButton(onClick = {
            if (target == "chat" || target == "all") clearChat()
            if (target == "transfers" || target == "all") clearTransfers()
            clearTarget = null
        }) { Text("确认清除", color = Color(0xFFD92D20)) } },
        dismissButton = { TextButton(onClick = { clearTarget = null }) { Text("取消") } }) }
}

private fun shareDiagnostics(context: android.content.Context, entries: List<DiagnosticEntry>) {
    runCatching {
        val directory = File(context.cacheDir, "shared").apply { mkdirs() }
        val target = File(directory, "BlueLink-diagnostics-${System.currentTimeMillis()}.log")
        val formatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss.SSS").withZone(ZoneId.systemDefault())
        target.writeText(entries.joinToString("\n") { entry ->
            "${formatter.format(entry.timestamp)} [${entry.level}] ${entry.component}: ${entry.message}"
        })
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", target)
        context.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_STREAM, uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }, "导出脱敏诊断日志"))
    }.onFailure { Toast.makeText(context, it.message ?: "导出失败", Toast.LENGTH_SHORT).show() }
}

@Composable
private fun SettingsSection(title: String, detail: String, content: @Composable ColumnScope.() -> Unit) {
    Card(colors = CardDefaults.cardColors(containerColor = Color.White), shape = RoundedCornerShape(18.dp)) {
        Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(title, color = Ink, fontWeight = FontWeight.Bold, style = MaterialTheme.typography.titleMedium)
            Text(detail, color = Muted, style = MaterialTheme.typography.bodySmall)
            HorizontalDivider(color = Color(0xFFDCE3EF))
            content()
        }
    }
}

@Composable
private fun SettingSwitch(title: String, checked: Boolean, changed: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(title, Modifier.weight(1f), color = Ink)
        Switch(checked = checked, onCheckedChange = changed)
    }
}

@Composable
private fun PermissionGuideDialog(confirm: () -> Unit, dismiss: () -> Unit) {
    AlertDialog(onDismissRequest = dismiss,
        title = { Text("让附近设备找到蓝联") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("蓝联需要以下系统权限来发现并连接附近设备：")
                Text("• 附近设备：扫描和建立蓝牙连接\n• 蓝牙广播：让 Windows 发现本机\n• 通知：后台保持会话和文件传输",
                    color = Muted)
                Text("应用没有 INTERNET 权限，消息和文件不会上传到服务器。",
                    color = Blue, fontWeight = FontWeight.SemiBold)
            }
        }, confirmButton = { Button(onClick = confirm) { Text("继续授权") } },
        dismissButton = { TextButton(onClick = dismiss) { Text("暂不") } })
}

private fun availableDownloadSpace(): String = runCatching {
    val path = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
    formatBytes(android.os.StatFs(path.absolutePath).availableBytes)
}.getOrDefault("不可用")

@Composable
private fun DiagnosticsScreen(
    modifier: Modifier,
    entries: List<DiagnosticEntry>,
    connection: ConnectionState,
    discovery: DiscoveryState,
    clear: () -> Unit,
) {
    var levelFilter by remember { mutableIntStateOf(0) }
    val visibleEntries = entries.filter { entry -> when (levelFilter) {
        1 -> entry.level == DiagnosticLevel.INFO
        2 -> entry.level == DiagnosticLevel.WARNING
        3 -> entry.level == DiagnosticLevel.ERROR
        else -> true
    } }
    val context = LocalContext.current
    val formatter = remember { DateTimeFormatter.ofPattern("HH:mm:ss.SSS").withZone(ZoneId.systemDefault()) }
    val output = remember(entries, connection, discovery) {
        buildString {
            appendLine("BlueLink Android diagnostics")
            appendLine("device=${Build.MANUFACTURER} ${Build.MODEL}; sdk=${Build.VERSION.SDK_INT}")
            appendLine("connection=${connection.phase}; peer=${connection.peerName.orEmpty()}; detail=${connection.detail.orEmpty()}")
            appendLine("discovery=${discovery.scanning}; detail=${discovery.detail}")
            appendLine()
            entries.forEach { entry ->
                append(formatter.format(entry.timestamp))
                append(" [${entry.level}] [${entry.component}] ")
                appendLine(entry.message)
            }
        }
    }
    Column(modifier.fillMaxSize().padding(horizontal = 14.dp)) {
        Row(Modifier.fillMaxWidth().padding(bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("连接诊断", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold, color = Ink)
                Text("仅保存在本机内存，不记录消息、文件内容、密钥或安全码。", color = Muted,
                    style = MaterialTheme.typography.bodySmall)
            }
            OutlinedButton(onClick = clear) { Text("清空") }
            Spacer(Modifier.width(8.dp))
            Button(onClick = {
                context.getSystemService(ClipboardManager::class.java)
                    .setPrimaryClip(ClipData.newPlainText("BlueLink diagnostics", output))
                Toast.makeText(context, "诊断日志已复制", Toast.LENGTH_SHORT).show()
            }) { Text("复制") }
        }
        LazyRow(horizontalArrangement = Arrangement.spacedBy(7.dp), modifier = Modifier.padding(bottom = 8.dp)) {
            items(listOf("全部", "信息", "警告", "错误")) { label ->
                val index = listOf("全部", "信息", "警告", "错误").indexOf(label)
                FilterChip(selected = levelFilter == index, onClick = { levelFilter = index }, label = { Text(label) })
            }
        }
        LazyColumn(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(7.dp)) {
            if (visibleEntries.isEmpty()) item { EmptyCard("暂无诊断日志", "启动扫描或连接后，阶段日志会显示在这里。") }
            items(visibleEntries.asReversed(), key = { it.sequence }) { entry ->
                Card(colors = CardDefaults.cardColors(containerColor = Color.White), shape = RoundedCornerShape(12.dp)) {
                    Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 9.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(formatter.format(entry.timestamp), color = Muted, style = MaterialTheme.typography.labelSmall)
                            Spacer(Modifier.width(8.dp))
                            Text(entry.component, color = when (entry.level) {
                                DiagnosticLevel.INFO -> Blue
                                DiagnosticLevel.WARNING -> Color(0xFFB06A00)
                                DiagnosticLevel.ERROR -> Color(0xFFC62828)
                            }, fontWeight = FontWeight.SemiBold, style = MaterialTheme.typography.labelMedium)
                        }
                        Text(entry.message, color = Ink, style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
    }
}

@Composable
private fun EmptyCard(title: String, detail: String) {
    Card(colors = CardDefaults.cardColors(containerColor = Color.White), shape = RoundedCornerShape(18.dp)) {
        Column(Modifier.fillMaxWidth().padding(22.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            Text(title, fontWeight = FontWeight.Bold, color = Ink); Spacer(Modifier.height(5.dp)); Text(detail, color = Muted, style = MaterialTheme.typography.bodySmall)
        }
    }
}

private fun formatBytes(value: Long): String = when {
    value >= 1024L * 1024 * 1024 -> "%.1f GiB".format(value / (1024.0 * 1024 * 1024))
    value >= 1024L * 1024 -> "%.1f MiB".format(value / (1024.0 * 1024))
    value >= 1024 -> "%.1f KiB".format(value / 1024.0)
    else -> "$value B"
}

private val BLUETOOTH_PERMISSIONS = arrayOf(
    Manifest.permission.BLUETOOTH_SCAN,
    Manifest.permission.BLUETOOTH_CONNECT,
    Manifest.permission.BLUETOOTH_ADVERTISE,
    Manifest.permission.POST_NOTIFICATIONS,
)
