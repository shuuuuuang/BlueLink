package com.bluelink.android.ui.conversation

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.text.buildAnnotatedString
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

@Composable
internal fun MessageSearchScreen(messages: List<ChatItem>, peerName: String, showThumbnails: Boolean,
                                 close: () -> Unit, locate: (ChatItem) -> Unit, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    var query by rememberSaveable { mutableStateOf("") }
    var kind by rememberSaveable { mutableStateOf(HistoryKind.ALL) }
    var dateValue by rememberSaveable { mutableStateOf<String?>(null) }
    var showCalendar by rememberSaveable { mutableStateOf(false) }
    val date = dateValue?.let(LocalDate::parse)
    val results = remember(messages, query, kind, date) { HistoryQuery.messages(messages, query, kind, date) }
    BackHandler(onBack = close)
    Column(modifier.fillMaxSize().background(DeviceColors.Canvas)) {
        Box(Modifier.fillMaxWidth().heightIn(min = 58.dp).background(DeviceColors.Surface)) {
            Text(context.getString(R.string.content_history_with, peerName), Modifier.align(Alignment.Center).padding(horizontal = 56.dp),
                fontSize = 18.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            ContentIconButton(R.drawable.figma_content_close, context.getString(R.string.content_close_search), Modifier.align(Alignment.CenterStart).padding(start = 4.dp), action = close)
        }
        HorizontalDivider(color = DeviceColors.Border)
        ContentSearch(query, { query = it }, context.getString(R.string.content_search_messages), Modifier.padding(horizontal = 16.dp, vertical = 12.dp), 48.dp)
        ContentTabs(HistoryKind.entries.map { it.contentLabel(context) }, kind.ordinal) {
            kind = HistoryKind.entries[it]
            if (kind == HistoryKind.DATE) showCalendar = true
        }
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            if (kind == HistoryKind.DATE) Text(date?.toString() ?: context.getString(R.string.content_all_dates), color = DeviceColors.Blue, fontSize = 12.sp,
                modifier = Modifier.clickable { showCalendar = true })
            Spacer(Modifier.weight(1f))
            Text(context.resources.getQuantityString(R.plurals.content_result_count, results.size, results.size), color = DeviceColors.Secondary, fontSize = 12.sp)
        }
        if (results.isEmpty()) Box(Modifier.fillMaxWidth().weight(1f).padding(horizontal = 16.dp, vertical = 4.dp)) {
            ContentSearchEmpty(context.getString(R.string.content_no_message_results), context.getString(R.string.content_message_search_hint))
        } else LazyColumn(Modifier.weight(1f), contentPadding = PaddingValues(start = 16.dp, end = 16.dp, bottom = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)) {
            results.groupBy { contentDay(it.timestamp, context) }.forEach { (day, items) ->
                item(key = "day-$day") { Text(day, color = DeviceColors.Secondary, fontSize = 12.sp) }
                items(items, key = { it.id.toString() }) { item ->
                    val attachment = item.attachments.firstOrNull { it.fileName.contains(query.trim(), true) }
                        ?: item.attachments.firstOrNull()
                    Row(Modifier.fillMaxWidth().heightIn(min = if (attachment?.isImage == true && showThumbnails) 108.dp else 88.dp)
                        .background(DeviceColors.Surface, RoundedCornerShape(12.dp)).border(1.dp, DeviceColors.Border, RoundedCornerShape(12.dp))
                        .clickable { locate(item) }.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(44.dp).background(if (attachment == null) DeviceColors.Selected else DeviceColors.Surface, RoundedCornerShape(12.dp)),
                            contentAlignment = Alignment.Center) {
                            if (attachment == null) FigmaIcon(R.drawable.figma_content_message, size = 22.dp)
                            else FileTypeIcon(attachment.fileName, mimeType = attachment.mimeType, size = 24.dp)
                        }
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text("${if (item.outgoing) context.getString(R.string.content_this_device) else peerName} · $day ${contentTime(item.timestamp)}", fontSize = 11.sp,
                                lineHeight = 16.sp, color = DeviceColors.Secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            HighlightedText(attachment?.fileName ?: item.text, query)
                            Text(attachment?.let { "${contentBytes(it.sizeBytes)} · ${it.contentStatus(context)}" } ?: context.getString(R.string.content_text_message),
                                fontSize = 11.sp, lineHeight = 16.sp, color = DeviceColors.Secondary)
                        }
                        if (attachment?.showsThumbnail(showThumbnails) == true) {
                            Spacer(Modifier.width(8.dp)); AttachmentThumbnail(attachment, Modifier.size(72.dp, 58.dp))
                        }
                        FigmaIcon(R.drawable.figma_content_chevron, Modifier.padding(start = 8.dp), size = 22.dp)
                    }
                }
            }
        }
    }
    if (showCalendar) HistoryDateSheet(date ?: messages.lastOrNull()?.timestamp?.atZone(ZoneId.systemDefault())?.toLocalDate() ?: LocalDate.now(),
        messages.map { it.timestamp.atZone(ZoneId.systemDefault()).toLocalDate() }.toSet(),
        dismiss = { showCalendar = false }, confirm = { dateValue = it.toString(); showCalendar = false })
}

@Composable
private fun HighlightedText(text: String, query: String) {
    val highlightColor = DeviceColors.Blue
    val annotated = remember(text, query, highlightColor) { buildAnnotatedString {
        append(text)
        val needle = query.trim()
        if (needle.isNotEmpty()) {
            var start = text.indexOf(needle, ignoreCase = true)
            while (start >= 0) {
                addStyle(SpanStyle(color = highlightColor), start, start + needle.length)
                start = text.indexOf(needle, start + needle.length, ignoreCase = true)
            }
        }
    } }
    Text(annotated, fontSize = 14.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun HistoryDateSheet(initial: LocalDate, recordedDates: Set<LocalDate>, dismiss: () -> Unit, confirm: (LocalDate) -> Unit) {
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
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
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
