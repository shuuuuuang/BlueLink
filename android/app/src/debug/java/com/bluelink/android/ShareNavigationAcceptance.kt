package com.bluelink.android

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.sharing.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import java.io.File
import java.util.UUID

/** Actual receive Activity + actual main navigation; QA sender never contacts a device. */
@Composable internal fun ShareNavigationAcceptance(close: () -> Unit) {
    val context = LocalContext.current
    val app = context.applicationContext as BlueLinkApplication
    var running by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf("QA navigation ready") }
    Column(Modifier.fillMaxSize().statusBarsPadding()) {
        Text(status)
        TextButton(enabled = !running, onClick = {
            running = true
            app.shareScope.launch {
                val result = File(context.cacheDir, "share-navigation-result.txt")
                var requestId: UUID? = null
                try {
                    val peer = withTimeout(10000) { app.runtime.conversations.first { items -> items.any { it.isTrusted && !it.isRemoved } } }
                        .first { it.isTrusted && !it.isRemoved }
                    val incoming = Intent(Intent.ACTION_SEND_MULTIPLE).setType("application/pdf")
                        .putExtra(Intent.EXTRA_TEXT, "QA navigation " + UUID.randomUUID())
                        .putParcelableArrayListExtra(Intent.EXTRA_STREAM, arrayListOf(
                            Uri.parse("content://${context.packageName}.qa-share-input/one"),
                            Uri.parse("content://${context.packageName}.qa-share-input/two")))
                    val id = app.shareInbox.capture(incoming); requestId = id
                    app.shareInbox.setTarget(id, peer.peerId)
                    val files = app.shareInbox.requests.value.first { it.id == id }.files
                    var failSecond = true
                    val sender = object : ShareInbox.Sender {
                        override suspend fun text(peerId: String, id: UUID, text: String) = true
                        override suspend fun file(peerId: String, id: UUID, source: Uri, name: String, size: Long) =
                            !(failSecond && id == files.last().id)
                    }
                    result.writeText("READY\n${peer.displayName}\n${peer.peerId}")
                    withContext(Dispatchers.Main) {
                        context.startActivity(Intent(incoming).setClass(context, IncomingShareActivity::class.java)
                            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))
                    }
                    delay(12000)
                    check(!app.shareInbox.send(id, peer.peerId, sender))
                    check(app.shareInbox.requests.value.first { it.id == id }.completedPeerId == null)
                    result.writeText("PARTIAL\n${peer.displayName}\n${peer.peerId}")
                    delay(16000)
                    failSecond = false
                    check(app.shareInbox.send(id, peer.peerId, sender))
                    result.writeText("COMPLETE\n${peer.displayName}\n${peer.peerId}")
                    delay(12000)
                    result.appendText("\nPASS partial stayed; whole request completed without real sends")
                } catch (error: Exception) {
                    result.writeText("FAILED: $error")
                } finally {
                    requestId?.let { app.shareInbox.discard(it) }
                    withContext(Dispatchers.Main) { running = false; status = "QA navigation finished" }
                }
            }
        }) { Text("QA Test share navigation") }
        TextButton(onClick = close) { Text("QA Close") }
    }
}
