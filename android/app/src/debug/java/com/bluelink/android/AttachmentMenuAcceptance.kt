package com.bluelink.android

import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.bluelink.android.domain.*
import com.bluelink.android.ui.components.BlueLinkConfirmation
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.devices.*
import com.bluelink.android.ui.files.*
import java.io.File
import java.util.UUID
import com.bluelink.android.session.TransferPauseController

/** In-memory protocol states; actions cannot affect a real session, file, clipboard or app. */
@Composable
internal fun AttachmentMenuAcceptance(scene: String, image: File, close: () -> Unit) {
    val context = LocalContext.current
    val fileContext = scene.startsWith("file-actions-")
    val kind = scene.removePrefix("attachment-actions-").removePrefix("file-actions-")
    val peer = remember { ConversationSummary("qa-menu-peer", "BlueLink QA PC", PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED) }
    var transfer by remember {
        val outgoing = kind in setOf("send", "send-paused", "send-failed", "success")
        val status = when (kind) {
            "send", "receive" -> TransferStatus.TRANSFERRING
            "send-paused", "receive-paused" -> TransferStatus.PAUSED
            "waiting" -> TransferStatus.REMOTE_PAUSED
            "resuming" -> TransferStatus.RESUMING
            "send-failed", "receive-failed" -> TransferStatus.FAILED
            else -> TransferStatus.COMPLETED
        }
        mutableStateOf(TransferItem(UUID.randomUUID(), if (kind == "image") "BlueLink-QA.png" else "BlueLink-QA.pdf",
            1_000_000, if (status == TransferStatus.COMPLETED) 1_000_000 else 310_000, outgoing, status,
            messageId = UUID.randomUUID(), peerId = peer.peerId,
            mimeType = if (kind == "image") "image/png" else "application/pdf",
            localUri = if (status == TransferStatus.COMPLETED || outgoing) Uri.fromFile(image).toString() else null,
            failureDetail = if (status == TransferStatus.FAILED) "QA: isolated transfer failure" else null))
    }
    val controller = remember {
        val initialStatus = transfer.status
        TransferPauseController { transfer = it }.also {
            it.report(transfer.copy(status = if (initialStatus in setOf(TransferStatus.PAUSED, TransferStatus.REMOTE_PAUSED))
                TransferStatus.TRANSFERRING else initialStatus))
            if (initialStatus == TransferStatus.PAUSED) it.setPaused(local = true, paused = true)
            if (initialStatus == TransferStatus.REMOTE_PAUSED) it.setPaused(local = false, paused = true)
        }
    }
    var menu by remember { mutableStateOf(true) }
    var details by remember { mutableStateOf(false) }
    var failure by remember { mutableStateOf(false) }
    var confirmation by remember { mutableStateOf<TransferAction?>(null) }
    var deleted by remember { mutableStateOf(false) }
    val attachment = ChatAttachment(transfer.id, transfer.id, transfer.name, transfer.mimeType, transfer.totalBytes,
        transfer.localUri, transfer.status.name, completedBytes = transfer.completedBytes)
    val messages = if (deleted) emptyList() else listOf(ChatItem(transfer.messageId!!, "", transfer.outgoing,
        status = if (transfer.outgoing) MessageStatus.SENT else MessageStatus.RECEIVED,
        kind = if (kind == "image") ChatItemKind.IMAGE else ChatItemKind.FILE, attachments = listOf(attachment)))
    Column(Modifier.fillMaxSize().background(DeviceColors.Canvas)) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp)) {
            Text("BlueLink · QA", Modifier.weight(1f))
            if (kind == "waiting") TextButton(onClick = {
                controller.setPaused(local = false, paused = transfer.status != TransferStatus.REMOTE_PAUSED)
            }) { Text("QA peer pause/resume") }
            TextButton(onClick = close) { Text(context.getString(R.string.close)) }
        }
        ConversationScreen(Modifier.weight(1f), peer.peerId, messages, ConnectionState(ConnectionPhase.CONNECTED),
            listOf(peer), listOf(transfer), true, "downloads://BlueLink", close, {}, {}, {}, {}, {},
            { menu = true }, { menu = true }, { if (fileContext) menu = true }, { menu = true },
            requestedTab = if (fileContext) 1 else null)
    }
    if (menu) TransferActionSheet(transfer, true, { menu = false }, messageContext = !fileContext) { action ->
        when (action) {
            TransferAction.PAUSE -> controller.setPaused(local = true, paused = true)
            TransferAction.RESUME -> { controller.setPaused(local = true, paused = false); controller.report(transfer.copy(status = TransferStatus.TRANSFERRING)) }
            TransferAction.DETAILS -> details = true
            TransferAction.FAILURE -> failure = true
            TransferAction.CANCEL, TransferAction.DELETE -> confirmation = action
            else -> Unit // External file actions deliberately do not execute in this scene.
        }
    }
    if (details) FileDetailsPrompt(attachment, transfer, peer.peerName, dismiss = { details = false })
    if (failure) TransferFailurePrompt(transfer, dismiss = { failure = false })
    confirmation?.let { action ->
        val cancel = action == TransferAction.CANCEL
        val title = context.getString(if (cancel) {
            if (transfer.outgoing) R.string.content_cancel_sending else R.string.content_cancel_receiving
        } else if (fileContext) R.string.content_delete_record else R.string.content_delete_local_message)
        val body = if (cancel) context.getString(if (transfer.outgoing) R.string.content_stop_sending else R.string.content_stop_receiving, transfer.name)
            else context.getString(if (fileContext) R.string.content_delete_transfer_body else R.string.content_delete_file_message_body)
        BlueLinkConfirmation(title, body, if (cancel) context.getString(R.string.content_keep_files) else null,
            if (cancel) title else context.getString(R.string.content_delete), { confirmation = null }) {
            confirmation = null
            if (cancel) controller.report(transfer.copy(status = TransferStatus.CANCELED)) else deleted = true
        }
    }
}
