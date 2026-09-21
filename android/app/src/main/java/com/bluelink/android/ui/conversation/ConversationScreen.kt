package com.bluelink.android.ui.conversation

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.background
import androidx.compose.foundation.interaction.collectIsDraggedAsState
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.listSaver
import java.util.UUID
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.ui.content.*
import com.bluelink.android.ui.devices.*
import com.bluelink.android.ui.files.FilesScreen
import kotlinx.coroutines.launch

@Composable
internal fun ConversationScreen(modifier: Modifier, peerId: String?, messages: List<ChatItem>, state: ConnectionState,
    conversations: List<ConversationSummary>, transfers: List<TransferItem>, showImageThumbnails: Boolean,
    receiveDirectory: String, back: () -> Unit, more: (ConversationSummary) -> Unit, openSettings: () -> Unit,
    send: (String) -> Unit, pickFile: () -> Unit, longPressMessage: (ChatItem) -> Unit,
    longPressAttachment: (ChatAttachment) -> Unit, openAttachment: (ChatAttachment) -> Unit,
    openTransfer: (TransferItem) -> Unit, moreTransfer: (TransferItem) -> Unit,
    previewImagesChanged: (List<ChatAttachment>) -> Unit = {},
    fileBatch: FileBatchOperations? = null,
    moreTransferWithSelection: ((TransferItem, () -> Unit) -> Unit)? = null,
    requestedTab: Int? = null, tabRequestHandled: () -> Unit = {},
    searchRequested: Boolean = false, searchRequestHandled: () -> Unit = {},
    messageListVisibilityChanged: (String?) -> Unit = {},
    searchTransferAction: ((TransferItem, TransferAction, UUID) -> Unit)? = null,
    deleteSearchMessage: ((ChatItem) -> Unit)? = null,
    deleteSelectedMessages: (suspend (List<UUID>) -> List<MessageBatchResult>)? = null,
    longPressMessageWithSelection: ((ChatItem, () -> Unit) -> Unit)? = null,
    longPressAttachmentWithSelection: ((ChatAttachment, () -> Unit) -> Unit)? = null,
    historyRevision: Long = 0,
    loadSearchHistory: (suspend () -> List<ChatItem>)? = null,
    loadHistoryContext: (suspend (ChatItem) -> Boolean)? = null,
    loadEarlierHistory: (suspend () -> Boolean)? = null,
    composer: com.bluelink.android.composer.ComposerController? = null,
    draftText: String = "", draftReady: Boolean = true,
    draftChanged: ((String) -> Unit)? = null, flushDraft: () -> Unit = {},
    usbEnabled: Boolean = false, usbSnapshot: com.bluelink.android.usb.UsbSnapshot = com.bluelink.android.usb.UsbSnapshot()) = DeviceScreenTheme {
    val context = LocalContext.current
    val focusManager = LocalFocusManager.current
    val selection = remember(peerId, focusManager) { MessageSelectionController { focusManager.clearFocus(force = true) } }
    var messageSelectionMode by rememberSaveable(peerId) { mutableStateOf(false) }
    var selectedMessageIds by rememberSaveable(peerId) { mutableStateOf(listOf<String>()) }
    var selectionAnchor by rememberSaveable(peerId) { mutableStateOf<String?>(null) }
    var messageBatchBusy by remember(peerId) { mutableStateOf(false) }
    val keyboard = androidx.compose.ui.platform.LocalSoftwareKeyboardController.current
    fun leaveSelection() { messageSelectionMode = false; selectedMessageIds = emptyList(); selectionAnchor = null }
    fun selectMessage(id: UUID) {
        if (messageBatchBusy) return
        val value = id.toString()
        if (value in selectedMessageIds) {
            selectedMessageIds = selectedMessageIds - value
            if (selectionAnchor == value) selectionAnchor = selectedMessageIds.lastOrNull()
        } else { selectedMessageIds = selectedMessageIds + value; selectionAnchor = value }
    }
    fun beginSelection(item: ChatItem) {
        focusManager.clearFocus(force = true); keyboard?.hide()
        selectedMessageIds = listOf(item.id.toString()); selectionAnchor = item.id.toString(); messageSelectionMode = true
    }
    LaunchedEffect(messages.map { it.id }) {
        val available = messages.map { it.id.toString() }.toSet()
        selectedMessageIds = selectedMessageIds.filter { it in available }
        if (selectionAnchor !in available) selectionAnchor = selectedMessageIds.lastOrNull()
    }
    val peer = conversations.firstOrNull { it.peerId == peerId }
    val peerName = peer?.displayName ?: context.getString(R.string.content_conversation)
    val connected = peer?.availability == DeviceAvailability.CONNECTED && state.phase == ConnectionPhase.CONNECTED
    var selectedTab by rememberSaveable(peerId) { mutableIntStateOf(0) }
    var search by rememberSaveable(peerId) { mutableStateOf(false) }
    var loadingEarlier by remember(peerId) { mutableStateOf(false) }
    var hasEarlier by remember(peerId) { mutableStateOf(true) }
    // A direct file-task route must never briefly expose/mark the messages tab as read.
    val displayedTab = (requestedTab ?: selectedTab).coerceIn(0, 1)
    val messageListVisible = !search && !searchRequested && displayedTab == 0
    LaunchedEffect(messageListVisible) { if (!messageListVisible) leaveSelection() }
    val visibilityChanged by rememberUpdatedState(messageListVisibilityChanged)
    DisposableEffect(peerId, messageListVisible) {
        visibilityChanged(peerId.takeIf { messageListVisible })
        onDispose { visibilityChanged(null) }
    }
    LaunchedEffect(peerId, requestedTab) {
        requestedTab?.let { selectedTab = it.coerceIn(0, 1); search = false; tabRequestHandled() }
    }
    LaunchedEffect(peerId, searchRequested) {
        if (searchRequested) { search = true; searchRequestHandled() }
    }
    var localDraft by rememberSaveable(peerId) { mutableStateOf("") }
    val draft = if (draftChanged != null) draftText else localDraft
    val flushCurrentDraft by rememberUpdatedState(flushDraft)
    DisposableEffect(peerId) { onDispose { flushCurrentDraft() } }
    var locateId by remember(peerId) { mutableStateOf<String?>(null) }
    var historyScroll by rememberSaveable(peerId, stateSaver = ConversationScrollSaver) { mutableStateOf(ConversationScrollState()) }
    var followingBottom by rememberSaveable(peerId) { mutableStateOf(true) }
    val listState = rememberLazyListState()
    val dragging by listState.interactionSource.collectIsDraggedAsState()
    val scope = rememberCoroutineScope()
    val historyVisible by rememberUpdatedState(messageListVisible)
    LaunchedEffect(peerId, listState) {
        // Only a user's scroll changes the follow intent. Image decoding and file-card
        // replacement also change canScrollForward, but must not detach a pinned viewport.
        snapshotFlow { Triple(dragging, !listState.canScrollForward, listState.layoutInfo.totalItemsCount) }.collect { (userDragging, atBottom, count) ->
            if (historyVisible && count > 0 && count == historyScroll.observedCount) {
                if (userDragging || atBottom) followingBottom = atBottom
                if (atBottom) historyScroll = historyScroll.readToBottom()
            }
        }
    }
    LaunchedEffect(peerId, listState, messageListVisible) {
        snapshotFlow {
            val layout = listState.layoutInfo
            Triple(followingBottom, layout.viewportEndOffset,
                layout.visibleItemsInfo.map { Triple(it.index, it.offset, it.size) })
        }.collect {
            if (historyVisible && followingBottom && !listState.isScrollInProgress &&
                listState.layoutInfo.totalItemsCount > 0 && listState.canScrollForward) {
                listState.scrollToConversationEnd()
            }
        }
    }
    LaunchedEffect(peerId, messages.lastOrNull()?.id, messages.size, displayedTab, search, locateId) {
        if (!messageListVisible) return@LaunchedEffect
        val target = locateId?.let { id -> messages.indexOfFirst { it.id.toString() == id } }?.takeIf { it >= 0 }
        val update = historyScroll.observe(messages, followingBottom, locating = target != null)
        historyScroll = update.state
        if (target != null) {
            followingBottom = false
            listState.scrollToItem(target)
            locateId = null
        } else if (update.followLatest) {
            listState.scrollToConversationEnd()
            followingBottom = true
        }
    }
    if (!search && !searchRequested && displayedTab == 0) SideEffect { previewImagesChanged(messages.flatMap { it.attachments }) }
    if (search || searchRequested) MessageSearchScreen(messages, peerName, showImageThumbnails, close = { search = false },
        loadHistory = loadSearchHistory, historyRevision = historyRevision,
        locate = { item -> scope.launch {
            try {
                if (loadHistoryContext?.invoke(item) != false) { locateId = item.id.toString(); selectedTab = 0; search = false }
            } catch (error: Exception) { android.widget.Toast.makeText(context, context.getString(R.string.content_history_load_failed), android.widget.Toast.LENGTH_SHORT).show() }
        } }, modifier = modifier,
        previewImagesChanged = previewImagesChanged,
        transfers = transfers, online = connected, onTransferAction = searchTransferAction, deleteMessage = deleteSearchMessage, deleteSelectedMessages = deleteSelectedMessages)
    else Column(modifier.fillMaxSize().background(DeviceColors.Canvas).messageSelectionSurface(selection)) {
        Row(Modifier.fillMaxWidth().heightIn(min = 64.dp).background(DeviceColors.Surface).padding(horizontal = 4.dp),
            verticalAlignment = Alignment.CenterVertically) {
            ContentIconButton(R.drawable.figma_content_back, context.getString(R.string.content_back_devices), action = { if (messageSelectionMode) { if (!messageBatchBusy) leaveSelection() } else back() })
            FigmaIcon(when (peer?.platform) { PeerPlatform.WINDOWS -> R.drawable.figma_desktop
                PeerPlatform.ANDROID -> R.drawable.figma_phone; else -> R.drawable.figma_generic }, size = 24.dp)
            Column(Modifier.weight(1f).padding(horizontal = 10.dp)) {
                DeviceNameWithUsb(peerName, connected && peer?.usbReady == true, fontSize = 16.sp)
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(6.dp).background(if (connected) DeviceColors.Success else DeviceColors.Secondary, CircleShape))
                    Spacer(Modifier.width(6.dp))
                    Text(if (connected) context.getString(R.string.content_connected_protocol) else context.getString(R.string.content_offline_history), color = DeviceColors.Secondary, fontSize = 10.sp, lineHeight = 16.sp)
                }
            }
            ContentIconButton(R.drawable.figma_content_more, context.getString(R.string.content_conversation_actions), enabled = peer != null && !messageSelectionMode) { peer?.let(more) }
        }
        if (messageSelectionMode) {
            Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).background(DeviceColors.Surface).padding(horizontal = 16.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Text(context.getString(R.string.batch_selected, selectedMessageIds.size), Modifier.weight(1f),
                    color = DeviceColors.Ink, fontSize = 14.sp)
                TextButton(enabled = !messageBatchBusy, onClick = { leaveSelection() }) { Text(context.getString(R.string.cancel)) }
            }
            HorizontalDivider(color = DeviceColors.Border)
        } else ContentTabs(listOf(context.getString(R.string.content_messages), context.getString(R.string.content_files)), displayedTab, { selectedTab = it })
        com.bluelink.android.usb.UsbStatePolicy.noticeStage(usbEnabled, peerId, peer?.usbReady == true, usbSnapshot)?.let { stage ->
            val text = when (stage) {
                com.bluelink.android.usb.UsbStage.AUTHORIZATION -> R.string.usb_notice_authorization
                com.bluelink.android.usb.UsbStage.NEGOTIATING -> R.string.usb_notice_verifying
                com.bluelink.android.usb.UsbStage.FALLBACK, com.bluelink.android.usb.UsbStage.UNAVAILABLE ->
                    if (connected && peer?.transport == SessionTransport.BLUETOOTH) R.string.usb_notice_fallback else R.string.usb_notice_offline
                else -> R.string.usb_notice_unavailable
            }
            Surface(Modifier.fillMaxWidth().padding(16.dp, 8.dp), color = DeviceColors.Selected, shape = RoundedCornerShape(8.dp)) {
                Text(androidx.compose.ui.res.stringResource(text), Modifier.padding(12.dp, 10.dp), fontSize = 12.sp,
                    lineHeight = 18.sp, color = DeviceColors.Secondary)
            }
        }
        if (displayedTab == 1) FilesScreen(Modifier.weight(1f), transfers, conversations, peerId,
            receiveDirectory, openSettings, openTransfer, moreTransfer, fileBatch, moreTransferWithSelection,
            previewImagesChanged = previewImagesChanged)
        else {
            Box(Modifier.weight(1f).fillMaxWidth()) {
                if (messages.isEmpty()) Text(context.getString(R.string.content_no_messages), Modifier.align(Alignment.Center), color = DeviceColors.Secondary)
                Column(Modifier.fillMaxSize()) {
                if(loadEarlierHistory != null && messages.size >= 200 && hasEarlier) TextButton(onClick = {
                    if(!loadingEarlier) scope.launch {
                        loadingEarlier = true; followingBottom = false
                        try { hasEarlier = loadEarlierHistory() }
                        catch(error: Exception) { android.widget.Toast.makeText(context, context.getString(R.string.content_history_load_failed), android.widget.Toast.LENGTH_SHORT).show() }
                        finally { loadingEarlier = false }
                    }
                }, enabled = !loadingEarlier, modifier = Modifier.align(Alignment.CenterHorizontally)) { Text(context.getString(R.string.content_load_earlier)) }
                LazyColumn(Modifier.weight(1f).fillMaxWidth(), state = listState, contentPadding = PaddingValues(top = if (messageSelectionMode) 48.dp else 12.dp, bottom = if (messageSelectionMode) 56.dp else 12.dp),
                    verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    itemsIndexed(messages, key = { _, it -> it.id.toString() }) { index, item ->
                        Column {
                            if (HistoryQuery.showTimestamp(messages.getOrNull(index - 1)?.timestamp, item.timestamp)) {
                                Text("${if (index == 0 || contentDay(messages[index - 1].timestamp, context) != contentDay(item.timestamp, context)) contentDay(item.timestamp, context) + " " else ""}${contentTime(item.timestamp)}",
                                    Modifier.align(Alignment.CenterHorizontally).padding(top = 4.dp, bottom = 14.dp), fontSize = 11.sp, color = DeviceColors.Secondary)
                            }
                            ConversationMessage(item, showImageThumbnails, openAttachment,
                                { message -> if (longPressMessageWithSelection != null) longPressMessageWithSelection(message) { beginSelection(message) } else longPressMessage(message) },
                                { attachment -> if (longPressAttachmentWithSelection != null) longPressAttachmentWithSelection(attachment) { beginSelection(item) } else longPressAttachment(attachment) },
                                selection, messageSelectionMode, item.id.toString() in selectedMessageIds, { selectMessage(item.id) })
                        }
                    }
                }
                }
                if (messageSelectionMode) MessageRangeSelectionButton(listState, messages, selectedMessageIds, selectionAnchor, !messageBatchBusy) { target ->
                    selectedMessageIds = MessageBatch.selectRange(messages, selectedMessageIds, selectionAnchor, target)
                }
                val newCount = historyScroll.pendingIds.size
                if (newCount > 0 && !messageSelectionMode) Button(onClick = {
                    scope.launch {
                        if (messages.isNotEmpty()) listState.scrollToConversationEnd()
                        followingBottom = !listState.canScrollForward
                        if (followingBottom) historyScroll = historyScroll.readToBottom()
                    }
                }, modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 16.dp).height(36.dp).widthIn(min = 136.dp),
                    shape = CircleShape, contentPadding = PaddingValues(horizontal = 16.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = DeviceColors.Blue, contentColor = MaterialTheme.colorScheme.onPrimary),
                    elevation = ButtonDefaults.buttonElevation(defaultElevation = 4.dp)) {
                    Text("↓  " + context.resources.getQuantityString(R.plurals.content_new_messages, newCount, newCount),
                        fontSize = 13.sp, fontWeight = androidx.compose.ui.text.font.FontWeight.Normal)
                }
            }
            HorizontalDivider(color = DeviceColors.Border)
            if (messageSelectionMode && deleteSelectedMessages != null) MessageBatchToolbar(messages, selectedMessageIds,
                messageBatchBusy, { messageBatchBusy = it }, { selectedMessageIds = it }, { leaveSelection() }, deleteSelectedMessages)
            else if (composer != null) RichComposer(composer, peerId, draft, draftReady, connected,
                { if (draftChanged != null) draftChanged(it) else localDraft = it }, { flushCurrentDraft() }, pickFile)
            else {
            val composerHeight = with(LocalDensity.current) { 20.sp.toDp() } + 20.dp
            Row(Modifier.fillMaxWidth().background(DeviceColors.Surface).imePadding().padding(8.dp),
                verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ComposerIconButton(R.drawable.figma_content_attachment, context.getString(R.string.content_choose_file),
                    composerHeight, connected, DeviceColors.Selected,
                    if (connected) DeviceColors.Ink else DeviceColors.Secondary, pickFile)
                BasicTextField(draft, { if (draftChanged != null) draftChanged(it) else localDraft = it }, enabled = draftReady && peerId != null, maxLines = 4,
                    textStyle = TextStyle(fontFamily = DeviceFont, fontSize = 13.sp, lineHeight = 20.sp, color = DeviceColors.Ink),
                    cursorBrush = SolidColor(DeviceColors.Blue),
                    modifier = Modifier.weight(1f).onFocusChanged { if (!it.isFocused) flushCurrentDraft() }.heightIn(min = composerHeight).border(1.dp, DeviceColors.Border, RoundedCornerShape(12.dp))
                        .padding(horizontal = 12.dp, vertical = 10.dp).semantics { contentDescription = context.getString(R.string.content_type_message) },
                    decorationBox = { input -> Box { if (draft.isEmpty()) Text(if (connected) context.getString(R.string.content_type_message) else context.getString(R.string.content_offline_composer),
                        fontSize = 12.sp, lineHeight = 20.sp, color = DeviceColors.Secondary); input() } })
                ComposerIconButton(R.drawable.figma_content_send, context.getString(R.string.content_send_message),
                    composerHeight, connected && draft.isNotBlank(),
                    if (connected && draft.isNotBlank()) DeviceColors.Blue else DeviceColors.Secondary,
                    MaterialTheme.colorScheme.onPrimary) { send(draft); if (draftChanged == null) localDraft = "" }
            }
            }
        }
    }
}

