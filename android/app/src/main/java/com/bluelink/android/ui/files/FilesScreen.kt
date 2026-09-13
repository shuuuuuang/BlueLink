package com.bluelink.android.ui.files

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
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
import androidx.compose.ui.text.font.FontWeight
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
                         open: (TransferItem) -> Unit, more: (TransferItem) -> Unit) = DeviceScreenTheme {
    val context = LocalContext.current
    var query by rememberSaveable(scopePeerId) { mutableStateOf("") }
    var status by rememberSaveable(scopePeerId) { mutableStateOf(FileStatusFilter.ALL) }
    var direction by rememberSaveable(scopePeerId) { mutableStateOf(FileDirectionFilter.ALL) }
    var selectedPeer by rememberSaveable(scopePeerId) { mutableStateOf<String?>(scopePeerId) }
    val filtered = remember(transfers, query, status, direction, selectedPeer, scopePeerId) {
        HistoryQuery.files(transfers, query, status, direction, scopePeerId ?: selectedPeer)
    }
    val collapsed = remember(scopePeerId) { mutableStateMapOf<Int, Boolean>() }
    var selectedFile by rememberSaveable(scopePeerId) { mutableStateOf<String?>(null) }
    val emptyDataset = transfers.none { scopePeerId == null || it.peerId == scopePeerId }
    val emptyConversation = scopePeerId != null && emptyDataset
    val groups = linkedMapOf(
        R.string.content_active to filtered.filter { it.status in HistoryQuery.activeStatuses },
        R.string.content_complete_group to filtered.filter { it.status == TransferStatus.COMPLETED },
        R.string.content_failed_group to filtered.filter { it.status !in HistoryQuery.activeStatuses && it.status != TransferStatus.COMPLETED },
    ).filterValues { it.isNotEmpty() }
    Column(modifier.fillMaxSize().background(DeviceColors.Canvas)) {
        if (!emptyConversation) {
        ContentSearch(query, { query = it }, context.getString(R.string.content_search_files), Modifier.padding(start = 16.dp, end = 16.dp, top = 12.dp))
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            ContentFilter(status.contentLabel(context), FileStatusFilter.entries.map { it.contentLabel(context) to it }, status,
                { status = it }, Modifier.weight(1f))
            if (scopePeerId == null) ContentFilter(conversations.firstOrNull { it.peerId == selectedPeer }?.peerName ?: context.getString(R.string.content_all_devices),
                listOf(context.getString(R.string.content_all_devices) to null) + conversations.map { it.peerName to it.peerId }, selectedPeer,
                { selectedPeer = it }, Modifier.weight(1f))
            ContentFilter(direction.contentLabel(context), FileDirectionFilter.entries.map { it.contentLabel(context) to it }, direction,
                { direction = it }, Modifier.weight(1f))
        }
        }
        if (emptyDataset) Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
            ContentFilesEmpty(conversation = scopePeerId != null)
        } else if (filtered.isEmpty()) Box(Modifier.weight(1f).fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp)) {
            ContentSearchEmpty(if (query.isNotBlank()) context.getString(R.string.content_no_file_results) else context.getString(R.string.content_no_filtered_files),
                if (query.isNotBlank()) context.getString(R.string.content_file_search_hint) else context.getString(R.string.content_file_filter_hint))
        } else LazyColumn(Modifier.weight(1f).fillMaxWidth(), contentPadding = PaddingValues(start = 12.dp, end = 12.dp, bottom = 12.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)) {
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
                        selectedFile == transfer.id.toString(), click = { selectedFile = transfer.id.toString(); open(transfer) },
                        more = { selectedFile = transfer.id.toString(); more(transfer) })
                }
            }
        }
        HorizontalDivider(color = DeviceColors.Border)
        Row(Modifier.fillMaxWidth().background(DeviceColors.Surface).heightIn(min = 48.dp).clickable(onClick = openSettings)
            .padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(context.getString(R.string.content_receive_folder, receiveDirectoryLabel(receiveDirectory, context)), color = DeviceColors.Secondary, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            Text(context.getString(R.string.content_change), color = DeviceColors.Blue, fontSize = 12.sp)
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun FileRecord(item: TransferItem, peerName: String, selected: Boolean, click: () -> Unit, more: () -> Unit) {
    val context = LocalContext.current
    val active = item.status in HistoryQuery.activeStatuses
    val failed = item.status in setOf(TransferStatus.FAILED, TransferStatus.REJECTED)
    val color = when { failed -> DeviceColors.Error; item.status == TransferStatus.PAUSED && item.outgoing -> DeviceColors.Warning; active -> DeviceColors.Blue; item.status == TransferStatus.COMPLETED -> DeviceColors.Success; else -> DeviceColors.Secondary }
    Row(Modifier.fillMaxWidth().heightIn(min = 86.dp).background(DeviceColors.Surface, RoundedCornerShape(12.dp))
        .border(1.dp, if (failed) DeviceColors.Error else if (selected) DeviceColors.Blue else DeviceColors.Border, RoundedCornerShape(12.dp))
        .combinedClickable(onClick = click, onLongClick = more).padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
        FileTypeIcon(item.name, mimeType = item.mimeType)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(item.name, fontSize = 13.sp, fontWeight = FontWeight.Normal, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text("${contentBytes(item.totalBytes)} · ${contentRoute(item.outgoing, peerName, context)}",
                fontSize = 10.sp, lineHeight = 16.sp, color = DeviceColors.Secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text("${contentDay(Instant.ofEpochMilli(item.startedAtEpochMs), context)} ${contentTime(Instant.ofEpochMilli(item.startedAtEpochMs))}",
                fontSize = 10.sp, lineHeight = 16.sp, color = DeviceColors.Secondary, maxLines = 1)
        }
        Spacer(Modifier.width(8.dp))
        Column(Modifier.align(Alignment.Top), horizontalAlignment = Alignment.End) {
            Surface(color = color.copy(alpha = 0.08f), shape = RoundedCornerShape(12.dp)) {
                val status = when {
                    item.status == TransferStatus.TRANSFERRING -> context.getString(if (item.outgoing) R.string.content_transferring else R.string.content_receiving)
                    item.status == TransferStatus.REMOTE_PAUSED -> context.getString(if (item.outgoing) R.string.content_remote_paused else R.string.content_waiting_receive)
                    else -> contentStatus(item.status, context)
                }
                val label = if (item.status in setOf(TransferStatus.TRANSFERRING, TransferStatus.PAUSED, TransferStatus.RESUMING))
                    "$status ${(item.progress.coerceIn(0f, 1f) * 100).toInt()}%" else status
                Text(label, color = color, fontSize = 10.sp, lineHeight = 16.sp,
                    modifier = Modifier.widthIn(max = 132.dp).padding(horizontal = 10.dp, vertical = 4.dp))
            }
        }
    }
}
