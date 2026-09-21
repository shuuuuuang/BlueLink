package com.bluelink.android.ui.files

import android.content.Context
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.sp
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.ui.components.BlueLinkPrompt
import com.bluelink.android.ui.devices.DeviceColors
import kotlinx.coroutines.launch
import java.util.UUID

internal fun FileBatchAction.label(context: Context) = context.getString(when(this) {
    FileBatchAction.RETRY -> R.string.batch_retry
    FileBatchAction.CANCEL -> R.string.batch_cancel
    FileBatchAction.DELETE_RECORDS -> R.string.batch_delete
    FileBatchAction.SHARE -> R.string.content_share
})
private fun FileBatchReason?.label(context: Context) = context.getString(when(this) {
    FileBatchReason.MISSING -> R.string.batch_missing
    FileBatchReason.OFFLINE -> R.string.content_transfer_offline
    FileBatchReason.SOURCE_UNREADABLE -> R.string.batch_unreadable
    FileBatchReason.ACTIVE -> R.string.batch_active
    FileBatchReason.STATE_CHANGED -> R.string.batch_changed
    else -> R.string.batch_failed
})

@Composable
internal fun FileBatchToolbar(ids: List<String>, items: List<TransferItem>, operations: FileBatchOperations,
    busy: Boolean, setBusy: (Boolean) -> Unit, select: (List<String>) -> Unit, leave: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var preview by remember { mutableStateOf<Pair<FileBatchAction,List<FileBatchCheck>>?>(null) }
    var result by remember { mutableStateOf<Pair<FileBatchAction,List<FileBatchEntry>>?>(null) }
    var failure by remember { mutableStateOf(false) }
    androidx.activity.compose.BackHandler { if(!busy && preview == null && result == null) leave() }
    Surface(color = DeviceColors.Surface, tonalElevation = 0.dp) {
        Column(Modifier.fillMaxWidth()) {
            HorizontalDivider(color = DeviceColors.Border)
            val selectedIds = ids.toSet()
            val selected = items.filter { it.id.toString() in selectedIds }
            Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                listOf(FileBatchAction.SHARE, FileBatchAction.RETRY, FileBatchAction.CANCEL, FileBatchAction.DELETE_RECORDS).forEach { action ->
                    val label = context.getString(when (action) {
                        FileBatchAction.RETRY -> R.string.file_batch_retry_action
                        FileBatchAction.CANCEL -> R.string.file_batch_cancel_action
                        FileBatchAction.SHARE -> R.string.content_share
                        FileBatchAction.DELETE_RECORDS -> R.string.batch_delete
                    })
                    val icon = when (action) {
                        FileBatchAction.SHARE -> R.drawable.ic_file_share
                        FileBatchAction.RETRY -> R.drawable.ic_file_retry
                        FileBatchAction.CANCEL -> R.drawable.figma_action_cancel
                        FileBatchAction.DELETE_RECORDS -> R.drawable.figma_action_delete
                    }
                    TextButton(modifier = Modifier.weight(1f), contentPadding = PaddingValues(horizontal = 2.dp, vertical = 8.dp),
                        enabled = !busy && FileBatchSelection.canRequest(selected, action),
                        colors = ButtonDefaults.textButtonColors(contentColor = if (action == FileBatchAction.DELETE_RECORDS)
                            DeviceColors.Error else DeviceColors.Blue), onClick = {
                            setBusy(true)
                            scope.launch {
                                try { preview = action to operations.check(ids.map(UUID::fromString), action) }
                                catch(error: Exception) { if(error is kotlinx.coroutines.CancellationException) throw error; failure = true }
                                finally { setBusy(false) }
                            }
                        }) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(3.dp)) {
                            com.bluelink.android.ui.devices.FigmaIcon(icon, size = 22.dp, tint = LocalContentColor.current)
                            Text(label, fontSize = 12.sp, lineHeight = 16.sp, textAlign = TextAlign.Center)
                        }
                    }
                }
            }
            if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        }
    }
    preview?.let { (action, checks) ->
        val eligible = checks.count { it.eligible }
        BlueLinkPrompt(action.label(context), { if(!busy) preview = null }, footer = {
            TextButton(onClick = { preview = null }, enabled = !busy) { Text(context.getString(R.string.cancel)) }
            Button(enabled = !busy && eligible > 0, colors = ButtonDefaults.buttonColors(containerColor =
                if(action == FileBatchAction.CANCEL || action == FileBatchAction.DELETE_RECORDS) DeviceColors.Error else DeviceColors.Blue), onClick = {
                setBusy(true)
                scope.launch {
                    try {
                        val entries = operations.run(checks.map { it.id },action)
                        select(ids.filter { selected -> entries.none { it.id.toString() == selected && it.outcome == FileBatchOutcome.SUBMITTED } })
                        preview = null; result = action to entries
                    } catch(error: Exception) { if(error is kotlinx.coroutines.CancellationException) throw error; failure = true }
                    finally { setBusy(false) }
                }
            }) { Text(action.label(context)) }
        }) {
            Text(context.getString(R.string.batch_preview,eligible,checks.size - eligible))
            Text(context.getString(if(action == FileBatchAction.DELETE_RECORDS) R.string.content_delete_transfer_body else if(action == FileBatchAction.SHARE) R.string.batch_share_note else R.string.batch_dispatch_note))
            checks.forEach { Text(it.name + if(it.eligible) "" else " · " + it.reason.label(context)) }
        }
    }
    result?.let { (action, entries) -> BlueLinkPrompt(context.getString(R.string.batch_result), { result = null }) {
        Text(context.getString(R.string.batch_summary, entries.count { it.outcome == FileBatchOutcome.SUBMITTED },
            entries.count { it.outcome == FileBatchOutcome.SKIPPED }, entries.count { it.outcome == FileBatchOutcome.FAILED }))
        Text(context.getString(if(action == FileBatchAction.DELETE_RECORDS) R.string.content_delete_transfer_body else
            if(action == FileBatchAction.SHARE) R.string.batch_share_note else R.string.batch_dispatch_note))
        entries.filter { it.outcome != FileBatchOutcome.SUBMITTED }.forEach { Text(it.name + " · " + it.reason.label(context)) }
    } }
    if(failure) BlueLinkPrompt(context.getString(R.string.batch_failed), { failure = false }) { Text(context.getString(R.string.batch_try_again)) }
}
