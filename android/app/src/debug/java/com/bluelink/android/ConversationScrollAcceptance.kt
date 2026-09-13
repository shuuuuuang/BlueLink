package com.bluelink.android

import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.bluelink.android.domain.*
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.conversation.ImagePreview
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.io.File
import java.util.UUID

@Composable
internal fun ConversationScrollAcceptance(image: File, close: () -> Unit) {
    val peer = remember { ConversationSummary("qa-scroll", "BlueLink QA Scroll", PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED) }
    var messages by remember { mutableStateOf(List(24) { ChatItem(text = "QA history $it", outgoing = it % 2 == 0, status = MessageStatus.RECEIVED) }) }
    var preview by remember { mutableStateOf<ChatAttachment?>(null) }
    val state = rememberSaveableStateHolder()
    val scope = rememberCoroutineScope()
    var sequence by remember { mutableIntStateOf(0) }
    fun append(outgoing: Boolean, count: Int = 1) {
        sequence++
        val number = sequence
        val attachments = List(count) { index -> ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), "QA-image-$number-$index.png",
            "image/png", image.length(), state = "TRANSFERRING") }
        val message = ChatItem(text = "", outgoing = outgoing, status = MessageStatus.RECEIVED, kind = ChatItemKind.IMAGE, attachments = attachments)
        messages = messages + message
        scope.launch {
            delay(1400)
            messages = messages.map { if (it.id != message.id) it else it.copy(attachments = attachments.map { attachment ->
                attachment.copy(localUri = Uri.fromFile(image).toString(), state = "COMPLETED")
            }) }
        }
    }
    if (preview == null) state.SaveableStateProvider("main") {
        Column(Modifier.fillMaxSize()) {
            Row {
                TextButton(onClick = { append(false) }) { Text("接收图片") }
                TextButton(onClick = { append(true) }) { Text("发送图片") }
                TextButton(onClick = { append(false, 6) }) { Text("多图消息") }
            }
            ConversationScreen(Modifier.weight(1f), peer.peerId, messages, ConnectionState(ConnectionPhase.CONNECTED),
                listOf(peer), emptyList(), true, "downloads://BlueLink", close, {}, {}, {}, {}, {}, {},
                openAttachment = { preview = it }, openTransfer = {}, moreTransfer = {})
        }
    }
    preview?.let { ImagePreview(it, dismiss = { preview = null }) }
}
