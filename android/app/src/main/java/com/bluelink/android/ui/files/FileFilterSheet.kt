package com.bluelink.android.ui.files

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.listSaver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.window.Popup
import androidx.compose.ui.window.PopupProperties
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.ui.content.ContentSearch
import com.bluelink.android.ui.content.contentLabel
import com.bluelink.android.ui.devices.DeviceColors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

internal val FileFilterSelectionSaver = listSaver<FileFilterSelection, String>(
    save = { listOf(it.status.name, it.direction.name, it.peerId.orEmpty(), it.kind.name,
        it.start?.toString().orEmpty(), it.end?.toString().orEmpty()) },
    restore = { FileFilterSelection(FileStatusFilter.valueOf(it[0]), FileDirectionFilter.valueOf(it[1]),
        it[2].ifEmpty { null }, FileKind.valueOf(it[3]), it[4].takeIf(String::isNotEmpty)?.let(LocalDate::parse),
        it[5].takeIf(String::isNotEmpty)?.let(LocalDate::parse)) }
)

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
internal fun FileFilterSheet(initial: FileFilterSelection, transfers: List<TransferItem>,
                             conversations: List<ConversationSummary>, scopePeerId: String?, query: String,
                             sort: HistorySort, descending: Boolean, dismiss: () -> Unit,
                             apply: (FileFilterSelection) -> Unit) {
    val context = LocalContext.current
    val locale = context.resources.configuration.locales[0]
    var draft by rememberSaveable(stateSaver = FileFilterSelectionSaver) { mutableStateOf(initial) }
    var expandedFilter by rememberSaveable { mutableStateOf<String?>(null) }
    var editDate by rememberSaveable { mutableIntStateOf(0) }
    val previewOptions = draft.options(query, sort, descending, scopePeerId, locale)
    // Tag the result with its options so a previous count cannot label a new draft.
    val preview by produceState<Pair<FileQueryOptions, Int>?>(null, transfers, previewOptions) {
        value = null
        if(draft.valid) {
            delay(125)
            val count = withContext(Dispatchers.Default) {
                val task = currentCoroutineContext()
                HistorySearch.files(transfers, previewOptions, checkCancelled = { task.ensureActive() }).size
            }
            value = previewOptions to count
        }
    }
    val count = preview?.takeIf { it.first == previewOptions }?.second
    val maxHeight = (LocalConfiguration.current.screenHeightDp * .85f).dp
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = DeviceColors.Surface, tonalElevation = 0.dp,
        shape = RoundedCornerShape(topStart = 24.dp, topEnd = 24.dp),
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        dragHandle = { Box(Modifier.padding(top = 10.dp, bottom = 14.dp).size(32.dp, 4.dp)
            .background(DeviceColors.Border, RoundedCornerShape(2.dp))) }) {
        BackHandler(editDate != 0) { editDate = 0 }
        Column(Modifier.fillMaxWidth().heightIn(max = maxHeight)) {
            Row(Modifier.fillMaxWidth().padding(start = if(editDate == 0) 20.dp else 4.dp, end = 20.dp, bottom = 12.dp),
                verticalAlignment = Alignment.CenterVertically) {
                if(editDate != 0) IconButton(onClick = { editDate = 0 }) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, context.getString(R.string.content_cancel))
                }
                Text(context.getString(if(editDate == 0) R.string.file_filter_title else if(editDate == 1)
                    R.string.content_date_start else R.string.content_date_end), fontSize = 18.sp,
                    fontWeight = FontWeight.Medium, color = DeviceColors.Ink)
            }
            if(editDate != 0) {
                key(editDate) {
                    val initialDate = if(editDate == 1) draft.start else draft.end
                    val picker = rememberDatePickerState(initialSelectedDateMillis = initialDate?.atStartOfDay(ZoneOffset.UTC)?.toInstant()?.toEpochMilli())
                    Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState())) {
                        DatePicker(state = picker, title = null, headline = null, showModeToggle = false,
                            colors = DatePickerDefaults.colors(containerColor = DeviceColors.Surface))
                    }
                    FilterFooter(context.getString(R.string.file_filter_reset), { if(editDate == 1) draft = draft.copy(start = null) else draft = draft.copy(end = null); editDate = 0 },
                        context.getString(R.string.batch_done), true) {
                        val date = picker.selectedDateMillis?.let { Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate() }
                        draft = if(editDate == 1) draft.copy(start = date) else draft.copy(end = date)
                        editDate = 0
                    }
                }
            } else {
                Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState()).padding(horizontal = 20.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterSegments(context.getString(R.string.content_file_type), FileKind.entries.map { kind ->
                        kind to context.getString(when(kind) { FileKind.ALL -> R.string.content_all; FileKind.IMAGES -> R.string.content_images; FileKind.FILES -> R.string.content_files })
                    }, draft.kind) { draft = draft.copy(kind = it) }
                    FilterSegments(context.getString(R.string.content_direction), FileDirectionFilter.entries.map { direction ->
                        direction to if(direction == FileDirectionFilter.ALL) context.getString(R.string.content_all) else direction.contentLabel(context)
                    }, draft.direction) { draft = draft.copy(direction = it) }
                    FilterDropdown(context.getString(R.string.content_status),
                        FileStatusFilter.entries.map { it to if(it == FileStatusFilter.ALL) context.getString(R.string.content_all) else it.contentLabel(context) },
                        draft.status, expandedFilter == "status", { expandedFilter = "status" }, { expandedFilter = null }) {
                        draft = draft.copy(status = it)
                    }
                    if(scopePeerId == null) {
                        FilterDropdown(context.getString(R.string.file_filter_device),
                            listOf(null to context.getString(R.string.content_all_devices)) + conversations.map { it.peerId to it.peerName },
                            draft.peerId, expandedFilter == "device", { expandedFilter = "device" }, { expandedFilter = null },
                            searchHint = if(conversations.size > 6) context.getString(R.string.file_filter_search_devices) else null,
                            fallbackLabel = context.getString(R.string.content_unknown_device)) {
                            draft = draft.copy(peerId = it)
                        }
                    }
                    HorizontalDivider(color = DeviceColors.Border)
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        listOf(1 to draft.start, 2 to draft.end).forEach { (part, date) ->
                            Surface(onClick = { editDate = part }, modifier = Modifier.weight(1f),
                                shape = RoundedCornerShape(10.dp), color = DeviceColors.Surface,
                                border = BorderStroke(1.dp, DeviceColors.Border)) {
                                Column(Modifier.padding(horizontal = 12.dp, vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                                    Text(context.getString(if(part == 1) R.string.content_date_start else R.string.content_date_end), fontSize = 11.sp, color = DeviceColors.Secondary)
                                    Text(date?.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).withLocale(locale)) ?: context.getString(R.string.file_filter_unlimited),
                                        fontSize = 13.sp, fontWeight = FontWeight.Normal, color = DeviceColors.Ink)
                                }
                            }
                        }
                    }
                    if(!draft.valid) Text(context.getString(R.string.file_filter_invalid_dates), color = DeviceColors.Error, fontSize = 12.sp)
                    Spacer(Modifier.height(4.dp))
                }
                FilterFooter(context.getString(R.string.file_filter_reset), { draft = FileFilterSelection() },
                    count?.let { context.resources.getQuantityString(R.plurals.file_filter_view_results, it, it) }
                        ?: context.getString(if(draft.valid) R.string.content_searching else R.string.content_confirm),
                    draft.valid && count != null) { apply(draft); dismiss() }
            }
        }
    }
}

