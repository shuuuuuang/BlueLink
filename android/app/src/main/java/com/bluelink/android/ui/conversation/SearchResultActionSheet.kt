package com.bluelink.android.ui.conversation

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.widget.Toast
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.ui.components.ActionOption
import com.bluelink.android.ui.components.ActionSheet
import com.bluelink.android.ui.content.contentBytes
import com.bluelink.android.ui.content.contentStatus
import com.bluelink.android.ui.files.transferActionOptions
import java.util.UUID

@Composable
internal fun SearchResultActionSheet(message: ChatItem, attachment: ChatAttachment?, transfers: List<TransferItem>,
                                     online: Boolean, dismiss: () -> Unit, locate: () -> Unit,
                                     onTransferAction: ((TransferItem, TransferAction, UUID) -> Unit)?,
                                     deleteMessage: ((ChatItem) -> Unit)?, selectMultiple: (() -> Unit)? = null) {
    val context = LocalContext.current
    fun copy(value: String) {
        context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("BlueLink", value))
    }
    val locateOption = ActionOption(context.getString(R.string.content_locate_message), R.drawable.figma_content_message, action = locate)
    val transfer = attachment?.let { SearchResultActions.transfer(message, it, transfers) }
    val selection = selectMultiple?.let { ActionOption(context.getString(R.string.batch_select), R.drawable.ic_message_multiselect, action = it) }
    val options = buildList {
        add(locateOption)
        if (attachment != null && transfer != null) {
            if (onTransferAction != null) {
                val fileOptions = transferActionOptions(context, transfer,
                    online && transfers.any { it.id == transfer.id }, messageContext = true, allowDelete = true) {
                    onTransferAction(transfer, it, message.id)
                }
                val actions = fileOptions.drop(1)
                val hasOpen = TransferAction.OPEN in TransferActions.available(transfer)
                if (hasOpen) add(actions.first())
                add(fileOptions.first())
                selection?.let(::add)
                addAll(actions.drop(if (hasOpen) 1 else 0))
            } else {
                add(com.bluelink.android.ui.files.copyFileNameOption(context, attachment.fileName))
                selection?.let(::add)
            }
        } else {
            if (message.text.isNotBlank()) add(ActionOption(context.getString(R.string.content_copy_text), R.drawable.figma_action_copy) { copy(message.text) })
            selection?.let(::add)
            if (message.text.isNotBlank()) add(ActionOption(context.getString(R.string.content_share), R.drawable.ic_file_share) {
                runCatching {
                    context.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
                        type = "text/plain"
                        putExtra(Intent.EXTRA_TEXT, message.text)
                    }, null))
                }.onFailure { Toast.makeText(context, context.getString(R.string.content_share_failed), Toast.LENGTH_SHORT).show() }
            })
            if (deleteMessage != null) add(ActionOption(context.getString(R.string.content_delete_local_message),
                R.drawable.figma_action_delete, destructive = true) { deleteMessage(message) })
        }
    }
    val detail = if (transfer != null) "${contentBytes(transfer.totalBytes)} · ${contentStatus(transfer, context)}"
        else message.text.take(160) + if (message.text.length > 160) "…" else ""
    ActionSheet(attachment?.fileName ?: context.getString(R.string.content_text_message), detail, options, dismiss)
}
