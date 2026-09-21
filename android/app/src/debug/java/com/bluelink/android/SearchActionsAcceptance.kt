package com.bluelink.android

import android.content.ClipboardManager
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.domain.*
import com.bluelink.android.files.FileInteraction
import com.bluelink.android.ui.components.BlueLinkConfirmation
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.conversation.ImagePreview
import com.bluelink.android.ui.files.FileDetailsPrompt
import java.io.File
import java.util.UUID

/** In-memory search records and generated file only; never changes user history or sends content. */
@Composable
internal fun SearchActionsAcceptance(image: File, close: () -> Unit) {
    val context = LocalContext.current
    val text = remember { "needle QA 完整复制\n" + "蓝联 Unicode 123 🌍 ".repeat(30) + "\nEND exact whitespace  " }
    val file = remember { ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), "needle-QA.png", "image/png",
        image.length(), Uri.fromFile(image).toString(), "COMPLETED", completedBytes = image.length()) }
    val missing = remember { file.copy(attachmentId = UUID.randomUUID(), transferId = UUID.randomUUID(), fileName = "missing-QA.png", localUri = null) }
    val paused = remember { missing.copy(attachmentId = UUID.randomUUID(), transferId = UUID.randomUUID(), fileName = "paused-QA.png", state = "PAUSED") }
    val peer = remember { ConversationSummary("search-qa", "BlueLink Search QA", PeerPlatform.WINDOWS, DeviceAvailability.OFFLINE) }
    var messages by remember { mutableStateOf(listOf(
        ChatItem(text = text, outgoing = false, status = MessageStatus.RECEIVED),
        ChatItem(text = "", outgoing = false, status = MessageStatus.RECEIVED, kind = ChatItemKind.IMAGE,
            attachments = listOf(file.copy(attachmentId = UUID.randomUUID(), transferId = UUID.randomUUID(), fileName = "first-QA.png"), file)),
        ChatItem(text = "", outgoing = false, status = MessageStatus.RECEIVED, kind = ChatItemKind.IMAGE, attachments = listOf(missing)),
        ChatItem(text = "", outgoing = false, status = MessageStatus.RECEIVED, kind = ChatItemKind.IMAGE, attachments = listOf(paused))
    )) }
    var searchRequested by remember { mutableStateOf(true) }
    var result by remember { mutableStateOf("QA ready") }
    var deletion by remember { mutableStateOf<ChatItem?>(null) }
    var preview by remember { mutableStateOf<ChatAttachment?>(null) }
    var details by remember { mutableStateOf<TransferItem?>(null) }
    val stateHolder = rememberSaveableStateHolder()
    val activePreview = preview
    if (activePreview != null) ImagePreview(activePreview, dismiss = { preview = null })
    else stateHolder.SaveableStateProvider("search") {
        Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
            Row {
                TextButton(onClick = { searchRequested = true }) { Text("QA Search") }
                TextButton(onClick = {
                    val clip = context.getSystemService(ClipboardManager::class.java).primaryClip
                    result = when {
                        clip?.description?.label != "BlueLink" -> "FAILED: clipboard label"
                        clip.getItemAt(0).text?.toString() == text -> "PASSED: full text including whitespace"
                        clip.getItemAt(0).text?.toString() == file.fileName -> "PASSED: matching attachment filename"
                        else -> "FAILED: copied value differs"
                    }
                }) { Text("QA Check copy") }
                TextButton(onClick = close) { Text("QA Close") }
            }
            Text(result)
            ConversationScreen(Modifier.weight(1f), peer.peerId, messages, ConnectionState(ConnectionPhase.OFFLINE),
                listOf(peer), emptyList(), true, "downloads://BlueLink", close, {}, {}, {}, {}, {}, {}, {}, {}, {},
                searchRequested = searchRequested, searchRequestHandled = { searchRequested = false },
                deleteSearchMessage = { deletion = it },
                searchTransferAction = { transfer, action, messageId ->
                    val attachment = messages.first { it.id == messageId }.attachments.first { it.transferId == transfer.id }
                    when (action) {
                        TransferAction.OPEN -> preview = attachment
                        TransferAction.SHARE -> FileInteraction.share(context, attachment)
                        TransferAction.DETAILS -> details = transfer
                        TransferAction.DELETE -> deletion = messages.first { it.id == messageId }
                        else -> result = "QA action: $action"
                    }
                })
        }
    }
    details?.let { transfer ->
        FileDetailsPrompt(messages.flatMap { it.attachments }.first { it.transferId == transfer.id }, transfer, peer.peerName,
            dismiss = { details = null })
    }
    deletion?.let { message ->
        BlueLinkConfirmation(context.getString(R.string.content_delete_record),
            context.getString(if (message.attachments.isEmpty()) R.string.content_delete_message_body else R.string.content_delete_file_message_body),
            null, context.getString(R.string.content_delete), { deletion = null }) {
            messages = messages.filterNot { it.id == message.id }; deletion = null; result = "PASSED: isolated record deleted"
        }
    }
}
