package com.bluelink.android

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.bluelink.android.domain.*
import com.bluelink.android.data.local.toTransferItem
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.conversation.ImagePreview
import com.bluelink.android.ui.conversation.MessageSearchScreen
import com.bluelink.android.ui.devices.*
import com.bluelink.android.ui.files.*
import com.bluelink.android.ui.settings.*
import com.bluelink.android.ui.theme.BlueLinkTheme
import java.io.File
import java.time.Instant
import java.util.UUID

/** Debug-only physical-device rendering. Only isolated QA data; no real-history mutations. */
class AcceptanceActivity : ComponentActivity() {
    private var scenario by mutableStateOf("files")
    private var appearance by mutableStateOf("light")
    private var language by mutableStateOf("zh-CN")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        readScene(intent)
        val image = File(cacheDir, "acceptance/bluelink-preview.png")
        image.parentFile?.mkdirs()
        if (!image.exists()) assets.open("bluelink-final-logo.png").use { input -> image.outputStream().use(input::copyTo) }
        setContent {
            BlueLinkTheme(theme = appearance, language = language) {
                DeviceScreenTheme { key(scenario, appearance, language) { Scenes(scenario, image) { finish() } } }
            }
        }
    }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); setIntent(intent); readScene(intent) }
    private fun readScene(intent: Intent) {
        scenario = intent.getStringExtra("scene") ?: "files"
        language = intent.getStringExtra("language")?.takeIf { it in setOf("zh-CN", "zh-TW", "en") } ?: "zh-CN"
        appearance = intent.getStringExtra("theme")?.takeIf { it in setOf("light", "dark") } ?: "light"
    }
}

