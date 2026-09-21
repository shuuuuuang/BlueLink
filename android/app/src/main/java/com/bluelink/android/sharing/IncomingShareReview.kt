package com.bluelink.android.sharing

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.ui.content.FileTypeIcon
import com.bluelink.android.ui.content.contentBytes
import com.bluelink.android.ui.devices.*
import java.util.UUID

/** Shared production presentation; sending and durable inbox ownership stay with the activity. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun IncomingShareReview(
    request: SharedRequest?, requests: List<SharedRequest>, targets: List<ConversationSummary>,
    selectedPeer: String?, busy: Boolean, preparing: Boolean, sending: Boolean,
    progressName: String, progressBytes: Long,
    selectRequest: (UUID) -> Unit, selectPeer: (String) -> Unit,
    close: () -> Unit, cancelPreparation: () -> Unit, discard: () -> Unit, keep: () -> Unit, send: () -> Unit,
) {
    val selected = targets.firstOrNull { it.peerId == selectedPeer }
    val locked = request?.let { it.textSubmitted || it.files.any { file -> file.submitted } } == true
    var showTargets by remember(request?.id) { mutableStateOf(false) }
    var showRequests by remember { mutableStateOf(false) }
    val activeRequests = requests.filter { !it.complete || it.id == request?.id }
    Scaffold(containerColor = DeviceColors.Canvas,
        topBar = { TopAppBar(
            title = { Text(stringResource(R.string.share_review_title), fontSize = 19.sp, fontWeight = FontWeight.Bold) },
            navigationIcon = { TextButton(onClick = close, enabled = !busy) { Text(stringResource(R.string.close)) } },
            actions = { if (request != null && !preparing) TextButton(onClick = discard, enabled = !busy,
                colors = ButtonDefaults.textButtonColors(contentColor = DeviceColors.Secondary)) {
                Text(stringResource(R.string.share_discard))
            } },
            colors = TopAppBarDefaults.topAppBarColors(containerColor = DeviceColors.Surface)) },
        bottomBar = {
            if (request != null && !preparing) Surface(color = DeviceColors.Surface, tonalElevation = 0.dp) {
                Column(Modifier.navigationBarsPadding().padding(horizontal = 20.dp, vertical = 12.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    if (sending) LinearProgressIndicator(Modifier.fillMaxWidth())
                    val note = when {
                        request.complete && request.files.isEmpty() -> R.string.share_text_sent
                        request.complete -> R.string.share_handed_off
                        selected == null -> R.string.share_pick_note
                        selected.availability != DeviceAvailability.CONNECTED -> R.string.share_offline_note
                        else -> R.string.share_review_note
                    }
                    Text(stringResource(note), fontSize = 12.sp, lineHeight = 18.sp,
                        color = if (request.complete) DeviceColors.Success else DeviceColors.Secondary)
                    if (request.complete) Button(onClick = close, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
                        shape = RoundedCornerShape(12.dp)) { Text(stringResource(R.string.batch_done)) }
                    else Row(Modifier.height(IntrinsicSize.Min), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        OutlinedButton(onClick = keep, enabled = !busy, modifier = Modifier.weight(1f).fillMaxHeight().heightIn(min = 48.dp),
                            shape = RoundedCornerShape(12.dp), contentPadding = PaddingValues(horizontal = 12.dp, vertical = 12.dp)) { Text(stringResource(R.string.share_keep_later)) }
                        Button(onClick = send, enabled = !busy && selected?.availability == DeviceAvailability.CONNECTED,
                            modifier = Modifier.weight(1f).fillMaxHeight().heightIn(min = 48.dp), shape = RoundedCornerShape(12.dp)) {
                            Text(stringResource(if (sending) R.string.share_submitting else R.string.share_send))
                        }
                    }
                }
            }
        }) { padding ->
        LazyColumn(Modifier.fillMaxSize().padding(padding), contentPadding = PaddingValues(20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)) {
            if (preparing) item {
                ReviewCard { Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text(stringResource(R.string.share_preparing), fontWeight = FontWeight.Medium)
                    LinearProgressIndicator(Modifier.fillMaxWidth())
                    Text(progressName, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    Text(contentBytes(progressBytes), color = DeviceColors.Secondary)
                    TextButton(onClick = cancelPreparation) { Text(stringResource(R.string.cancel)) }
                } }
            }
            else if (request == null) item {
                ReviewCard { Text(stringResource(R.string.share_no_pending), Modifier.padding(24.dp), color = DeviceColors.Secondary) }
            }
            else {
                if (activeRequests.size > 1) item {
                    Box {
                        TextButton(onClick = { showRequests = true }, enabled = !busy) {
                            Text(stringResource(R.string.share_pending, activeRequests.size))
                            FigmaIcon(R.drawable.figma_chevron, Modifier.padding(start = 6.dp), size = 16.dp, tint = DeviceColors.Secondary)
                        }
                        DropdownMenu(showRequests, { showRequests = false }) {
                            activeRequests.forEach { pending -> DropdownMenuItem(text = {
                                Text(pending.files.firstOrNull()?.name ?: pending.text, maxLines = 2, overflow = TextOverflow.Ellipsis)
                            }, onClick = { showRequests = false; selectRequest(pending.id) }) }
                        }
                    }
                }
                item {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        SectionLabel(stringResource(R.string.share_target))
                        Box {
                            Surface(onClick = { showTargets = true }, enabled = !busy && !locked,
                                color = DeviceColors.Surface, shape = RoundedCornerShape(16.dp), modifier = Modifier.fillMaxWidth()) {
                                Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                                    Box(Modifier.size(44.dp).background(DeviceColors.Selected, RoundedCornerShape(12.dp)), Alignment.Center) {
                                        FigmaIcon(if (selected?.platform == PeerPlatform.ANDROID) R.drawable.figma_phone else R.drawable.figma_desktop,
                                            tint = DeviceColors.Blue)
                                    }
                                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                                        Text(selected?.displayName ?: stringResource(R.string.share_choose_conversation),
                                            fontWeight = FontWeight.Medium, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                        Text(stringResource(when {
                                            selected == null -> R.string.share_trusted_only
                                            selected.availability == DeviceAvailability.CONNECTED -> R.string.content_connected_protocol
                                            else -> R.string.share_offline
                                        }), fontSize = 12.sp, color = if (selected?.availability == DeviceAvailability.CONNECTED) DeviceColors.Success else DeviceColors.Secondary)
                                    }
                                    if (!locked) FigmaIcon(R.drawable.figma_chevron, size = 18.dp, tint = DeviceColors.Secondary)
                                }
                            }
                            DropdownMenu(showTargets, { showTargets = false }, modifier = Modifier.widthIn(max = 340.dp).heightIn(max = 360.dp)) {
                                if (targets.isEmpty()) DropdownMenuItem(text = { Text(stringResource(R.string.share_no_conversations)) }, enabled = false, onClick = {})
                                targets.sortedByDescending { it.availability == DeviceAvailability.CONNECTED }.forEach { target ->
                                    DropdownMenuItem(text = { Column {
                                        Text(target.displayName, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                        Text(stringResource(if (target.availability == DeviceAvailability.CONNECTED) R.string.device_connected else R.string.share_offline),
                                            color = DeviceColors.Secondary, fontSize = 12.sp)
                                    } }, onClick = { showTargets = false; selectPeer(target.peerId) })
                                }
                            }
                        }
                    }
                }
                item {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                        SectionLabel(stringResource(R.string.share_content_label))
                        if (request.files.isNotEmpty()) Text(stringResource(R.string.share_files_summary, request.files.size, contentBytes(request.files.sumOf { it.size })),
                            fontSize = 12.sp, color = DeviceColors.Secondary)
                    }
                }
                if (request.text.isNotEmpty()) item {
                    ReviewCard { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Text(stringResource(R.string.content_text_message), fontSize = 12.sp, color = DeviceColors.Secondary)
                        Text(request.text, fontSize = 15.sp, lineHeight = 24.sp)
                    } }
                }
                items(request.files, key = { it.id }) { file ->
                    ReviewCard { Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        FileTypeIcon(file.name, mimeType = file.mime, size = 36.dp)
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(file.name, maxLines = 2, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.Medium)
                            Text(contentBytes(file.size), fontSize = 12.sp, color = DeviceColors.Secondary)
                            if (file.submitted) Text(stringResource(R.string.share_submitted), fontSize = 12.sp, color = DeviceColors.Success)
                        }
                    } }
                }
            }
        }
    }
}

@Composable private fun SectionLabel(text: String) {
    Text(text, color = DeviceColors.Secondary, fontSize = 13.sp, fontWeight = FontWeight.Medium)
}
@Composable private fun ReviewCard(content: @Composable () -> Unit) {
    Surface(Modifier.fillMaxWidth(), color = DeviceColors.Surface, shape = RoundedCornerShape(16.dp), content = content)
}
