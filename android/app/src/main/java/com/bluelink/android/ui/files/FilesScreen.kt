package com.bluelink.android.ui.files

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.HorizontalDivider
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material.icons.filled.Check
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.material3.TextButton
import androidx.compose.material3.CircularProgressIndicator
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.ui.content.*
import com.bluelink.android.ui.devices.*
import java.time.Instant

@Composable
internal fun FilesScreen(modifier: Modifier = Modifier, transfers: List<TransferItem>,
                         conversations: List<ConversationSummary>, scopePeerId: String? = null,
                         receiveDirectory: String, openSettings: () -> Unit,
                         open: (TransferItem) -> Unit, more: (TransferItem) -> Unit,
                         batch: FileBatchOperations? = null,
                         moreWithSelection: ((TransferItem, () -> Unit) -> Unit)? = null,
                         selectionModeChanged: (Boolean) -> Unit = {},
                         previewImagesChanged: (List<ChatAttachment>) -> Unit = {}) = DeviceScreenTheme {
    val context = LocalContext.current
    var selectionMode by rememberSaveable(scopePeerId) { mutableStateOf(false) }
    val reportSelectionMode by rememberUpdatedState(selectionModeChanged)
    SideEffect { reportSelectionMode(selectionMode) }
    DisposableEffect(Unit) { onDispose { reportSelectionMode(false) } }
    var selectedIds by rememberSaveable(scopePeerId) { mutableStateOf(listOf<String>()) }
    var selectionAnchor by rememberSaveable(scopePeerId) { mutableStateOf<String?>(null) }
    var batchBusy by remember { mutableStateOf(false) }
    val listState = rememberLazyListState()
    fun leaveSelection() { selectionMode = false; selectedIds = emptyList(); selectionAnchor = null }
    LaunchedEffect(transfers, selectedIds) {
        val ids = transfers.map { it.id.toString() }.toSet()
        selectedIds = selectedIds.filter { it in ids }
        if (selectionAnchor !in selectedIds) selectionAnchor = selectedIds.lastOrNull()
    }
    fun toggle(id: String) {
        if (batchBusy) return
        if (id !in selectedIds && selectedIds.size < FileBatchPolicy.MAXIMUM_SELECTION) selectionAnchor = id
        selectedIds = if(id in selectedIds) selectedIds - id else if(selectedIds.size < FileBatchPolicy.MAXIMUM_SELECTION) selectedIds + id else {
            android.widget.Toast.makeText(context,context.getString(R.string.batch_selection_limit),android.widget.Toast.LENGTH_SHORT).show(); selectedIds
        }
    }
    var query by rememberSaveable(scopePeerId) { mutableStateOf("") }
    var filters by rememberSaveable(scopePeerId, stateSaver = FileFilterSelectionSaver) { mutableStateOf(FileFilterSelection()) }
    var sortExpanded by remember { mutableStateOf(false) }
    var filtersExpanded by rememberSaveable(scopePeerId) { mutableStateOf(false) }
    var sort by rememberSaveable(scopePeerId) { mutableStateOf(HistorySort.TIME) }
    var descending by rememberSaveable(scopePeerId) { mutableStateOf(true) }
    val keyboard = LocalSoftwareKeyboardController.current
    val options = filters.options(query, sort, descending, scopePeerId, context.resources.configuration.locales[0])
    var pageSize by remember(transfers, options) { mutableIntStateOf(100) }
    val filtered by produceState<List<TransferItem>?>(null, transfers, options) {
        value = null
        delay(125)
        value = withContext(Dispatchers.Default) {
            val task = currentCoroutineContext()
            HistorySearch.files(transfers, options, checkCancelled = { task.ensureActive() })
        }
    }
    SideEffect { filtered?.let { items -> previewImagesChanged(items.map { it.asGalleryAttachment() }) } }
    val visibleFiles = filtered.orEmpty().take(pageSize)
    val collapsed = remember(scopePeerId) { mutableStateMapOf<Int, Boolean>() }
    var selectedFile by rememberSaveable(scopePeerId) { mutableStateOf<String?>(null) }
    val emptyDataset = transfers.none { scopePeerId == null || it.peerId == scopePeerId }
    val emptyConversation = scopePeerId != null && emptyDataset
    val groups = linkedMapOf(
        R.string.content_active to visibleFiles.filter { it.status in HistoryQuery.activeStatuses },
        R.string.content_complete_group to visibleFiles.filter { it.status == TransferStatus.COMPLETED },
        R.string.content_incomplete_group to visibleFiles.filter { it.status !in HistoryQuery.activeStatuses && it.status != TransferStatus.COMPLETED },
    ).filterValues { it.isNotEmpty() }
    val displayedIds = groups.filterKeys { collapsed[it] != true }.values.flatten().map { it.id.toString() }
    Column(modifier.fillMaxSize().background(DeviceColors.Canvas)) {
        if (selectionMode) {
            Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).background(DeviceColors.Surface).padding(horizontal = 16.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Text(context.getString(R.string.batch_selected, selectedIds.size), Modifier.weight(1f),
                    color = DeviceColors.Ink, fontSize = 14.sp)
                TextButton(enabled = !batchBusy, onClick = { leaveSelection() }) { Text(context.getString(R.string.cancel)) }
            }
            HorizontalDivider(color = DeviceColors.Border)
        } else if (!emptyConversation) {
        if (!selectionMode) ContentSearch(query, { query = it }, context.getString(R.string.content_search_files),
            Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, top = 12.dp),
            height = 42.dp, cornerRadius = 10.dp)
        Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(if(filtered == null) context.getString(R.string.content_searching) else context.resources.getQuantityString(R.plurals.content_result_count, filtered.orEmpty().size, filtered.orEmpty().size),
                Modifier.weight(1f), fontSize = 11.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, color = DeviceColors.Secondary)
            if(!selectionMode) {
                Box {
                    val label = context.getString(when(sort) { HistorySort.TIME -> R.string.content_sort_time; HistorySort.NAME -> R.string.content_sort_name; HistorySort.SIZE -> R.string.content_sort_size })
                    TextButton(onClick = { keyboard?.hide(); sortExpanded = true }, contentPadding = PaddingValues(horizontal = 8.dp),
                        modifier = Modifier.semantics { contentDescription = context.getString(R.string.file_filter_sort_description, label, context.getString(if(descending) R.string.content_descending else R.string.content_ascending)) }) {
                        Text(label + if(descending) " ↓" else " ↑", fontSize = 12.sp, color = DeviceColors.Secondary)
                    }
                    DropdownMenu(sortExpanded, { sortExpanded = false }) {
                        for(key in HistorySort.entries) for(desc in listOf(true, false)) {
                            val name = context.getString(when(key) { HistorySort.TIME -> R.string.content_sort_time; HistorySort.NAME -> R.string.content_sort_name; HistorySort.SIZE -> R.string.content_sort_size }) + " · " + context.getString(if(desc) R.string.content_descending else R.string.content_ascending)
                            DropdownMenuItem(text = { Text(name) }, trailingIcon = { if(sort == key && descending == desc) Icon(Icons.Default.Check, null) },
                                onClick = { sort = key; descending = desc; sortExpanded = false })
                        }
                    }
                }
                val active = filters.count(scopePeerId)
                TextButton(onClick = { keyboard?.hide(); filtersExpanded = true }, contentPadding = PaddingValues(horizontal = 8.dp),
                    modifier = Modifier.semantics { contentDescription = context.getString(R.string.file_filter_count_description, active) }) {
                    val tint = if(active > 0) DeviceColors.Blue else DeviceColors.Secondary
                    Icon(Icons.AutoMirrored.Filled.Sort, null, Modifier.size(16.dp), tint = tint)
                    Spacer(Modifier.width(4.dp))
                    Text(context.getString(R.string.file_filter_title) + if(active > 0) " $active" else "", color = tint, fontSize = 12.sp)
                }

            }
        }
        }
        if (filtered == null) Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        else if (emptyDataset) Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
            ContentFilesEmpty(conversation = scopePeerId != null)
        } else if (filtered.orEmpty().isEmpty()) Box(Modifier.weight(1f).fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp)) {
            ContentSearchEmpty(if (query.isNotBlank()) context.getString(R.string.content_no_file_results) else context.getString(R.string.content_no_filtered_files),
                if (query.isNotBlank()) context.getString(R.string.content_file_search_hint) else context.getString(R.string.content_file_filter_hint))
        } else Box(Modifier.weight(1f).fillMaxWidth()) {
        LazyColumn(Modifier.fillMaxSize(), state = listState, contentPadding = PaddingValues(start = 12.dp, end = 12.dp, bottom = 12.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp)) {
            groups.forEach { (group, files) ->
                val day = context.getString(group)
                item(key = "group-$group") {
                    Row(Modifier.fillMaxWidth().clickable { collapsed[group] = collapsed[group] != true }
                        .padding(horizontal = 4.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text("$day (${files.size})", Modifier.weight(1f), color = DeviceColors.Secondary, fontSize = 12.sp)
                        FigmaIcon(R.drawable.figma_chevron,
                            Modifier.rotate(if (collapsed[group] == true) -90f else 0f)
                                .semantics { contentDescription = context.getString(if (collapsed[group] == true) R.string.content_expand else R.string.content_collapse, day) },
                            size = 14.dp, tint = DeviceColors.Secondary)
                    }
                }
                if (collapsed[group] != true) items(files, key = { it.id.toString() }) { transfer ->
                    FileRecord(transfer, conversations.firstOrNull { it.peerId == transfer.peerId }?.peerName ?: context.getString(R.string.content_unknown_device),
                        if(selectionMode) transfer.id.toString() in selectedIds else selectedFile == transfer.id.toString(),
                        query = query, click = { if(selectionMode) toggle(transfer.id.toString()) else { selectedFile = transfer.id.toString(); open(transfer) } },
                        selectionMode = selectionMode, selectionEnabled = !batchBusy, toggle = { toggle(transfer.id.toString()) },
                        more = {
                            selectedFile = transfer.id.toString()
                            if(batch != null && moreWithSelection != null) moreWithSelection(transfer) { keyboard?.hide(); selectionMode = true; selectedIds = listOf(transfer.id.toString()); selectionAnchor = transfer.id.toString() }
                            else more(transfer)
                        })
                }
            }
            if (pageSize < filtered.orEmpty().size) item(key = "more-files") {
                TextButton(onClick = { pageSize += 100 }, modifier = Modifier.fillMaxWidth()) { Text(context.getString(R.string.content_load_more)) }
            }
        }

        if (selectionMode) FileRangeSelectionButton(listState, displayedIds, selectedIds, selectionAnchor, !batchBusy) { target ->
            val selection = FileBatchSelection.selectRange(displayedIds, selectedIds, selectionAnchor, target)
            if (selection == null) android.widget.Toast.makeText(context, context.getString(R.string.batch_selection_limit), android.widget.Toast.LENGTH_SHORT).show()
            else selectedIds = selection
        }
        }

        if(selectionMode && batch != null) FileBatchToolbar(selectedIds, transfers, batch, batchBusy,
            setBusy = { batchBusy = it }, select = { selectedIds = it }, leave = { leaveSelection() })
    }
    if(filtersExpanded) FileFilterSheet(filters, transfers, conversations, scopePeerId, query, sort, descending,
        dismiss = { filtersExpanded = false }, apply = { filters = it })

}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun FileRecord(item: TransferItem, peerName: String, selected: Boolean, query: String, click: () -> Unit, more: () -> Unit, selectionMode: Boolean = false, selectionEnabled: Boolean = true, toggle: () -> Unit = {}) {
    val context = LocalContext.current
    val active = item.status in HistoryQuery.activeStatuses
    val failed = !item.recoveryPending && item.status in setOf(TransferStatus.FAILED, TransferStatus.REJECTED)
    val color = when { failed -> DeviceColors.Error; item.status == TransferStatus.PAUSED && item.outgoing -> DeviceColors.Warning; active -> DeviceColors.Blue; item.status == TransferStatus.COMPLETED -> DeviceColors.Success; else -> DeviceColors.Secondary }
    Row(Modifier.fillMaxWidth().heightIn(min = 76.dp).background(DeviceColors.Surface, RoundedCornerShape(12.dp))
        .border(1.dp, if (failed) DeviceColors.Error else if (selected) DeviceColors.Blue else DeviceColors.Border, RoundedCornerShape(12.dp))
        .combinedClickable(enabled = !selectionMode || selectionEnabled, onClick = click, onLongClick = if (selectionMode) null else more).padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        if(selectionMode) androidx.compose.material3.Checkbox(checked = selected, enabled = selectionEnabled, onCheckedChange = { toggle() },
            modifier = Modifier.semantics { contentDescription = context.getString(R.string.batch_select_file,item.name) })
        FileTypeIcon(item.name, mimeType = item.mimeType, size = 32.dp)
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            SearchPreviewText(item.name, query, fileName = true, fontSize = 13.sp, maxLines = 1)
            Text("${contentBytes(item.totalBytes)} · ${contentRoute(item.outgoing, peerName, context)}",
                fontSize = 10.sp, lineHeight = 16.sp, color = DeviceColors.Secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("${contentDay(Instant.ofEpochMilli(item.startedAtEpochMs), context)} ${contentTime(Instant.ofEpochMilli(item.startedAtEpochMs))}",
                    Modifier.weight(1f), fontSize = 10.sp, lineHeight = 16.sp, color = DeviceColors.Secondary,
                    maxLines = 1, overflow = TextOverflow.Ellipsis)
                val status = when {
                    item.status == TransferStatus.TRANSFERRING -> context.getString(if (item.outgoing) R.string.content_transferring else R.string.content_receiving)
                    item.status == TransferStatus.REMOTE_PAUSED -> context.getString(if (item.outgoing) R.string.content_remote_paused else R.string.content_waiting_receive)
                    else -> contentStatus(item, context)
                }
                val label = if (item.status in setOf(TransferStatus.TRANSFERRING, TransferStatus.PAUSED, TransferStatus.RESUMING))
                    "$status ${(item.progress.coerceIn(0f, 1f) * 100).toInt()}%" else status
                Text(label, color = color, fontSize = 10.sp, lineHeight = 16.sp,
                    modifier = Modifier.widthIn(max = 132.dp), maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}