@Composable
private fun Scenes(scene: String, image: File, close: () -> Unit) {
    if (scene.startsWith("thumbnail-geometry-")) { ThumbnailGeometryAcceptance(scene.removePrefix("thumbnail-geometry-"), close); return }
    if (scene == "image-preview-original") { ImagePreviewOriginalAcceptance(close); return }
    if (scene == "conversation-scroll") { ConversationScrollAcceptance(image, close); return }
    if (scene == "thumbnail-transfer") { ThumbnailTransferAcceptance(image, close); return }
    if (scene == "file-icons") { FileIconAcceptance(); return }
    if (scene == "physical-discovery") { PhysicalDiscoveryAcceptance(close); return }
    if (scene in setOf("settings-about", "settings-privacy-policy", "settings-agreement")) {
        var page by remember { mutableIntStateOf(when (scene) {
            "settings-privacy-policy" -> SETTINGS_PRIVACY_POLICY
            "settings-agreement" -> SETTINGS_AGREEMENT
            else -> 3
        }) }
        Scaffold(topBar = { SettingsHeader(page) { if (page == 3) close() else page = 3 } }) { padding ->
            when (page) {
                3 -> AboutSettings(Modifier.padding(padding), { page = it }, {})
                SETTINGS_LICENSES -> LicensesScreen(Modifier.padding(padding))
                SETTINGS_HELP -> HelpFeedbackScreen(Modifier.padding(padding), emptyList(), { page = it })
                else -> SupportArticle(Modifier.padding(padding), page) { page = SETTINGS_HELP }
            }
        }
        return
    }
    if (scene == "message-visibility") { MessageVisibilityAcceptance(close); return }
    if (scene == "forget-device") { ForgetDeviceAcceptance(close); return }
    if (scene == "physical-transfer") { PhysicalTransferAcceptance(close); return }
    if (scene.startsWith("device-actions-") || scene.startsWith("device-state-")) { DeviceMenuAcceptance(scene, close); return }
    if (scene.startsWith("attachment-actions-") || scene.startsWith("file-actions-")) { AttachmentMenuAcceptance(scene, image, close); return }
    if (scene.startsWith("usb-session-")) { UsbSessionAcceptance(scene, close); return }
    if (scene.startsWith("usb-settings-")) {
        var settings by remember { mutableStateOf(com.bluelink.android.data.local.AppSettings(usbTransferEnabled = scene.endsWith("on"))) }
        Scaffold(topBar = { SettingsHeader(0, close) }) { padding ->
            ConnectionSettings(Modifier.padding(padding), settings, emptyList(), BluetoothAccessState.READY,
                {}, { settings = it }, {})
        }
        return
    }
    if (scene == "identity-association") { IdentityAssociationAcceptance(close); return }
    if (scene.startsWith("security-")) {
        var visible by remember { mutableStateOf(true) }
        var result by remember { mutableStateOf("") }
        val request = remember(scene) {
            SecurityRequest("BlueLink QA · 验收电脑", "729 348", "D774:3514:983B:8204:5419:1618",
                "A92C:0E6F:7B44:19D2:63A8:C501", true, "148B:2782:ACBD:1700:4859:775D", platform = PeerPlatform.WINDOWS).also {
                when (scene.removePrefix("security-")) {
                    "waiting" -> it.confirm()
                    "rejected" -> it.finish(TrustStage.REJECTED)
                    "timeout" -> it.finish(TrustStage.TIMED_OUT)
                    "changed" -> it.finish(TrustStage.IDENTITY_CHANGED)
                    "closed" -> it.finish(TrustStage.REMOTE_CLOSED)
                }
            }
        }
        Column(Modifier.fillMaxSize().background(DeviceColors.Canvas).padding(24.dp)) {
            Text("BlueLink · QA", color = DeviceColors.Secondary)
            Text(result, color = DeviceColors.Ink)
        }
        if (visible) com.bluelink.android.ui.components.SecurityPrompt(request,
            confirm = { request.confirm() }, dismiss = { request.cancel(); result = "CANCELED"; visible = false },
            retry = { result = "RETRY_REQUESTED"; visible = false }, manageTrust = { result = "MANAGE_TRUST"; visible = false })
        return
    }
    if (scene in setOf("settings-connection", "settings-trusted", "settings-trusted-empty")) {
        var page by remember { mutableIntStateOf(if (scene == "settings-connection") 0 else SETTINGS_TRUSTED) }
        var settings by remember { mutableStateOf(com.bluelink.android.data.local.AppSettings(localDeviceName = "BlueLink QA Android")) }
        val peers = remember { if (scene == "settings-trusted-empty") emptyList() else
            List(4) { index -> ConversationSummary("qa-trusted-$index", "BlueLink QA ${index + 1}",
                PeerPlatform.WINDOWS, DeviceAvailability.OFFLINE, isTrusted = index < 3,
                lastConnectedAt = 1788854400000 + index * 60000) } }
        androidx.activity.compose.BackHandler { if (page == 0) close() else page = 0 }
        Scaffold(topBar = { SettingsHeader(page) { if (page == 0) close() else page = 0 } }) { padding ->
            if (page == SETTINGS_TRUSTED) TrustedDevicesScreen(Modifier.padding(padding), peers, {}, {})
            else ConnectionSettings(Modifier.padding(padding), settings, peers,
                BluetoothAccessState.READY, {}, { settings = it }, { page = it })
        }
        return
    }
    if (scene == "settings-privacy") {
        var settings by remember { mutableStateOf(com.bluelink.android.data.local.AppSettings()) }
        Scaffold(topBar = { SettingsHeader(2, close) }) { padding ->
            PrivacySettings(Modifier.padding(padding), settings, { settings = it }, "AABB:CCDD:EEFF", emptyList()) { /* In-memory confirmation exercise only. */ }
        }
        return
    }
    if (scene == "settings-files") {
        var settings by remember { mutableStateOf(com.bluelink.android.data.local.AppSettings()) }
        Scaffold(topBar = { SettingsHeader(1, close) }) { padding ->
            FileStorageSettings(Modifier.padding(padding), settings, { settings = it }, {}, close)
        }
        return
    }
    if (scene == "receive-confirmation" || scene == "file-conflict") {
        var result by remember { mutableStateOf<String?>(null) }
        Column(Modifier.fillMaxSize().background(DeviceColors.Canvas).padding(24.dp)) {
            Text("BlueLink · QA", color = DeviceColors.Secondary)
            result?.let { Text(it) }
        }
        if (result == null) {
            if (scene == "file-conflict") FileConflictSheet("BlueLink-acceptance.pdf") { if (result == null) result = it.name }
            else IncomingFileConfirmation(IncomingFileRequest(sessionId = UUID.randomUUID(), peerName = "BlueLink QA",
                transfer = TransferItem(UUID.randomUUID(), "BlueLink-acceptance.pdf", 1024 * 1024, outgoing = false,
                    status = TransferStatus.OFFERED, peerId = "qa-peer"))) { result = if (it) "ACCEPTED" else "REJECTED" }
        }
        return
    }
    if (scene.startsWith("help-article-")) {
        var page by remember { mutableIntStateOf(when (scene.removePrefix("help-article-")) {
            "connection" -> SETTINGS_HELP_CONNECTION
            "messages" -> SETTINGS_HELP_MESSAGES
            else -> SETTINGS_HELP_FAQ
        }) }
        Scaffold(topBar = { SettingsHeader(page, close) }) { padding ->
            if (page == SETTINGS_HELP) HelpFeedbackScreen(Modifier.padding(padding), emptyList(), { page = it })
            else SupportArticle(Modifier.padding(padding), page) { page = SETTINGS_HELP }
        }
        return
    }
    if (scene.startsWith("support")) {
        var page by remember { mutableIntStateOf(when (scene) { "support-about" -> 3; "support-licenses" -> SETTINGS_LICENSES; else -> SETTINGS_HELP }) }
        val blocked = remember(scene) { if (scene == "support-failure") File(image.parentFile, "feedback-blocked").apply { writeText("Debug-only unwritable feedback directory fixture") } else null }
        androidx.activity.compose.BackHandler { if (page == SETTINGS_HELP || page == 3) close() else page = settingsParent(page) }
        Scaffold(topBar = { SettingsHeader(page) { if (page == SETTINGS_HELP || page == 3) close() else page = settingsParent(page) } }) { padding ->
            when (page) {
                3 -> AboutSettings(Modifier.padding(padding), { page = it }, {})
                SETTINGS_HELP -> HelpFeedbackScreen(Modifier.padding(padding), listOf(DiagnosticEntry(sequence = 1, level = DiagnosticLevel.INFO, component = "Acceptance", message = "Only a debug event")), { page = it }, packageDirectory = blocked)
                SETTINGS_LICENSES -> LicensesScreen(Modifier.padding(padding))
                else -> SupportArticle(Modifier.padding(padding), page) { page = SETTINGS_HELP }
            }
        }
        return
    }
    val now = Instant.now()
    val rows = remember {
        TransferStatus.entries.mapIndexed { i, status ->
            TransferItem(UUID.nameUUIDFromBytes("qa-$i".toByteArray()),
                if (status == TransferStatus.COMPLETED) image.name else "BlueLink-QA-${status.name}.pdf",
                if (status == TransferStatus.COMPLETED) image.length() else 1024 * 1024,
                completedBytes = if (status == TransferStatus.COMPLETED) image.length() else 512 * 1024,
                outgoing = i % 2 == 0, status = status, peerId = "qa-peer",
                localUri = if (status == TransferStatus.COMPLETED || i % 2 == 0) Uri.fromFile(image).toString() else null,
                mimeType = if (status == TransferStatus.COMPLETED) "image/png" else "application/pdf",
                failureDetail = "QA: Bluetooth disconnected. In-memory record only.",
                startedAtEpochMs = now.minusSeconds(i * 3600L).toEpochMilli())
        }
    }
    val historyRows = remember {
        val stored = listOf("PC-2026-08-20.pdf" to "qa-peer", "Phone-2026-08-21.pdf" to "qa-other").mapIndexed { i, (name, peerId) ->
            com.bluelink.android.data.local.TransferEntity(
                transferId = UUID.nameUUIDFromBytes(name.toByteArray()).toString(), peerId = peerId,
                direction = if (i == 0) "OUTGOING" else "INCOMING", status = "COMPLETED", fileName = name,
                totalBytes = 1024, completedBytes = 1024,
                createdAt = Instant.parse("2026-08-${20 + i}T02:30:00Z").toEpochMilli(),
                updatedAt = Instant.parse("2026-08-${20 + i}T02:31:00Z").toEpochMilli())
        }.map { it.toTransferItem() }
        TransferHistoryIndex().restore(stored).items.values.toList()
    }
    val context = androidx.compose.ui.platform.LocalContext.current
    var receiptMessages by remember { mutableStateOf<List<ChatItem>>(emptyList()) }
    LaunchedEffect(scene) {
        if (scene == "conversation-receipts") {
            receiptMessages = try { verifyMessagePersistence(context) }
            catch (canceled: kotlinx.coroutines.CancellationException) { throw canceled }
            catch (failure: Exception) { listOf(ChatItem(text = "QA FAILED: ${failure.javaClass.simpleName}: ${failure.message}",
                outgoing = false, status = MessageStatus.RECEIVED)) }
        }
    }
    val peer = remember { ConversationSummary("qa-peer", "BlueLink QA PC", PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED) }
    val photo = rows.first { it.status == TransferStatus.COMPLETED }.attachment()
    val messages = remember { listOf(
        ChatItem(text = "BlueLink QA: secure connection established.", outgoing = false, status = MessageStatus.RECEIVED),
        ChatItem(text = "File received.", outgoing = true, status = MessageStatus.DELIVERED),
        ChatItem(text = "", outgoing = false, status = MessageStatus.RECEIVED, kind = ChatItemKind.IMAGE, attachments = listOf(photo)),
        ChatItem(text = "", outgoing = true, status = MessageStatus.SENT, kind = ChatItemKind.FILE,
            attachments = listOf(rows.first().attachment()), timestamp = now.minusSeconds(86400))) }
    var historyMessages by remember { mutableStateOf(List(40) { i -> ChatItem(text = "QA history ${i + 1}",
        outgoing = false, status = MessageStatus.RECEIVED, timestamp = now.plusSeconds(i.toLong())) }) }
    val statusMessages = remember { listOf(MessageStatus.LOCAL_QUEUED, MessageStatus.SENDING, MessageStatus.SENT,
        MessageStatus.DELIVERED, MessageStatus.READ, MessageStatus.FAILED).mapIndexed { i, status ->
        ChatItem(text = "QA ${i + 1}: text message", outgoing = true, status = status, timestamp = now.plusSeconds(i.toLong()))
    } }
    val selectionMessages = remember { listOf(
        ChatItem(text = "第一行：收到的消息用于跨行选择。\n第二行：拖出气泡后继续左右移动。\n第三行：返回气泡仍应连续选择。",
            outgoing = false, status = MessageStatus.RECEIVED),
        ChatItem(text = "Alpha bravo charlie delta\nEcho foxtrot golf hotel\nIndia juliet kilo lima\nMike november oscar papa",
            outgoing = true, status = MessageStatus.DELIVERED),
        ChatItem(text = "短消息", outgoing = true, status = MessageStatus.DELIVERED)) }
    var selected by remember { mutableStateOf<TransferItem?>(null) }
    var details by remember { mutableStateOf<TransferItem?>(null) }
    var failure by remember { mutableStateOf<TransferItem?>(null) }
    var preview by remember { mutableStateOf(scene == "preview") }
    var feedback by remember { mutableStateOf<String?>(null) }
    if (preview) ImagePreview(photo, transfer = rows.first { it.status == TransferStatus.COMPLETED }, peerName = peer.peerName) { preview = false }
    if (preview) return
    Column(Modifier.fillMaxSize().background(DeviceColors.Canvas)) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
            Text("BlueLink · QA", Modifier.weight(1f), style = MaterialTheme.typography.labelSmall, color = DeviceColors.Secondary)
            if (scene == "conversation-live") {
                TextButton(onClick = { historyMessages = historyMessages + List(3) { i ->
                    ChatItem(text = "QA incoming ${historyMessages.size + i + 1}", outgoing = false,
                        status = MessageStatus.RECEIVED, timestamp = now.plusSeconds((historyMessages.size + i).toLong()))
                } }) { Text("QA +3") }
                TextButton(onClick = { historyMessages = historyMessages + ChatItem(text = "QA own send", outgoing = true,
                    status = MessageStatus.SENT, timestamp = now.plusSeconds(historyMessages.size.toLong())) }) { Text("QA Send") }
            }
            TextButton(onClick = close) { Text(androidx.compose.ui.res.stringResource(R.string.close)) }
        }
        if (scene.startsWith("devices")) {
            val access = when (scene) {
                "devices-permission" -> BluetoothAccessState.REQUIRED
                "devices-denied" -> BluetoothAccessState.DENIED
                "devices-off" -> BluetoothAccessState.OFF
                else -> BluetoothAccessState.READY
            }
            DeviceHomeHeader(access, if (access == BluetoothAccessState.READY) 1 else 0)
            DevicesScreen(Modifier.weight(1f), emptyList(),
                listOf(peer.copy(unreadCount = 7, availability = if (access == BluetoothAccessState.READY) DeviceAvailability.CONNECTED else DeviceAvailability.OFFLINE),
                    peer.copy(peerId = "qa-offline", peerName = "BlueLink QA Phone", platform = PeerPlatform.ANDROID, availability = DeviceAvailability.OFFLINE, unreadCount = 2)),
                rows, ConnectionState(ConnectionPhase.CONNECTED), DiscoveryState(), access, null, null,
                {}, {}, {}, {}, {}, {})
        } else if (scene.startsWith("conversation")) ConversationScreen(Modifier.weight(1f), peer.peerId,
            when (scene) { "conversation-empty" -> emptyList(); "conversation-receipts" -> receiptMessages; "conversation-live" -> historyMessages; "conversation-statuses" -> statusMessages; "conversation-selection" -> selectionMessages; else -> messages },
            ConnectionState(if (scene == "conversation-offline") ConnectionPhase.DISCONNECTED else ConnectionPhase.CONNECTED),
            listOf(if (scene == "conversation-offline") peer.copy(availability = DeviceAvailability.OFFLINE) else peer), rows, true,
            "downloads://BlueLink", close, {}, {}, {}, {}, {}, {}, { preview = true }, { selected = it }, { selected = it })
        else if (scene.startsWith("search")) MessageSearchScreen(if (scene == "search-empty") emptyList() else messages, peer.peerName, true, close, {}, Modifier.weight(1f))
        else FilesScreen(Modifier.weight(1f), when (scene) { "files-empty", "files-global-empty" -> emptyList(); "files-history" -> historyRows; else -> rows },
            listOf(peer, peer.copy(peerId = "qa-other", peerName = "BlueLink QA Phone", platform = PeerPlatform.ANDROID)),
            scopePeerId = if (scene == "files-empty") peer.peerId else null, receiveDirectory = "downloads://BlueLink",
            openSettings = { feedback = "此验收页面不修改接收目录" },
            open = { if (it.status == TransferStatus.COMPLETED) preview = true else selected = it }, more = { selected = it })
    }
    LaunchedEffect(scene) {
        if (scene.startsWith("menu-")) selected = rows.firstOrNull { it.status.name == scene.removePrefix("menu-") }
        if (scene == "details") details = rows.first { it.status == TransferStatus.COMPLETED }
        if (scene == "failure") failure = rows.first { it.status == TransferStatus.FAILED }
    }
    selected?.let { item ->
        TransferActionSheet(item, true, { selected = null }) { action ->
            when (action) {
                TransferAction.DETAILS -> details = item
                TransferAction.FAILURE -> failure = item
                TransferAction.OPEN -> preview = true
                else -> feedback = "验收页面仅展示状态，未执行 ${action.name}"
            }
        }
    }
    details?.let { FileDetailsPrompt(it.attachment(), it, peer.peerName, dismiss = { details = null }) }
    failure?.let { TransferFailurePrompt(it, dismiss = { failure = null }) }
    if (feedback != null) com.bluelink.android.ui.components.BlueLinkPrompt("验收提示", { feedback = null }) { Text(feedback.orEmpty()) }
}

private fun TransferItem.attachment() = ChatAttachment(id, id, name, mimeType, totalBytes, localUri,
    status.name, completedBytes = completedBytes)