/** The Popup is outside sheet measurement; expanding choices never grows the bottom sheet. */
@Composable
private fun <T> FilterDropdown(label: String, options: List<Pair<T, String>>, selected: T,
                               expanded: Boolean, expand: () -> Unit, dismiss: () -> Unit,
                               searchHint: String? = null, fallbackLabel: String = "", select: (T) -> Unit) {
    val density = LocalDensity.current
    var anchorWidth by remember { mutableIntStateOf(0) }
    val maxMenuHeight = (LocalConfiguration.current.screenHeightDp * .45f).dp.coerceAtMost(360.dp)
    val position = remember(density) { with(density) { FileFilterPopupPosition(12.dp.roundToPx(), 6.dp.roundToPx()) } }
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(label, Modifier.width(58.dp), fontSize = 12.sp, color = DeviceColors.Secondary)
        Box(Modifier.weight(1f).onSizeChanged { anchorWidth = it.width }) {
            Surface(onClick = { if(expanded) dismiss() else expand() }, modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(8.dp), color = DeviceColors.Surface,
                border = BorderStroke(1.dp, if(expanded) DeviceColors.Blue else DeviceColors.Border)) {
                Row(Modifier.heightIn(min = 44.dp).padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(options.firstOrNull { it.first == selected }?.second ?: fallbackLabel, Modifier.weight(1f),
                        fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, color = DeviceColors.Ink)
                    Icon(Icons.Default.ExpandMore, label, Modifier.padding(start = 8.dp).size(18.dp).rotate(if(expanded) 180f else 0f), tint = DeviceColors.Secondary)
                }
            }
            if(expanded && anchorWidth > 0) {
                var query by remember { mutableStateOf("") }
                Popup(popupPositionProvider = position, onDismissRequest = dismiss,
                    properties = PopupProperties(focusable = true)) {
                    Surface(Modifier.width(with(density) { anchorWidth.toDp() }).heightIn(max = maxMenuHeight),
                        shape = RoundedCornerShape(12.dp), color = DeviceColors.Surface,
                        border = BorderStroke(1.dp, DeviceColors.Border), shadowElevation = 8.dp) {
                        Column(Modifier.padding(vertical = 6.dp)) {
                            if(searchHint != null) Box(Modifier.padding(horizontal = 8.dp, vertical = 4.dp)) {
                                ContentSearch(query, { query = it }, searchHint)
                            }
                            Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState()).selectableGroup()) {
                                options.filter { it.first == null || it.second.contains(query, true) }.forEach { (value, title) ->
                                    val checked = value == selected
                                    Row(Modifier.fillMaxWidth().heightIn(min = 48.dp)
                                        .selectable(checked, role = Role.RadioButton, onClick = { select(value); dismiss() })
                                        .padding(horizontal = 12.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                                        Text(title, Modifier.weight(1f), fontSize = 13.sp, maxLines = 2, overflow = TextOverflow.Ellipsis,
                                            color = if(checked) DeviceColors.Blue else DeviceColors.Ink)
                                        Box(Modifier.padding(start = 8.dp).size(18.dp)) {
                                            if(checked) Icon(Icons.Default.Check, null, tint = DeviceColors.Blue)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun FilterFooter(resetLabel: String, reset: () -> Unit, applyLabel: String, enabled: Boolean, apply: () -> Unit) {
    HorizontalDivider(color = DeviceColors.Border)
    Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        TextButton(onClick = reset) { Text(resetLabel, fontSize = 13.sp, fontWeight = FontWeight.Normal) }
        Button(onClick = apply, enabled = enabled, modifier = Modifier.weight(1f).heightIn(min = 48.dp), shape = RoundedCornerShape(12.dp)) { Text(applyLabel, fontSize = 14.sp, fontWeight = FontWeight.Medium) }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun <T> FilterSegments(label: String, options: List<Pair<T, String>>, selected: T, select: (T) -> Unit) {
    val colors = SegmentedButtonDefaults.colors(activeContainerColor = DeviceColors.Blue.copy(alpha = .08f),
        activeContentColor = DeviceColors.Blue, activeBorderColor = DeviceColors.Blue,
        inactiveContainerColor = Color.Transparent, inactiveContentColor = DeviceColors.Ink,
        inactiveBorderColor = DeviceColors.Border)
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(label, Modifier.width(58.dp), fontSize = 12.sp, color = DeviceColors.Secondary)
        SingleChoiceSegmentedButtonRow(Modifier.weight(1f)) {
            options.forEachIndexed { index, (value, title) ->
                SegmentedButton(selected == value, onClick = { select(value) }, icon = {},
                    shape = SegmentedButtonDefaults.itemShape(index, options.size, RoundedCornerShape(8.dp)),
                    colors = colors, label = { Text(title, fontSize = 12.sp, fontWeight = FontWeight.Normal) })
            }
        }
    }
}
