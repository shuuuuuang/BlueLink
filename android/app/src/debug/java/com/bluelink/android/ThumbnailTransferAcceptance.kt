package com.bluelink.android

import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.bluelink.android.domain.*
import com.bluelink.android.ui.conversation.ConversationScreen
import java.io.File
import java.util.UUID

/** Physical-device rendering of production conversation components, with no database writes. */
@Composable
internal fun ThumbnailTransferAcceptance(image: File, close: () -> Unit) {
    var enabled by remember { mutableStateOf(true) }
    var paused by remember { mutableStateOf(false) }
    val peer = remember { ConversationSummary("qa-preview", "BlueLink QA", PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED) }
    val base = remember { ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), "bluelink-preview.png", "image/png", 1_000_000,
        state = "TRANSFERRING", previewUri = Uri.fromFile(image).toString(), completedBytes = 400_000) }
    val messages = listOf(ChatItem(text = "", outgoing = false, status = MessageStatus.RECEIVED, kind = ChatItemKind.IMAGE,
        attachments = listOf(base.copy(state = if (paused) "REMOTE_PAUSED" else "TRANSFERRING"))),
        ChatItem(text = "", outgoing = true, status = MessageStatus.SENT, kind = ChatItemKind.IMAGE,
            attachments = listOf(base.copy(transferId = UUID(0, 2), state = "COMPLETED", localUri = Uri.fromFile(image).toString()))))
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(8.dp)) {
            TextButton(onClick = { enabled = !enabled }) { Text(if (enabled) "关闭缩略图" else "开启缩略图") }
            TextButton(onClick = { paused = !paused }) { Text(if (paused) "对端继续" else "对端暂停") }
        }
        ConversationScreen(Modifier.weight(1f), peer.peerId, messages, ConnectionState(ConnectionPhase.CONNECTED),
            listOf(peer), emptyList(), enabled, "downloads://BlueLink", close, {}, {}, {}, {}, {}, {}, {}, {}, {})
    }
}
