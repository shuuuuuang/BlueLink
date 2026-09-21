package com.bluelink.android.ui.conversation

import androidx.activity.compose.BackHandler
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.ui.content.*
import com.bluelink.android.ui.devices.*
import java.time.LocalDate
import java.time.YearMonth
import java.time.ZoneId

@OptIn(ExperimentalFoundationApi::class)
@Composable
internal fun MessageSearchScreen(messages: List<ChatItem>, peerName: String, showThumbnails: Boolean,
                                 close: () -> Unit, locate: (ChatItem) -> Unit, modifier: Modifier = Modifier,
                                 transfers: List<TransferItem> = emptyList(), online: Boolean = false,
                                 onTransferAction: ((TransferItem, TransferAction, java.util.UUID) -> Unit)? = null,
                                 previewImagesChanged: (List<ChatAttachment>) -> Unit = {},
                                 deleteMessage: ((ChatItem) -> Unit)? = null,
                                 loadHistory: (suspend () -> List<ChatItem>)? = null, historyRevision: Long = 0,
                                 deleteSelectedMessages: (suspend (List<java.util.UUID>) -> List<MessageBatchResult>)? = null) {
    val context = LocalContext.current
    var sourceFailed by remember(peerName) { mutableStateOf(false) }
    var reload by remember(peerName) { mutableIntStateOf(0) }
    val source by produceState<List<ChatItem>?>(null, historyRevision, peerName, reload) {
        value = null; sourceFailed = false
        try { value = loadHistory?.invoke() ?: messages }
        catch(cancelled: CancellationException) { throw cancelled }
        catch(error: Exception) { sourceFailed = true; value = emptyList() }
    }
    var selectionMode by rememberSaveable(peerName) { mutableStateOf(false) }
    var selectedIds by rememberSaveable(peerName) { mutableStateOf(listOf<String>()) }
    var selectionAnchor by rememberSaveable(peerName) { mutableStateOf<String?>(null) }
    var deletedIds by rememberSaveable(peerName) { mutableStateOf(listOf<String>()) }
    var batchBusy by remember { mutableStateOf(false) }
    val listState = rememberLazyListState()
    val keyboard = androidx.compose.ui.platform.LocalSoftwareKeyboardController.current
    fun leaveSelection() { selectionMode = false; selectedIds = emptyList(); selectionAnchor = null }
    fun toggle(id: String) {
        if (batchBusy) return
        if (id in selectedIds) {
            selectedIds = selectedIds - id
            if (selectionAnchor == id) selectionAnchor = selectedIds.lastOrNull()
        } else { selectedIds = selectedIds + id; selectionAnchor = id }
    }
    val mergedMessages by produceState<List<ChatItem>?>(null, source, messages, deletedIds) {
        // Keep the previous snapshot while refreshing. A transient null followed by an equal
        // list can be conflated by Compose, leaving the downstream search stuck loading.
        value = withContext(Dispatchers.Default) { SearchResultActions.merge(source.orEmpty(), messages, deletedIds) }
    }
    LaunchedEffect(mergedMessages, source) {
        if (source != null && mergedMessages != null && !sourceFailed) {
            val available = mergedMessages.orEmpty().map { it.id.toString() }.toSet()
            selectedIds = selectedIds.filter { it in available }
            if (selectionAnchor !in selectedIds) selectionAnchor = selectedIds.lastOrNull()
        }
    }
    val focusManager = LocalFocusManager.current
    var actionMessageId by rememberSaveable { mutableStateOf<String?>(null) }
    fun actions(message: ChatItem) { focusManager.clearFocus(); actionMessageId = message.id.toString() }
    var query by rememberSaveable { mutableStateOf("") }
    var kind by rememberSaveable { mutableStateOf(HistoryKind.ALL) }
    var dateValue by rememberSaveable { mutableStateOf<String?>(null) }
    var showCalendar by rememberSaveable { mutableStateOf(false) }
    var endDateValue by rememberSaveable { mutableStateOf<String?>(null) }
    var editingEnd by rememberSaveable { mutableStateOf(false) }
    var pageSize by remember(messages, query, kind, dateValue, endDateValue) { mutableIntStateOf(100) }
    val date = dateValue?.let(LocalDate::parse)
    val endDate = endDateValue?.let(LocalDate::parse)
    val criteria = listOf(query, kind, date, endDate)
    val settledCriteria by produceState(criteria, criteria) { delay(125); value = criteria }
    val results by produceState<List<ChatItem>?>(null, source, mergedMessages, criteria, settledCriteria) {
        value = null
        if(source == null || criteria != settledCriteria) return@produceState
        val currentMessages = mergedMessages ?: return@produceState
        value = withContext(Dispatchers.Default) {
            HistoryQuery.messages(currentMessages, query, kind, date, endDate = endDate)
        }
    }
    SideEffect { results?.let { previewImagesChanged(it.flatMap { message -> message.attachments }) } }
    val visibleResults = results.orEmpty().take(pageSize)
    BackHandler { if (!batchBusy) { if (selectionMode) leaveSelection() else close() } }
    Column(modifier.fillMaxSize().background(DeviceColors.Canvas)) {
        Box(Modifier.fillMaxWidth().heightIn(min = 58.dp).background(DeviceColors.Surface)) {
            Text(context.getString(R.string.content_history_with, peerName), Modifier.align(Alignment.Center).padding(horizontal = 56.dp),
                fontSize = 18.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            ContentIconButton(R.drawable.figma_content_close, context.getString(R.string.content_close_search), Modifier.align(Alignment.CenterStart).padding(start = 4.dp), enabled = !batchBusy, action = { if (selectionMode) leaveSelection() else close() })
        }
        HorizontalDivider(color = DeviceColors.Border)
        if (selectionMode) {
            Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).background(DeviceColors.Surface).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(context.getString(R.string.batch_selected, selectedIds.size), Modifier.weight(1f), color = DeviceColors.Ink, fontSize = 14.sp)
                TextButton(enabled = !batchBusy, onClick = { leaveSelection() }) { Text(context.getString(R.string.cancel)) }
            }
            HorizontalDivider(color = DeviceColors.Border)
        } else {
        ContentSearch(query, { query = it }, context.getString(R.string.content_search_messages), Modifier.padding(horizontal = 16.dp, vertical = 12.dp), 48.dp)
        ContentTabs(HistoryKind.entries.filter { it != HistoryKind.DATE }.map { it.contentLabel(context) }, kind.ordinal.coerceAtMost(3)) {
            kind = HistoryKind.entries[it]
        }
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = { editingEnd = false; showCalendar = true }) {
                Text(date?.toString() ?: context.getString(R.string.content_date_start), fontSize = 12.sp)
            }
            TextButton(onClick = { editingEnd = true; showCalendar = true }) {
                Text(endDate?.toString() ?: context.getString(R.string.content_date_end), fontSize = 12.sp)
            }
            if (date != null || endDate != null || kind != HistoryKind.ALL || query.isNotEmpty())
                TextButton(onClick = { dateValue = null; endDateValue = null; kind = HistoryKind.ALL; query = "" }) {
                    Text(context.getString(R.string.content_clear_filters), fontSize = 12.sp)
                }
            Spacer(Modifier.weight(1f))
            Text(if (results == null) context.getString(R.string.content_searching) else context.resources.getQuantityString(R.plurals.content_result_count, results.orEmpty().size, results.orEmpty().size), color = DeviceColors.Secondary, fontSize = 12.sp)
        }
        }
        if (sourceFailed) TextButton(onClick = { reload++ }, modifier = Modifier.fillMaxWidth()) { Text(context.getString(R.string.content_history_load_failed)) }
        if (results == null) Box(Modifier.fillMaxWidth().weight(1f), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        else if (results.orEmpty().isEmpty()) Box(Modifier.fillMaxWidth().weight(1f).padding(horizontal = 16.dp, vertical = 4.dp)) {
            ContentSearchEmpty(context.getString(R.string.content_no_message_results), context.getString(R.string.content_message_search_hint))
        } else Box(Modifier.weight(1f).fillMaxWidth()) {
        LazyColumn(Modifier.fillMaxSize(), state = listState, contentPadding = PaddingValues(bottom = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)) {
            visibleResults.groupBy { contentDay(it.timestamp, context) }.forEach { (day, items) ->
                item(key = "day-$day") { Text(day, Modifier.padding(horizontal = 16.dp), color = DeviceColors.Secondary, fontSize = 12.sp) }
                items(items, key = { it.id.toString() }) { item ->
                    val attachment = SearchResultActions.attachment(item, query)
                    val showFilename = SearchResultActions.showsFilename(item, attachment, query)
                    val selected = item.id.toString() in selectedIds
                    val rowInteraction = if (selectionMode) Modifier.toggleable(selected, enabled = !batchBusy, role = Role.Checkbox) { toggle(item.id.toString()) } else Modifier
                    Row(Modifier.fillMaxWidth().then(rowInteraction).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                    if (selectionMode) {
                        Checkbox(checked = selected, onCheckedChange = null, enabled = !batchBusy)
                        Spacer(Modifier.width(8.dp))
                    }
                    val cardInteraction = if (selectionMode) Modifier else Modifier.combinedClickable(role = Role.Button,
                        onClickLabel = context.getString(R.string.content_result_actions), onLongClickLabel = context.getString(R.string.content_result_actions),
                        onClick = { actions(item) }, onLongClick = { actions(item) })
                    Row(Modifier.weight(1f).heightIn(min = if (attachment?.isImage == true && showThumbnails) 108.dp else 88.dp)
                        .background(DeviceColors.Surface, RoundedCornerShape(12.dp)).border(1.dp, DeviceColors.Border, RoundedCornerShape(12.dp))
                        .then(cardInteraction).padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(44.dp).background(if (attachment == null) DeviceColors.Selected else DeviceColors.Surface, RoundedCornerShape(12.dp)),
                            contentAlignment = Alignment.Center) {
                            if (attachment == null) FigmaIcon(R.drawable.figma_content_message, size = 22.dp)
                            else FileTypeIcon(attachment.fileName, mimeType = attachment.mimeType, size = 24.dp)
                        }
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text("${if (item.outgoing) context.getString(R.string.content_this_device) else peerName} · $day ${contentTime(item.timestamp)}", fontSize = 11.sp,
                                lineHeight = 16.sp, color = DeviceColors.Secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            SearchPreviewText(if (showFilename) requireNotNull(attachment).fileName else item.text, query, fileName = showFilename)
                            Text(attachment?.let { "${contentBytes(it.sizeBytes)} · ${it.contentStatus(context)}" } ?: context.getString(R.string.content_text_message),
                                fontSize = 11.sp, lineHeight = 16.sp, color = DeviceColors.Secondary)
                        }
                        if (attachment?.showsThumbnail(showThumbnails) == true) {
                            Spacer(Modifier.width(8.dp)); AttachmentThumbnail(attachment, Modifier.size(72.dp, 58.dp))
                        }
                        if (!selectionMode) ContentIconButton(R.drawable.figma_preview_more, context.getString(R.string.content_result_actions),
                            Modifier.padding(start = 4.dp).size(40.dp)) { actions(item) }
                    }
                    }
                }
            }
            if (pageSize < results.orEmpty().size) item(key = "more-results") {
                TextButton(onClick = { pageSize += 100 }, modifier = Modifier.fillMaxWidth()) { Text(context.getString(R.string.content_load_more)) }
            }
        }
        if (selectionMode) MessageRangeSelectionButton(listState, visibleResults, selectedIds, selectionAnchor, !batchBusy) { target ->
            selectedIds = MessageBatch.selectRange(visibleResults, selectedIds, selectionAnchor, target)
        }
        }
        if (selectionMode && deleteSelectedMessages != null) MessageBatchToolbar(mergedMessages.orEmpty(), selectedIds, batchBusy || source == null || mergedMessages == null,
            setBusy = { batchBusy = it }, select = { selectedIds = it }, leave = { leaveSelection() }, delete = { ids ->
                val outcome = deleteSelectedMessages(ids)
                deletedIds = (deletedIds + outcome.filter { it.deleted }.map { it.id.toString() }).distinct()
                outcome
            })
    }
    (messages.firstOrNull { it.id.toString() == actionMessageId } ?: source.orEmpty().firstOrNull { it.id.toString() == actionMessageId })?.let { message ->
        SearchResultActionSheet(message, SearchResultActions.attachment(message, query), transfers, online,
            dismiss = { actionMessageId = null }, locate = { locate(message) },
            onTransferAction = onTransferAction, deleteMessage = deleteMessage,
            selectMultiple = if (deleteSelectedMessages == null) null else ({
                focusManager.clearFocus(force = true); keyboard?.hide()
                selectedIds = listOf(message.id.toString()); selectionAnchor = message.id.toString(); selectionMode = true
            }))
    }
    if (showCalendar) HistoryDateSheet(date ?: messages.lastOrNull()?.timestamp?.atZone(ZoneId.systemDefault())?.toLocalDate() ?: LocalDate.now(),
        source.orEmpty().map { it.timestamp.atZone(ZoneId.systemDefault()).toLocalDate() }.toSet(),
        dismiss = { showCalendar = false }, confirm = {
            if (editingEnd) { endDateValue = it.toString(); if (date != null && it < date) dateValue = it.toString() }
            else { dateValue = it.toString(); if (endDate != null && it > endDate) endDateValue = it.toString() }
            showCalendar = false
        })
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun HistoryDateSheet(initial: LocalDate, recordedDates: Set<LocalDate>, dismiss: () -> Unit, confirm: (LocalDate) -> Unit) {
    val context = LocalContext.current
    val locale = context.resources.configuration.locales[0]
    var selected by remember { mutableStateOf(initial) }
    var month by remember { mutableStateOf(YearMonth.from(initial)) }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = DeviceColors.Surface, tonalElevation = 0.dp,
        shape = androidx.compose.ui.graphics.RectangleShape, scrimColor = Color(0xFF0D1729).copy(alpha = .34f),
        dragHandle = { Box(Modifier.padding(top = 12.dp, bottom = 14.dp).size(32.dp, 4.dp)
            .background(DeviceColors.Border, RoundedCornerShape(3.dp))) },
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState())) {
            Text(context.getString(R.string.content_choose_date), Modifier.padding(horizontal = 24.dp), fontSize = 18.sp)
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                ContentIconButton(R.drawable.figma_content_back, context.getString(R.string.content_previous_month)) { month = month.minusMonths(1) }
                Text(month.format(java.time.format.DateTimeFormatter.ofPattern(android.text.format.DateFormat.getBestDateTimePattern(locale, "yMMMM"), locale)), Modifier.weight(1f), textAlign = androidx.compose.ui.text.style.TextAlign.Center, fontSize = 16.sp)
                ContentIconButton(R.drawable.figma_content_back, context.getString(R.string.content_next_month), Modifier.rotate(180f)) { month = month.plusMonths(1) }
            }
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
                java.time.DayOfWeek.entries.map { it.getDisplayName(java.time.format.TextStyle.NARROW, locale) }.forEach { Text(it, Modifier.weight(1f),
                    textAlign = androidx.compose.ui.text.style.TextAlign.Center, fontSize = 12.sp, color = DeviceColors.Secondary) }
            }
            val first = month.atDay(1).minusDays((month.atDay(1).dayOfWeek.value - 1).toLong())
            repeat(6) { week ->
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
                    repeat(7) { day ->
                        val date = first.plusDays((week * 7 + day).toLong())
                        Box(Modifier.weight(1f).height(38.dp).semantics {
                            contentDescription = date.format(java.time.format.DateTimeFormatter.ofLocalizedDate(java.time.format.FormatStyle.LONG).withLocale(locale)); this.selected = date == selected
                        }.clickable { selected = date }, contentAlignment = Alignment.Center) {
                            Box(Modifier.size(32.dp).background(if (date == selected) DeviceColors.Blue else Color.Transparent, CircleShape), contentAlignment = Alignment.Center) {
                                Text(date.dayOfMonth.toString(), fontSize = 12.sp, color = when {
                                    date == selected -> Color.White
                                    YearMonth.from(date) != month -> DeviceColors.Secondary.copy(alpha = 0.5f)
                                    else -> DeviceColors.Ink
                                })
                            }
                            if (date in recordedDates) Box(Modifier.align(Alignment.BottomCenter).size(4.dp)
                                .background(DeviceColors.Blue, CircleShape))
                        }
                    }
                }
            }
            Spacer(Modifier.height(24.dp))
            HorizontalDivider(color = DeviceColors.Border)
            Row(Modifier.fillMaxWidth().padding(16.dp), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                OutlinedButton(onClick = dismiss, modifier = Modifier.weight(1f).height(48.dp), shape = RoundedCornerShape(12.dp)) { Text(context.getString(R.string.content_cancel)) }
                Button(onClick = { confirm(selected) }, modifier = Modifier.weight(1f).height(48.dp), shape = RoundedCornerShape(12.dp)) { Text(context.getString(R.string.content_confirm)) }
            }
        }
    }
}
