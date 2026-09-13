package com.bluelink.android

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.net.Uri
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.domain.*
import com.bluelink.android.ui.conversation.ConversationScreen
import java.io.File
import java.util.UUID

/** Uses production chat rendering on a physical device; only generated QA images in app cache. */
@Composable
internal fun ThumbnailGeometryAcceptance(case: String, close: () -> Unit) {
    val allowed = mapOf("small" to (24 to 32), "narrow" to (32 to 1200), "short" to (1200 to 32),
        "portrait" to (120 to 2400), "landscape" to (2400 to 120), "photo" to (800 to 600),
        "natural" to (100 to 80), "square" to (1000 to 1000))
    val (width, height) = allowed[case] ?: error("Unknown thumbnail QA case")
    val context = LocalContext.current
    val file = remember(case) {
        File(context.cacheDir, "acceptance/geometry-$case.png").also { target ->
            target.parentFile?.mkdirs()
            val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
            val canvas = Canvas(bitmap); canvas.drawColor(Color.RED)
            canvas.drawRect(width * .25f, height * .25f, width * .75f, height * .75f, Paint().apply { color = Color.rgb(0, 191, 255) })
            target.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }; bitmap.recycle()
        }
    }
    val peer = remember { ConversationSummary("qa-geometry", "QA · $width × $height", PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED) }
    val attachments = remember(file) { listOf(
        ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), file.name, "image/png", file.length(), localUri = Uri.fromFile(file).toString(), state = "COMPLETED"),
        ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), file.name, "image/png", file.length(), previewUri = Uri.fromFile(file).toString(), state = "REMOTE_PAUSED", completedBytes = 40)) }
    val messages = remember(attachments) { attachments.mapIndexed { i, attachment -> ChatItem(text = "", outgoing = i == 1,
        status = MessageStatus.RECEIVED, kind = ChatItemKind.IMAGE, attachments = listOf(attachment)) } }
    ConversationScreen(Modifier.fillMaxSize(), peer.peerId, messages, ConnectionState(ConnectionPhase.CONNECTED),
        listOf(peer), emptyList(), true, "downloads://BlueLink", close, {}, {}, {}, {}, {}, {}, {}, {}, {})
}
