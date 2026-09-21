package com.bluelink.android.ui.conversation

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.widget.Toast
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.files.FileInteraction
import com.bluelink.android.ui.components.BlueLinkPrompt
import com.bluelink.android.ui.devices.DeviceColors
import kotlinx.coroutines.launch
import java.util.UUID

@Composable
internal fun MessageBatchToolbar(messages: List<ChatItem>, ids: List<String>, busy: Boolean,
    setBusy: (Boolean) -> Unit, select: (List<String>) -> Unit, leave: () -> Unit,
    delete: suspend (List<UUID>) -> List<MessageBatchResult>) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val selected = MessageBatch.ordered(messages, ids.map(UUID::fromString))
    val files = MessageBatch.attachments(selected)
    var confirmIds by remember { mutableStateOf<List<UUID>?>(null) }
    var results by remember { mutableStateOf<List<MessageBatchResult>?>(null) }
    fun notice(resource: Int) = Toast.makeText(context, context.getString(resource), Toast.LENGTH_SHORT).show()
    androidx.activity.compose.BackHandler { if (!busy && confirmIds == null && results == null) leave() }
    Surface(color = DeviceColors.Surface) {
        Column(Modifier.fillMaxWidth().navigationBarsPadding().padding(horizontal = 12.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly) {
                TextButton(enabled = !busy && selected.isNotEmpty(), onClick = {
                    runCatching { context.getSystemService(ClipboardManager::class.java).setPrimaryClip(
                        ClipData.newPlainText("BlueLink", MessageBatch.copyText(selected))) }
                        .onSuccess { notice(R.string.message_batch_copied) }.onFailure { notice(R.string.message_batch_copy_failed) }
                }) { BatchActionLabel(R.drawable.figma_action_copy, context.getString(R.string.message_batch_copy)) }
                TextButton(enabled = !busy && selected.isNotEmpty(), onClick = {
                    if (MessageShareContent.fileCount(selected) > FileBatchPolicy.MAXIMUM_SELECTION) notice(R.string.message_batch_share_limit)
                    else if (files.any { !it.canOpen || it.isTransferActive || it.recoveryPending }) notice(R.string.batch_unreadable)
                    else {
                        setBusy(true)
                        scope.launch {
                            try {
                                val intent = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                                    FileInteraction.messageShareIntent(context, selected)
                                }
                                context.startActivity(Intent.createChooser(intent, null))
                            } catch (cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
                            catch (_: Exception) { notice(R.string.content_share_failed) }
                            finally { setBusy(false) }
                        }
                    }
                }) { BatchActionLabel(R.drawable.ic_file_share, context.getString(R.string.content_share)) }
                TextButton(enabled = !busy && selected.isNotEmpty(), onClick = { confirmIds = selected.map { it.id } },
                    colors = ButtonDefaults.textButtonColors(contentColor = DeviceColors.Error)) {
                    BatchActionLabel(R.drawable.figma_action_delete, context.getString(R.string.content_delete))
                }
            }
        }
    }
    confirmIds?.let { snapshot ->
        BlueLinkPrompt(context.getString(R.string.content_delete_record), { if (!busy) confirmIds = null }, footer = {
            TextButton(enabled = !busy, onClick = { confirmIds = null }) { Text(context.getString(R.string.cancel)) }
            Button(enabled = !busy, colors = ButtonDefaults.buttonColors(containerColor = DeviceColors.Error), onClick = {
                setBusy(true)
                scope.launch {
                    try {
                        val outcome = delete(snapshot)
                        select(ids.filter { id -> outcome.none { it.deleted && it.id.toString() == id } })
                        confirmIds = null
                        if (outcome.any { !it.deleted }) results = outcome
                        else Toast.makeText(context, context.getString(R.string.message_batch_summary, outcome.size, 0), Toast.LENGTH_SHORT).show()
                    } catch (cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
                    catch (_: Exception) { notice(R.string.batch_try_again) }
                    finally { setBusy(false) }
                }
            }) { Text(context.getString(R.string.content_delete)) }
        }) {
            Text(context.getString(R.string.message_batch_delete_confirm, snapshot.size))
            Text(context.getString(R.string.message_batch_delete_note))
        }
    }
    results?.let { entries -> BlueLinkPrompt(context.getString(R.string.batch_result), { results = null }) {
        Text(context.getString(R.string.message_batch_summary, entries.count { it.deleted }, entries.count { !it.deleted }))
        if (entries.any { it.reason == MessageBatchReason.ACTIVE }) Text(context.getString(R.string.message_batch_active))
        if (entries.any { it.reason == MessageBatchReason.MISSING }) Text(context.getString(R.string.batch_missing))
        if (entries.any { it.reason == MessageBatchReason.FAILED }) Text(context.getString(R.string.batch_try_again))
    } }
}

@Composable
private fun BatchActionLabel(icon: Int, label: String) {
    Column(Modifier.padding(vertical = 4.dp), horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(3.dp)) {
        com.bluelink.android.ui.devices.FigmaIcon(icon, size = 22.dp, tint = LocalContentColor.current)
        Text(label, style = MaterialTheme.typography.labelMedium)
    }
}
