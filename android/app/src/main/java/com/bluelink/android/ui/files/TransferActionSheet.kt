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
                                 onAction: (TransferAction) -> Unit) {
    val context = LocalContext.current
    val options = TransferActions.available(transfer).filter { allowDelete || it != TransferAction.DELETE }.map { action ->
        val callback = { onAction(action) }
        when (action) {
            TransferAction.OPEN -> ActionOption(if (transfer.mimeType.startsWith("image/")) context.getString(R.string.content_preview_image) else context.getString(R.string.content_open), R.drawable.figma_action_open, action = callback)
            TransferAction.COPY -> ActionOption(context.getString(R.string.content_copy_file), R.drawable.figma_action_copy, action = callback)
            TransferAction.REVEAL -> ActionOption(context.getString(R.string.content_show_folder), R.drawable.figma_action_folder, action = callback)
            TransferAction.SAVE -> ActionOption(context.getString(R.string.content_save_as), R.drawable.figma_action_save, action = callback)
            TransferAction.DETAILS -> ActionOption(context.getString(R.string.content_view_details), R.drawable.figma_action_info, action = callback)
            TransferAction.PAUSE -> ActionOption(context.getString(if (transfer.outgoing) R.string.content_pause_sending else R.string.content_pause_receiving), R.drawable.figma_action_pause, enabled = online, action = callback)
            TransferAction.RESUME -> ActionOption(context.getString(if (transfer.outgoing) R.string.content_resume_sending else R.string.content_resume_receiving), R.drawable.figma_action_resume, accent = true, enabled = online, action = callback)
            TransferAction.RETRY -> ActionOption(context.getString(R.string.content_retry_transfer), R.drawable.figma_action_resume, accent = true, enabled = online, action = callback)
            TransferAction.FAILURE -> ActionOption(context.getString(R.string.content_view_failure), R.drawable.figma_dialog_warning, destructive = true, action = callback)
            TransferAction.CANCEL -> ActionOption(context.getString(if (transfer.outgoing) R.string.content_cancel_sending else R.string.content_cancel_receiving), R.drawable.figma_action_cancel, destructive = true, enabled = online, action = callback)
            TransferAction.DELETE -> ActionOption(context.getString(if (messageContext) R.string.content_delete_local_message else R.string.content_delete_record), R.drawable.figma_action_delete, destructive = true, action = callback)
        }
    }
    ActionSheet(transfer.name, "${contentStatus(transfer.status, context, transfer.outgoing)} · ${contentBytes(transfer.completedBytes)} / ${contentBytes(transfer.totalBytes)}",
        options, dismiss, extraContent = if (!online && transfer.status in HistoryQuery.activeStatuses) ({
            Text(context.getString(R.string.content_transfer_offline), Modifier.padding(horizontal = 24.dp, vertical = 8.dp),
                color = DeviceColors.Secondary, style = MaterialTheme.typography.bodySmall)
        }) else null)
}