private suspend fun LazyListState.scrollToConversationEnd() {
    val lastIndex = layoutInfo.totalItemsCount - 1
    if (lastIndex < 0) return
    scrollToItem(lastIndex)
    val layout = layoutInfo
    val tail = layout.visibleItemsInfo.lastOrNull() ?: return
    // Includes tall multi-attachment rows and the list's bottom content padding.
    val remaining = tail.offset + tail.size + layout.afterContentPadding - layout.viewportEndOffset
    if (remaining > 0) scrollBy(remaining.toFloat())
}

private val ConversationScrollSaver = listSaver<ConversationScrollState, String>(
    save = { listOf(it.lastObservedId?.toString().orEmpty(), it.observedCount.toString()) + it.pendingIds.map(UUID::toString) },
    restore = { ConversationScrollState(it[0].takeIf(String::isNotEmpty)?.let(UUID::fromString), it[1].toInt(),
        it.drop(2).map(UUID::fromString).toSet()) }
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun ComposerIconButton(asset: Int, description: String, side: Dp, enabled: Boolean,
                               background: Color, foreground: Color, action: () -> Unit) {
    // Keep the visible button equal to the single-line field. Compose still expands hit testing
    // to its minimum touch target; the composer's padding/gaps leave room around the smaller square.
    CompositionLocalProvider(LocalMinimumInteractiveComponentEnforcement provides false) {
        IconButton(onClick = action, enabled = enabled,
            modifier = Modifier.size(side).clip(RoundedCornerShape(12.dp)).background(background)
                .semantics { contentDescription = description }) {
            FigmaIcon(asset, size = 20.dp, tint = foreground)
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ConversationMessage(item: ChatItem, thumbnails: Boolean, open: (ChatAttachment) -> Unit,
                                more: (ChatItem) -> Unit, moreAttachment: (ChatAttachment) -> Unit, selection: MessageSelectionController,
                                selecting: Boolean = false, selected: Boolean = false, toggle: () -> Unit = {}) {
    val context = LocalContext.current
    val haptics = LocalHapticFeedback.current
    // Match the Windows bubbles: the lower corner on the sender's side is tighter.
    val bubbleShape = remember(item.outgoing) {
        RoundedCornerShape(topStart = 14.dp, topEnd = 14.dp,
            bottomEnd = if (item.outgoing) 4.dp else 14.dp,
            bottomStart = if (item.outgoing) 14.dp else 4.dp)
    }
    Row(Modifier.fillMaxWidth().then(if (selecting) Modifier.toggleable(selected, role = androidx.compose.ui.semantics.Role.Checkbox,
        onValueChange = { toggle() }) else Modifier).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
    if (selecting) Checkbox(selected, onCheckedChange = null, modifier = Modifier.padding(end = 8.dp))
    Column(Modifier.weight(1f), horizontalAlignment = if (item.outgoing) Alignment.End else Alignment.Start,
        verticalArrangement = Arrangement.spacedBy(6.dp)) {
        if (item.text.isNotBlank()) {
            val textStyle = LocalTextStyle.current.merge(TextStyle(color = if (item.outgoing) MaterialTheme.colorScheme.onPrimary else DeviceColors.Ink,
                fontSize = 13.sp, lineHeight = 22.sp))
            val textModifier = Modifier.widthIn(min = 56.dp, max = 320.dp)
                .then(if (selecting) Modifier else Modifier.width(IntrinsicSize.Min))
                .background(if (item.outgoing) DeviceColors.Blue else DeviceColors.Surface, bubbleShape)
                .border(1.dp, if (item.outgoing) DeviceColors.Blue else DeviceColors.Border, bubbleShape)
                .padding(horizontal = 14.dp, vertical = 9.dp)
            if (selecting) Text(item.text, modifier = textModifier, style = textStyle)
            else SelectableMessageText(selection, item.outgoing, more = { more(item) }, text = item.text, textStyle = textStyle, modifier = textModifier)
        }
        item.attachments.forEach { attachment ->
            val failed = !attachment.recoveryPending && (attachment.state == "FAILED" || attachment.state == "REJECTED")
            val transferCard = attachment.isTransferActive || failed
            val progressColor = when { failed -> DeviceColors.Error; attachment.state == "PAUSED" -> DeviceColors.Warning; else -> DeviceColors.Blue }
            // In selection mode the full row owns gestures and its ripple, including card/image taps.
            val action = if (selecting) Modifier else Modifier.combinedClickable(
                onClick = { if (attachment.canOpen) open(attachment) else moreAttachment(attachment) },
                onLongClick = { haptics.performHapticFeedback(HapticFeedbackType.LongPress); moreAttachment(attachment) })
            if (attachment.showsThumbnail(thumbnails)) Column(horizontalAlignment = Alignment.Start, verticalArrangement = Arrangement.spacedBy(4.dp)) {
                ChatAttachmentThumbnail(attachment, action) {
                    if (attachment.isTransferActive) Box(Modifier.align(Alignment.Center).size(38.dp)
                        .background(DeviceColors.Surface.copy(alpha = .9f), RoundedCornerShape(19.dp)), contentAlignment = Alignment.Center) {
                        if (attachment.state in setOf("PAUSED", "REMOTE_PAUSED"))
                            FigmaIcon(R.drawable.figma_action_pause, size = 24.dp, tint = progressColor)
                        else if (attachment.isProgressIndeterminate) CircularProgressIndicator(modifier = Modifier.size(28.dp),
                            color = DeviceColors.Blue, trackColor = DeviceColors.Border, strokeWidth = 3.dp)
                        else CircularProgressIndicator(progress = { attachment.progress }, modifier = Modifier.size(28.dp),
                            color = DeviceColors.Blue, trackColor = DeviceColors.Border, strokeWidth = 3.dp)
                    }
                }
                if (attachment.isTransferActive || failed || attachment.recoveryPending) Text(attachment.contentStatus(context, item.outgoing),
                    color = DeviceColors.Secondary, fontSize = 11.sp, lineHeight = 18.sp)
            }
            else Row(Modifier.widthIn(max = if (transferCard) 286.dp else 290.dp).fillMaxWidth()
                .heightIn(min = if (transferCard) 76.dp else 72.dp)
                .background(if (item.outgoing) DeviceColors.Selected else DeviceColors.Surface, bubbleShape)
                .border(1.dp, when { failed -> DeviceColors.Error; !transferCard && item.outgoing -> DeviceColors.Blue; else -> DeviceColors.Border }, bubbleShape).then(action)
                .padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                FileTypeIcon(attachment.fileName, mimeType = attachment.mimeType, size = if (transferCard) 46.dp else 40.dp)
                Column(Modifier.weight(1f).padding(start = 12.dp)) {
                    Text(attachment.fileName, fontSize = if (transferCard) 14.sp else 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(if (attachment.state == "REMOTE_PAUSED") attachment.contentStatus(context, item.outgoing)
                        else if (transferCard) "${attachment.contentStatus(context, item.outgoing)} · ${contentBytes(attachment.sizeBytes)}"
                        else "${contentBytes(attachment.sizeBytes)} · ${attachment.contentStatus(context, item.outgoing)}",
                        color = if (failed || attachment.state == "PAUSED") progressColor else DeviceColors.Secondary,
                        fontSize = 11.sp, lineHeight = 18.sp)
                }
                if (attachment.isTransferActive || failed) Box(Modifier.padding(start = 8.dp).size(34.dp), contentAlignment = Alignment.Center) {
                    if (attachment.state == "REMOTE_PAUSED") FigmaIcon(R.drawable.figma_action_pause, size = 24.dp, tint = progressColor)
                    else if (attachment.isProgressIndeterminate) CircularProgressIndicator(modifier = Modifier.fillMaxSize(),
                        color = progressColor, trackColor = DeviceColors.Border, strokeWidth = 3.dp)
                    else {
                        CircularProgressIndicator(progress = { attachment.progress }, modifier = Modifier.fillMaxSize(),
                            color = progressColor, trackColor = DeviceColors.Border, strokeWidth = 3.dp)
                        Text("${(attachment.progress * 100).toInt()}%", color = progressColor, fontSize = 8.sp)
                    }
                }
            }
        }
        if (item.outgoing && item.kind == ChatItemKind.TEXT) Text(
            when (item.status) { MessageStatus.LOCAL_QUEUED -> context.getString(R.string.content_waiting_send); MessageStatus.SENDING -> context.getString(R.string.content_sending); MessageStatus.FAILED -> context.getString(R.string.content_send_failed); MessageStatus.DELIVERED -> context.getString(R.string.content_delivered); MessageStatus.READ -> context.getString(R.string.content_read); else -> context.getString(R.string.content_sent) },
            color = if (item.status == MessageStatus.FAILED) DeviceColors.Error else if (item.status == MessageStatus.READ) DeviceColors.Blue else DeviceColors.Secondary, fontSize = 11.sp, lineHeight = 16.sp)
    }
    }
}
