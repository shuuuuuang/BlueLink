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
import androidx.activity.compose.BackHandler
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
import androidx.compose.foundation.layout.consumeWindowInsets
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
import com.bluelink.android.domain.canSendContent
import com.bluelink.android.domain.ConversationSummary
import com.bluelink.android.domain.DeviceAction
import com.bluelink.android.domain.DeviceActions
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
import com.bluelink.android.ui.components.ActionOption
import com.bluelink.android.ui.components.ActionSheet
import com.bluelink.android.ui.files.FileDetailsPrompt
import com.bluelink.android.ui.files.TransferFailurePrompt
import com.bluelink.android.domain.TransferActions
import com.bluelink.android.domain.TransferAction
import com.bluelink.android.ui.content.contentStatus
import com.bluelink.android.ui.components.BlueLinkConfirmation
import com.bluelink.android.ui.components.BlueLinkPrompt
import com.bluelink.android.ui.components.PromptField
import com.bluelink.android.ui.components.BlueLinkAppHeader
import com.bluelink.android.ui.components.BlueLinkLogo
import com.bluelink.android.bluetooth.markBluetoothPermissionRequested
import com.bluelink.android.bluetooth.nearbyPermissions
import com.bluelink.android.bluetooth.readBluetoothAccess
import com.bluelink.android.bluetooth.rememberBluetoothAccess
import com.bluelink.android.domain.BluetoothAccessState
import com.bluelink.android.domain.DeviceScreenState
import com.bluelink.android.ui.devices.DeviceActionSheet
import com.bluelink.android.ui.devices.NearbyDeviceActionSheet
import com.bluelink.android.ui.devices.DeviceDetailsPrompt
import com.bluelink.android.ui.devices.DeviceBottomNavigation
import com.bluelink.android.ui.devices.DeviceHomeHeader
import com.bluelink.android.ui.devices.DevicesScreen
import com.bluelink.android.ui.devices.DeviceColors
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.conversation.ImagePreview
import com.bluelink.android.ui.files.FilesScreen
import com.bluelink.android.ui.settings.*
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
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private var acceptanceKeepScreenOn = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        acceptanceKeepScreenOn = acceptanceWakeSession.value
            ?: savedInstanceState?.getBoolean(ACCEPTANCE_KEEP_SCREEN_ON) ?: false
        applyAcceptanceWakePolicy(intent)
        if (BuildConfig.DEBUG) lifecycleScope.launch {
            // USB attachment can open another MainActivity over the launcher instance.
            // All BlueLink windows in this debug session share explicit keep-awake changes.
            acceptanceWakeSession.collect { enabled ->
                acceptanceKeepScreenOn = enabled == true
                val flag = android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON
                setTurnScreenOn(acceptanceKeepScreenOn)
                if (acceptanceKeepScreenOn) window.addFlags(flag) else window.clearFlags(flag)
            }
        }
        setContent { BlueLinkApp(onPermissionsReady = ::startBluetoothService, fileInfo = ::fileInfo) }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putBoolean(ACCEPTANCE_KEEP_SCREEN_ON, acceptanceKeepScreenOn)
        super.onSaveInstanceState(outState)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        applyAcceptanceWakePolicy(intent)
    }

    private fun applyAcceptanceWakePolicy(intent: Intent) {
        if (!BuildConfig.DEBUG) return
        val flag = android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON
        // Opening BlueLink from its icon or notification must not end the
        // current acceptance session. Only an explicit command changes it.
        if (intent.hasExtra(ACCEPTANCE_KEEP_SCREEN_ON)) {
            acceptanceKeepScreenOn = intent.getBooleanExtra(ACCEPTANCE_KEEP_SCREEN_ON, false)
        }
        acceptanceWakeSession.value = acceptanceKeepScreenOn
        setTurnScreenOn(acceptanceKeepScreenOn)
        if (acceptanceKeepScreenOn) window.addFlags(flag) else window.clearFlags(flag)
    }

    private companion object {
        const val ACCEPTANCE_KEEP_SCREEN_ON = "bluelink.acceptance.keep_screen_on"
        val acceptanceWakeSession = kotlinx.coroutines.flow.MutableStateFlow<Boolean?>(null)
    }

    private fun startBluetoothService() {
        (application as BlueLinkApplication).runtime.onAppForegrounded()
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

    override fun onResume() {
        super.onResume()
        (application as BlueLinkApplication).runtime.setMessagePageResumed(true)
    }

    override fun onPause() {
        (application as BlueLinkApplication).runtime.setMessagePageResumed(false)
        super.onPause()
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

private data class ConfirmAction(val title: String, val message: String, val confirmLabel: String, val note: String? = null, val action: () -> Unit)

@Composable
private fun BlueLinkApp(
    onPermissionsReady: () -> Unit,
    fileInfo: (android.net.Uri) -> Pair<String, Long>,
    model: MainViewModel = viewModel(),
) {
    val settings by model.settings.collectAsStateWithLifecycle()
    // The entire content, including launchers and menu callbacks, must capture
    // the app-selected locale instead of the Activity/system locale.
    BlueLinkTheme(settings.theme, settings.language) {
        BlueLinkAppContent(onPermissionsReady, fileInfo, model)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun BlueLinkAppContent(
    onPermissionsReady: () -> Unit,
    fileInfo: (android.net.Uri) -> Pair<String, Long>,
    model: MainViewModel = viewModel(),
) {
    val devices by model.devices.collectAsStateWithLifecycle()
    val discovery by model.discovery.collectAsStateWithLifecycle()
    val connection by model.connection.collectAsStateWithLifecycle()
    val messages by model.messages.collectAsStateWithLifecycle()
    val transfers by model.transfers.collectAsStateWithLifecycle()
    val allTransfers by model.allTransfers.collectAsStateWithLifecycle()
    val trust by model.trustPrompt.collectAsStateWithLifecycle()
    val securityRequest by model.securityRequest.collectAsStateWithLifecycle()
    val operationFailed by model.operationFailed.collectAsStateWithLifecycle()
    val fileConflictRequest by model.fileConflictRequest.collectAsStateWithLifecycle()
    val incomingFileRequest by model.incomingFileRequest.collectAsStateWithLifecycle()
    val usbState by model.usbState.collectAsStateWithLifecycle()
    val diagnostics by model.diagnostics.collectAsStateWithLifecycle()
    val identityFingerprint by model.identityFingerprint.collectAsStateWithLifecycle()
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
    var infoAttachment by remember { mutableStateOf<ChatAttachment?>(null) }
    var failureTransfer by remember { mutableStateOf<TransferItem?>(null) }
    var confirmAction by remember { mutableStateOf<ConfirmAction?>(null) }
    var selected by remember { mutableIntStateOf(0) }
    var showSettings by remember { mutableStateOf(false) }
    var settingsPage by androidx.compose.runtime.saveable.rememberSaveable { mutableIntStateOf(SETTINGS_HOME) }
    val context = LocalContext.current
    var permissionRevision by remember { mutableIntStateOf(0) }
    val bluetoothAccess = rememberBluetoothAccess(permissionRevision)
    var selectedPeerId by remember { mutableStateOf<String?>(null) }
    var requestedConversationTab by remember { mutableStateOf<Int?>(null) }
    var requestedConversationSearch by remember(selectedPeerId) { mutableStateOf(false) }
    var connectingDeviceKey by remember { mutableStateOf<String?>(null) }
    val permissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        permissionRevision++
        if (readBluetoothAccess(context).canUseBluetooth) model.discover()
    }
    val permissionAction: () -> Unit = {
        if (bluetoothAccess == BluetoothAccessState.DENIED) {
            runCatching {
                context.startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                    android.net.Uri.parse("package:" + context.packageName)))
            }.onFailure { Toast.makeText(context, context.getString(R.string.content_open_settings_failed), Toast.LENGTH_SHORT).show() }
        } else {
            markBluetoothPermissionRequested(context)
            permissions.launch(nearbyPermissions)
        }
    }
    val connectDevice: (NearbyDevice) -> Unit = { device ->
        if (readBluetoothAccess(context).canUseBluetooth) {
            connectingDeviceKey = device.stableKey.ifBlank { device.address }
            model.connect(device)
        }
    }
    val openConversation: (String) -> Unit = { peerId ->
        selectedPeerId = peerId
        requestedConversationTab = 0
        model.selectPeer(peerId)
        selected = 1
    }
    BackHandler(enabled = showSettings || selected != 0) {
        if (showSettings && settingsPage != SETTINGS_HOME) settingsPage = settingsParent(settingsPage)
        else { showSettings = false; selected = 0 }
    }
    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null && connection.canSendContent(readBluetoothAccess(context))) {
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
    val mtpDirectoryPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) runCatching { com.bluelink.android.usb.MtpSpool.grant(context, uri) }
            .onFailure { Toast.makeText(context, it.message ?: "USB 文件夹授权失败", Toast.LENGTH_LONG).show() }
    }
    var mtpGrantRequested by androidx.compose.runtime.saveable.rememberSaveable { mutableStateOf(false) }
    LaunchedEffect(settings.usbTransferEnabled) {
        if (!settings.usbTransferEnabled) mtpGrantRequested = false
        else if (!mtpGrantRequested && !com.bluelink.android.usb.MtpSpool.hasGrant(context)) {
            mtpGrantRequested = true
            Toast.makeText(context, "请选择或新建 BlueLinkUSB 专用文件夹，用于 USB 加密文件中转", Toast.LENGTH_LONG).show()
            mtpDirectoryPicker.launch(android.provider.DocumentsContract.buildDocumentUri(
                "com.android.externalstorage.documents", "primary:Download"))
        }
    }
    val localUpdatePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) runCatching {
            context.startActivity(Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, "application/vnd.android.package-archive")
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            })
        }.onFailure { Toast.makeText(context, it.message ?: context.getString(R.string.content_open_package_failed), Toast.LENGTH_SHORT).show() }
    }
    val interactionScope = rememberCoroutineScope()
    val saveCopyPicker = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("*/*")) { uri ->
        val attachment = pendingSaveAttachment
        pendingSaveAttachment = null
        if (uri != null && attachment != null) interactionScope.launch {
            try {
                kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { FileInteraction.copyTo(context, attachment, uri) }
                Toast.makeText(context, context.getString(R.string.content_copy_saved), Toast.LENGTH_SHORT).show()
            } catch (canceled: kotlinx.coroutines.CancellationException) { throw canceled }
            catch (failure: Exception) { Toast.makeText(context, failure.message ?: context.getString(R.string.content_save_failed), Toast.LENGTH_SHORT).show() }
        }
    }

    LaunchedEffect(bluetoothAccess) {
        if (bluetoothAccess.canUseBluetooth) onPermissionsReady()
    }

    val mainContentState = androidx.compose.runtime.saveable.rememberSaveableStateHolder()
    if (previewAttachment == null) {
        mainContentState.SaveableStateProvider("main") {
            Scaffold(
                containerColor = DeviceColors.Canvas,
                topBar = {
                    if (!showSettings && selected != 1) DeviceHomeHeader(bluetoothAccess,
                        DeviceScreenState.project(conversations, devices, bluetoothAccess, "").totalConnected,
                        title = if (selected == 2) context.getString(R.string.content_file_management) else context.getString(R.string.content_devices_conversations))
                    else if (showSettings) SettingsHeader(settingsPage) {
                        settingsPage = settingsParent(settingsPage)
                    }
                },
                bottomBar = {
                    if ((showSettings && settingsPage == SETTINGS_HOME) || (!showSettings && selected != 1)) DeviceBottomNavigation(
                        selected = if (showSettings) 2 else if (selected == 2) 1 else 0,
                    ) { destination ->
                        showSettings = destination == 2
                        if (showSettings) settingsPage = SETTINGS_HOME
                        selected = if (destination == 1) 2 else 0
                    }
                },
            ) { padding ->
                if (showSettings) SettingsScreen(Modifier.padding(padding), settings, conversations,
                    selectedPage = settingsPage, selectPage = { settingsPage = it },
                    save = model::saveSettings, chooseDirectory = { directoryPicker.launch(null) },
                    manageFiles = { showSettings = false; selectedPeerId = null; selected = 2 },
                    forgetPeer = model::forgetPeer, forgetAllPeers = model::forgetAllPeers,
                    performPrivacyAction = model::performPrivacyAction, fingerprint = identityFingerprint,
                    diagnostics = diagnostics,
                    bluetoothAccess = bluetoothAccess, changeBluetooth = {
                        if (bluetoothAccess == com.bluelink.android.domain.BluetoothAccessState.REQUIRED || bluetoothAccess == com.bluelink.android.domain.BluetoothAccessState.DENIED) permissionAction()
                        else runCatching {
                            context.startActivity(Intent(if (bluetoothAccess == com.bluelink.android.domain.BluetoothAccessState.OFF)
                                android.bluetooth.BluetoothAdapter.ACTION_REQUEST_ENABLE else android.provider.Settings.ACTION_BLUETOOTH_SETTINGS))
                        }.onFailure { Toast.makeText(context, context.getString(R.string.connection_system_error), Toast.LENGTH_SHORT).show() }
                    },
                    checkLocalUpdate = { localUpdatePicker.launch(arrayOf("application/vnd.android.package-archive")) })
                else when (selected) {
                    0 -> DevicesScreen(
                        Modifier.padding(padding), devices, conversations, allTransfers.values.toList(), connection, discovery,
                        access = bluetoothAccess, selectedPeerId = selectedPeerId,
                        connectingDeviceKey = connectingDeviceKey,
                        discover = { if (readBluetoothAccess(context).canUseBluetooth) model.discover() },
                        connect = connectDevice, permissionAction = permissionAction,
                        openConversation = openConversation,
                        longPressConversation = { actionConversation = it },
                        longPressNearby = { actionNearbyDevice = it },
                    )
                    // The Scaffold padding is already applied; descendants must only add
                    // the remaining IME inset, not the system-bar space a second time.
                    1 -> ConversationScreen(Modifier.padding(padding).consumeWindowInsets(padding), selectedPeerId, messages,
                        if (bluetoothAccess.canUseBluetooth || connection.transport == com.bluelink.android.domain.SessionTransport.USB) connection else connection.copy(phase = ConnectionPhase.OFFLINE), conversations,
                        transfers = transfers.values.toList(), receiveDirectory = settings.downloadDirectory,
                        showImageThumbnails = settings.showImageThumbnails,
                        searchRequested = requestedConversationSearch, searchRequestHandled = { requestedConversationSearch = false },
                        requestedTab = requestedConversationTab, tabRequestHandled = { requestedConversationTab = null },
                        messageListVisibilityChanged = model::setVisibleMessagePeer,
                        usbEnabled = settings.usbTransferEnabled, usbSnapshot = usbState,
                        back = { selected = 0 }, more = { actionConversation = it },
                        openSettings = { settingsPage = 1; showSettings = true },
                        openTransfer = { transfer ->
                            if (transfer.asAttachment().canOpen && transfer.asAttachment().isImage) previewAttachment = transfer.asAttachment()
                            else if (transfer.asAttachment().canOpen) runCatching { FileInteraction.open(context, transfer.asAttachment()) }
                                .onFailure { Toast.makeText(context, it.message ?: context.getString(R.string.content_open_failed), Toast.LENGTH_SHORT).show() }
                            else actionTransfer = transfer
                        }, moreTransfer = { actionTransfer = it },
                        send = { if (connection.canSendContent(readBluetoothAccess(context))) model.sendMessage(it) },
                        pickFile = { if (connection.canSendContent(readBluetoothAccess(context))) filePicker.launch(arrayOf("*/*")) },
                        longPressMessage = { actionMessage = it },
                        longPressAttachment = { actionAttachment = it },
                        openAttachment = { attachment ->
                            if (attachment.canOpen && attachment.isImage) previewAttachment = attachment
                            else runCatching { FileInteraction.open(context, attachment) }
                                .onFailure { Toast.makeText(context, it.message ?: context.getString(R.string.content_open_failed), Toast.LENGTH_SHORT).show() }
                        })
                    2 -> FilesScreen(Modifier.padding(padding), allTransfers.values.toList(), conversations,
                        receiveDirectory = settings.downloadDirectory,
                        openSettings = { settingsPage = 1; showSettings = true }, more = { actionTransfer = it },
                        open = { transfer ->
                            val attachment = transfer.asAttachment()
                            if (attachment.canOpen && attachment.isImage) previewAttachment = attachment
                            else if (attachment.canOpen) runCatching { FileInteraction.open(context, attachment) }
                                .onFailure { Toast.makeText(context, it.message ?: context.getString(R.string.content_open_failed), Toast.LENGTH_SHORT).show() }
                            else actionTransfer = transfer
                        })
                    else -> DiagnosticsScreen(Modifier.padding(padding), diagnostics, connection, discovery,
                        clear = model::clearDiagnostics)
                }
            }

        }
    }

    if (trust == null && incomingFileRequest == null) fileConflictRequest?.let { request ->
        com.bluelink.android.ui.files.FileConflictSheet(request.fileName) {
            model.resolveFileConflict(request.requestId, it)
        }
    }
    if (trust == null && securityRequest == null) incomingFileRequest?.let { request ->
        com.bluelink.android.ui.files.IncomingFileConfirmation(request) { model.confirmIncomingFile(request.requestId, it) }
    }
    if (operationFailed) BlueLinkPrompt(androidx.compose.ui.res.stringResource(R.string.privacy_failed), model::dismissOperationFailure) {
        Text(androidx.compose.ui.res.stringResource(R.string.privacy_failed_note))
    }
    trust?.let { prompt ->
        if (prompt.connectionConfirmation) BlueLinkPrompt(androidx.compose.ui.res.stringResource(R.string.connection_request_title),
            { model.confirmTrust(false, prompt.requestId) }, footer = {
                OutlinedButton(onClick = { model.confirmTrust(false, prompt.requestId) }) { Text(androidx.compose.ui.res.stringResource(R.string.connection_reject)) }
                Button(onClick = { model.confirmTrust(true, prompt.requestId) }) { Text(androidx.compose.ui.res.stringResource(R.string.connection_allow)) }
            }) {
            Text(androidx.compose.ui.res.stringResource(R.string.connection_request_message, prompt.peerName))
            Text(androidx.compose.ui.res.stringResource(R.string.connection_request_note), color = DeviceColors.Secondary)
        }
    }
    if (trust == null) securityRequest?.let { request ->
        com.bluelink.android.ui.components.SecurityPrompt(request,
            confirm = { model.confirmSecurityRequest(request.id) },
            dismiss = { model.dismissSecurityRequest(request.id) },
            retry = { model.retrySecurityRequest(request.id) },
            manageTrust = { model.dismissSecurityRequest(request.id); showSettings = true; settingsPage = SETTINGS_TRUSTED })
    }
    previewAttachment?.let { attachment ->
        val transfer = allTransfers[attachment.transferId]
        val owner = messages.firstOrNull { message -> message.attachments.any { it.attachmentId == attachment.attachmentId } }
        ImagePreview(attachment, transfer,
            conversations.firstOrNull { it.peerId == (transfer?.peerId ?: selectedPeerId) }?.peerName,
            outgoing = transfer?.outgoing ?: owner?.outgoing,
            timestamp = owner?.timestamp ?: transfer?.let { Instant.ofEpochMilli(it.startedAtEpochMs) },
            dismiss = { previewAttachment = null })
    }

    actionConversation?.let { selectedConversation ->
        val conversation = conversations.firstOrNull { it.peerId == selectedConversation.peerId }
            ?: selectedConversation.copy(availability = DeviceAvailability.OFFLINE)
        val nearby = devices.firstOrNull {
            it.address.equals(conversation.transportAddress, true) ||
                (it.discoveryId.isNotBlank() && conversation.peerId.startsWith(it.discoveryId, true))
        }
        val task = DeviceActions.transfer(conversation.peerId, allTransfers.values)
        DeviceActionSheet(conversation, task,
            canConnect = bluetoothAccess.canUseBluetooth && nearby?.let(DeviceActions::canConnect) == true,
            dismiss = { actionConversation = null },
            searchConversation = if (selected == 1 && selectedPeerId == conversation.peerId) {
                { requestedConversationSearch = true }
            } else null) { action ->
            when (action) {
                DeviceAction.OPEN -> openConversation(conversation.peerId)
                DeviceAction.CONNECT -> nearby?.let(connectDevice)
                DeviceAction.INFO -> infoConversation = conversation
                DeviceAction.TRANSFERS -> { openConversation(conversation.peerId); requestedConversationTab = 1 }
                DeviceAction.PAUSE -> task?.let(model::pauseTransfer)
                DeviceAction.RESUME -> task?.let(model::resumeTransfer)
                DeviceAction.DISCONNECT -> model.disconnectPeer(conversation.peerId)
                DeviceAction.CLEAR -> confirmAction = ConfirmAction(context.getString(R.string.content_clear_conversation),
                    context.getString(R.string.content_clear_conversation_body, conversation.peerName), context.getString(R.string.content_clear)) {
                    model.clearConversation(conversation.peerId)
                }
                DeviceAction.REMOVE_TRUST -> confirmAction = ConfirmAction(context.getString(R.string.content_remove_trust),
                    context.getString(R.string.content_remove_trust_body, conversation.peerName), context.getString(R.string.content_remove)) {
                    model.forgetPeer(conversation.peerId)
                }
            }
        }
    }
    actionNearbyDevice?.let { selectedDevice ->
        val device = devices.firstOrNull { it.address == selectedDevice.address } ?: selectedDevice
        NearbyDeviceActionSheet(device,
            canConnect = bluetoothAccess.canUseBluetooth && DeviceActions.canConnect(device) &&
                !(connectingDeviceKey == device.stableKey.ifBlank { device.address } &&
                    connection.transportAddress.equals(device.address, true) && connection.phase in
                    setOf(ConnectionPhase.CONNECTING, ConnectionPhase.SECURE_HANDSHAKE, ConnectionPhase.TRUST_REQUIRED)),
            dismiss = { actionNearbyDevice = null }, connect = { connectDevice(device) }, info = {
                infoConversation = ConversationSummary(device.discoveryId.ifBlank { context.getString(R.string.content_peer_not_provided) },
                    device.name, device.platform, DeviceAvailability.CONNECTABLE, transportAddress = device.address)
            })
    }
    actionMessage?.let { message ->
        ActionSheet(if (message.text.isBlank()) context.getString(R.string.content_file_message) else context.getString(R.string.content_text_message),
            "${if (message.outgoing) context.getString(R.string.content_sent) else context.getString(R.string.content_received)} · ${DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss").withZone(ZoneId.systemDefault()).format(message.timestamp)}",
            buildList {
                if (message.text.isNotBlank()) add(ActionOption(context.getString(R.string.content_copy_text), R.drawable.figma_action_copy) {
                    val clipboard = context.getSystemService(ClipboardManager::class.java)
                    clipboard.setPrimaryClip(ClipData.newPlainText("BlueLink", message.text))
                })
                add(ActionOption(context.getString(R.string.content_delete_record), R.drawable.figma_action_delete, destructive = true) {
                    confirmAction = ConfirmAction(context.getString(R.string.content_delete_record), context.getString(R.string.content_delete_message_body), context.getString(R.string.content_delete)) {
                        model.deleteMessage(message.id)
                    }
                })
            }, dismiss = { actionMessage = null })
    }
    fun performTransferAction(transfer: TransferItem, action: TransferAction, messageId: java.util.UUID? = null) {
        val attachment = transfer.asAttachment()
        when (action) {
            TransferAction.OPEN -> if (attachment.isImage) previewAttachment = attachment else runCatching { FileInteraction.open(context, attachment) }
                .onFailure { Toast.makeText(context, it.message ?: context.getString(R.string.content_open_failed), Toast.LENGTH_SHORT).show() }
            TransferAction.COPY -> runCatching { FileInteraction.copyReference(context, attachment) }
                .onSuccess { Toast.makeText(context, context.getString(R.string.content_file_copied), Toast.LENGTH_SHORT).show() }
                .onFailure { Toast.makeText(context, it.message ?: context.getString(R.string.content_copy_failed), Toast.LENGTH_SHORT).show() }
            TransferAction.REVEAL -> interactionScope.launch {
                try { FileInteraction.reveal(context, attachment) }
                catch (canceled: kotlinx.coroutines.CancellationException) { throw canceled }
                catch (failure: Exception) { Toast.makeText(context, failure.message ?: context.getString(R.string.content_folder_unavailable), Toast.LENGTH_LONG).show() }
            }
            TransferAction.SAVE -> { pendingSaveAttachment = attachment; saveCopyPicker.launch(transfer.name) }
            TransferAction.DETAILS -> { infoTransfer = transfer }
            TransferAction.PAUSE -> model.pauseTransfer(transfer)
            TransferAction.RESUME -> model.resumeTransfer(transfer)
            TransferAction.RETRY -> model.retryTransfer(transfer)
            TransferAction.FAILURE -> { failureTransfer = transfer }
            TransferAction.CANCEL -> {
                val title = context.getString(if (transfer.outgoing) R.string.content_cancel_sending else R.string.content_cancel_receiving)
                confirmAction = ConfirmAction(title,
                    context.getString(if (transfer.outgoing) R.string.content_stop_sending else R.string.content_stop_receiving, transfer.name), title,
                    context.getString(R.string.content_keep_files)) { model.cancelTransfer(transfer) }
            }
            TransferAction.DELETE -> {
                if (messageId != null) confirmAction = ConfirmAction(context.getString(R.string.content_delete_local_message),
                    context.getString(R.string.content_delete_file_message_body), context.getString(R.string.content_delete)) { model.deleteMessage(messageId) }
                else confirmAction = ConfirmAction(context.getString(R.string.content_delete_transfer), context.getString(R.string.content_delete_transfer_body),
                    context.getString(R.string.content_delete)) { model.deleteTransfer(transfer.id) }
            }
        }
    }
    actionAttachment?.let { attachment ->
        val owner = messages.firstOrNull { item -> item.attachments.any { it.attachmentId == attachment.attachmentId } }
        val transfer = allTransfers[attachment.transferId] ?: TransferItem(attachment.transferId,
            attachment.fileName, attachment.sizeBytes, attachment.completedBytes, owner?.outgoing ?: false,
            runCatching { TransferStatus.valueOf(attachment.state) }.getOrDefault(TransferStatus.FAILED),
            messageId = owner?.id, attachmentId = attachment.attachmentId, mimeType = attachment.mimeType,
            localUri = attachment.localUri, peerId = selectedPeerId,
            startedAtEpochMs = owner?.timestamp?.toEpochMilli() ?: 0, updatedAtEpochMs = owner?.timestamp?.toEpochMilli() ?: 0)
        val online = allTransfers.containsKey(transfer.id) && conversations.any {
            it.peerId == transfer.peerId && it.availability == DeviceAvailability.CONNECTED }
        com.bluelink.android.ui.files.TransferActionSheet(transfer, online, { actionAttachment = null },
            messageContext = true, allowDelete = owner != null) { action -> performTransferAction(transfer, action, owner?.id) }
    }
    actionTransfer?.let { selectedTransfer ->
        val transfer = allTransfers[selectedTransfer.id] ?: selectedTransfer
        val online = conversations.any { it.peerId == transfer.peerId && it.availability == DeviceAvailability.CONNECTED }
        com.bluelink.android.ui.files.TransferActionSheet(transfer, online, { actionTransfer = null }) { action ->
            performTransferAction(transfer, action)
        }
    }

    infoConversation?.let { conversation ->
        DeviceDetailsPrompt(conversation) { infoConversation = null }
    }
    infoTransfer?.let { transfer ->
        FileDetailsPrompt(transfer.asAttachment(), transfer,
            conversations.firstOrNull { it.peerId == transfer.peerId }?.peerName, dismiss = { infoTransfer = null })
    }
    infoAttachment?.let { attachment ->
        val transfer = allTransfers[attachment.transferId]
        val message = messages.firstOrNull { m -> m.attachments.any { it.attachmentId == attachment.attachmentId } }
        FileDetailsPrompt(attachment, transfer,
            conversations.firstOrNull { it.peerId == (transfer?.peerId ?: selectedPeerId) }?.peerName,
            outgoing = transfer?.outgoing ?: message?.outgoing, timestamp = message?.timestamp ?: transfer?.let { Instant.ofEpochMilli(it.startedAtEpochMs) },
            dismiss = { infoAttachment = null })
    }
    failureTransfer?.let { TransferFailurePrompt(it, dismiss = { failureTransfer = null }) }
    confirmAction?.let { prompt ->
        BlueLinkConfirmation(prompt.title, prompt.message, prompt.note, prompt.confirmLabel,
            dismiss = { confirmAction = null }, confirm = { confirmAction = null; prompt.action() })
    }
}

