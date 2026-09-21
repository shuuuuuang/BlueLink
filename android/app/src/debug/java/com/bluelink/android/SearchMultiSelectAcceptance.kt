package com.bluelink.android

import android.content.ClipboardManager
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.domain.*
import com.bluelink.android.ui.conversation.MessageSearchScreen
import java.io.File
import java.time.Instant
import java.util.UUID

/** Isolated search-history fixture; no real message deletion or network sends. */
@Composable
internal fun SearchMultiSelectAcceptance(image: File, close: () -> Unit) {
    val context = LocalContext.current
    val document = remember { File(image.parentFile, "search-batch-QA.txt").apply { writeText("search batch QA file") } }
    val rows = remember { (0..23).map { index ->
        val attachment = when (index) {
            0 -> ChatAttachment(UUID(1, 1), UUID(2, 1), "needle-QA-image.png", "image/png", image.length(), Uri.fromFile(image).toString(), "COMPLETED")
            1 -> ChatAttachment(UUID(1, 2), UUID(2, 2), "needle-QA-file.txt", "text/plain", document.length(), Uri.fromFile(document).toString(), "COMPLETED")
            else -> null
        }
        ChatItem(id = UUID(0, index.toLong() + 1), text = if (attachment != null) "" else "needle QA text ${index.toString().padStart(2, '0')}",
            outgoing = false, status = MessageStatus.RECEIVED, timestamp = Instant.now().minusSeconds(index * 3600L),
            kind = if (index == 0) ChatItemKind.IMAGE else if (index == 1) ChatItemKind.FILE else ChatItemKind.TEXT,
            attachments = listOfNotNull(attachment))
    } }
    var deleted by remember { mutableStateOf(setOf<UUID>()) }
    var feedback by remember { mutableStateOf("QA ready") }
    var copied by remember { mutableStateOf("") }
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        Row {
            TextButton(onClick = close) { Text("QA Close") }
            TextButton(onClick = {
                val value = context.getSystemService(ClipboardManager::class.java).primaryClip?.getItemAt(0)?.text?.toString().orEmpty()
                copied = value
                feedback = if (value.contains("needle QA text") || value.contains("needle-QA-")) "PASS copy" else "FAIL copy"
                File(context.cacheDir, "search-batch-copy.txt").writeText(value)
            }) { Text("QA Copy check") }
        }
        Text(feedback)
        MessageSearchScreen(rows.take(2).filterNot { it.id in deleted }, "Search QA", true, close, { feedback = "PASS locate ${it.id}" }, Modifier.weight(1f),
            loadHistory = { rows }, // Deliberately stale: successful deletion must still disappear.
            deleteMessage = { deleted = deleted + it.id },
            onTransferAction = { _, action, _ -> feedback = "QA $action" },
            deleteSelectedMessages = { ids ->
                MessageBatch.delete(ids, { id -> rows.firstOrNull { it.id == id && id !in deleted } }, { false }) { id -> deleted = deleted + id }
            })
    }
}
