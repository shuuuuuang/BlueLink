package com.bluelink.android.ui.files

import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.ui.components.ActionOption
import com.bluelink.android.ui.components.ActionSheet
import com.bluelink.android.ui.content.contentBytes
import com.bluelink.android.ui.content.contentStatus
import com.bluelink.android.ui.devices.DeviceColors

@Composable
internal fun TransferActionSheet(transfer: TransferItem, online: Boolean, dismiss: () -> Unit,
                                 messageContext: Boolean = false, allowDelete: Boolean = true,
                                 selectMultiple: (() -> Unit)? = null,
                                 onAction: (TransferAction) -> Unit) {
    val context = LocalContext.current
    val base = transferActionOptions(context, transfer, online, messageContext, allowDelete, onAction)
    val selection = selectMultiple?.let { ActionOption(context.getString(R.string.batch_select), R.drawable.ic_message_multiselect, action = it) }
    val options = buildList {
        // The shared list starts with Copy filename, followed by the available transfer actions.
        val actions = base.drop(1)
        val hasOpen = TransferAction.OPEN in TransferActions.available(transfer)
        if (hasOpen) add(actions.first())
        add(base.first()) // Keep copying the name available even when the local file is missing.
        selection?.let(::add)
        addAll(actions.drop(if (hasOpen) 1 else 0).map { option ->
            if (option.icon == R.drawable.figma_action_delete)
                option.copy(label = context.getString(R.string.content_delete_local_message))
            else option
        })
    }
    ActionSheet(transfer.name, "${contentStatus(transfer, context)} · ${contentBytes(transfer.completedBytes)} / ${contentBytes(transfer.totalBytes)}",
        options, dismiss, extraContent = if (!online && transfer.status in HistoryQuery.activeStatuses) ({
            Text(context.getString(R.string.content_transfer_offline), Modifier.padding(horizontal = 24.dp, vertical = 8.dp),
                color = DeviceColors.Secondary, style = MaterialTheme.typography.bodySmall)
        }) else null)
}

/** Shared action availability and labels for file cards and search results. */
internal fun transferActionOptions(context: android.content.Context, transfer: TransferItem, online: Boolean,
                                   messageContext: Boolean, allowDelete: Boolean,
                                   onAction: (TransferAction) -> Unit): List<ActionOption> {
    return listOf(copyFileNameOption(context, transfer.name)) + TransferActions.available(transfer).filter { allowDelete || it != TransferAction.DELETE }.map { action ->
        val callback = { onAction(action) }
        when (action) {
            TransferAction.OPEN -> ActionOption(if (transfer.mimeType.startsWith("image/")) context.getString(R.string.content_preview_image) else context.getString(R.string.content_open), R.drawable.ic_file_open, action = callback)
            TransferAction.SHARE -> ActionOption(context.getString(R.string.content_share), R.drawable.ic_file_share, action = callback)
            TransferAction.SAVE -> ActionOption(context.getString(R.string.content_save_as), R.drawable.figma_action_save, action = callback)
            TransferAction.DETAILS -> ActionOption(context.getString(R.string.content_view_details), R.drawable.figma_action_info, action = callback)
            TransferAction.PAUSE -> ActionOption(context.getString(if (transfer.outgoing) R.string.content_pause_sending else R.string.content_pause_receiving), R.drawable.figma_action_pause, enabled = online, action = callback)
            TransferAction.RESUME -> ActionOption(context.getString(if (transfer.outgoing) R.string.content_resume_sending else R.string.content_resume_receiving), R.drawable.figma_action_resume, accent = true, enabled = online, action = callback)
            TransferAction.RETRY -> ActionOption(context.getString(if (transfer.recoveryPending) R.string.transfer_recovery_action else R.string.content_retry_transfer), R.drawable.ic_file_retry, accent = true, enabled = online, action = callback)
            TransferAction.BLUETOOTH -> ActionOption(context.getString(R.string.transfer_use_bluetooth), R.drawable.figma_action_resume, enabled = online, action = callback)
            TransferAction.RESELECT -> ActionOption(context.getString(R.string.transfer_reselect_source), R.drawable.ic_file_open, enabled = online, action = callback)
            TransferAction.FAILURE -> ActionOption(context.getString(if (transfer.recoveryPending) R.string.transfer_recovery_details else R.string.content_view_failure), R.drawable.figma_dialog_warning, destructive = !transfer.recoveryPending, action = callback)
            TransferAction.CANCEL -> ActionOption(context.getString(if (transfer.outgoing) R.string.content_cancel_sending else R.string.content_cancel_receiving), R.drawable.figma_action_cancel, destructive = true, enabled = online, action = callback)
            TransferAction.DELETE -> ActionOption(context.getString(if (messageContext) R.string.content_delete_local_message else R.string.content_delete_record), R.drawable.figma_action_delete, destructive = true, action = callback)
        }
    }
}

internal fun copyFileNameOption(context: android.content.Context, fileName: String) =
    ActionOption(context.getString(R.string.content_copy_filename), R.drawable.figma_action_copy) {
        com.bluelink.android.files.FileInteraction.copyFileName(context, fileName)
    }