private val activeTransferStatuses = setOf(TransferStatus.OFFERED, TransferStatus.QUEUED,
    TransferStatus.TRANSFERRING, TransferStatus.PAUSED, TransferStatus.REMOTE_PAUSED, TransferStatus.RESUMING,
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
    TransferStatus.REMOTE_PAUSED -> "对端已暂停"
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
    manageFiles: () -> Unit,
    forgetPeer: (String) -> Unit,
    forgetAllPeers: () -> Unit,
    performPrivacyAction: suspend (com.bluelink.android.domain.PrivacyAction) -> Unit,
    fingerprint: String,
    diagnostics: List<DiagnosticEntry>,
    bluetoothAccess: com.bluelink.android.domain.BluetoothAccessState,
    changeBluetooth: () -> Unit,
    checkLocalUpdate: () -> Unit,
) {
    if (selectedPage == SETTINGS_HOME) {
        SettingsHome(modifier, selectPage)
        return
    }
    if (selectedPage == SETTINGS_GENERAL) {
        GeneralSettings(modifier, settings, save)
        return
    }
    when (selectedPage) {
        0 -> { ConnectionSettings(modifier, settings, conversations, bluetoothAccess, changeBluetooth, save, selectPage); return }
        1 -> { FileStorageSettings(modifier, settings, save, chooseDirectory, manageFiles); return }
        2 -> { PrivacySettings(modifier, settings, save, fingerprint, diagnostics, performPrivacyAction); return }
        SETTINGS_TRUSTED -> { TrustedDevicesScreen(modifier, conversations, forgetPeer, forgetAllPeers); return }
        3 -> { AboutSettings(modifier, selectPage, checkLocalUpdate); return }
        SETTINGS_HELP -> { HelpFeedbackScreen(modifier, diagnostics, selectPage); return }
        SETTINGS_HELP_CONNECTION, SETTINGS_HELP_MESSAGES, SETTINGS_HELP_FAQ, SETTINGS_PRIVACY_POLICY, SETTINGS_AGREEMENT -> {
            SupportArticle(modifier, selectedPage) { selectPage(SETTINGS_HELP) }; return
        }
        SETTINGS_LICENSES -> { LicensesScreen(modifier); return }
    }
}

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
