package com.bluelink.android

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.FileProvider
import com.bluelink.android.domain.*
import com.bluelink.android.files.FileInteraction
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.conversation.ImagePreview
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.util.UUID

/** Distinct original/thumbnail pixels exercise the real decoder and preview on a physical device. */
@Composable
internal fun ImagePreviewOriginalAcceptance(close: () -> Unit) {
    val context = LocalContext.current
    var result by remember { mutableStateOf("QA loading") }
    val fixtures by produceState<List<ChatAttachment>>(emptyList()) {
        value = withContext(Dispatchers.IO) {
            runCatching {
                val folder = File(context.cacheDir, "shared/qa-image-preview").apply { mkdirs() }
                fun image(name: String, width: Int, height: Int, color: Int): File {
                    val file = File(folder, name)
                    val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.RGB_565)
                    try {
                        val canvas = Canvas(bitmap)
                        canvas.drawColor(color)
                        val paint = Paint().apply { this.color = Color.BLACK; textSize = 72f }
                        canvas.drawText("ORIGINAL $width x $height", 60f, 160f, paint)
                        paint.strokeWidth = 2f
                        for (x in 500 until minOf(width, 1800) step 12) canvas.drawLine(x.toFloat(), 500f, x.toFloat(), 1100f, paint)
                        file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
                    } finally { bitmap.recycle() }
                    return file
                }
                val original = image("original.png", 2400, 1600, Color.GREEN)
                val thumbnail = image("thumbnail.png", 160, 90, Color.MAGENTA)
                val large = image("large.png", 8192, 2048, Color.GREEN)
                fun attachment(file: File, uri: String = Uri.fromFile(file).toString()) = ChatAttachment(
                    UUID.randomUUID(), UUID.randomUUID(), file.name, "image/png", file.length(),
                    localUri = uri, state = "COMPLETED", previewUri = Uri.fromFile(thumbnail).toString())
                val file = attachment(original)
                val content = file.copy(attachmentId = UUID.randomUUID(), fileName = "content-original.png",
                    localUri = FileProvider.getUriForFile(context, "${context.packageName}.files", original).toString())
                val missing = file.copy(attachmentId = UUID.randomUUID(), fileName = "missing-original.png",
                    localUri = Uri.fromFile(File(folder, "absent.png")).toString())
                val big = attachment(large)
                var checks = 0
                // Warm the existing thumbnail cache first: it must not supply full-screen pixels.
                val small = requireNotNull(FileInteraction.loadBitmap(context, file, 4096))
                check(small.width == 160 && small.height == 90); small.recycle(); checks++
                for (source in listOf(file, content)) {
                    val decoded = requireNotNull(FileInteraction.loadPreviewBitmap(context, source))
                    try {
                        check(decoded.width == 2400 && decoded.height == 1600)
                        check(decoded.getPixel(350, 450) == Color.GREEN)
                    } finally { decoded.recycle() }
                    checks++
                }
                check(FileInteraction.loadPreviewBitmap(context, missing) == null); checks++
                check(FileInteraction.loadPreviewBitmap(context, file.copy(state = "TRANSFERRING")) == null); checks++
                val corruptFile = File(folder, "corrupt.png").apply { writeText("not an image") }
                check(FileInteraction.loadPreviewBitmap(context, attachment(corruptFile)) == null); checks++
                val decoded = requireNotNull(FileInteraction.loadPreviewBitmap(context, big))
                try {
                    check(decoded.width == 4096 && decoded.height == 1024)
                    check(decoded.allocationByteCount <= 32 * 1024 * 1024)
                } finally { decoded.recycle() }
                checks++
                val replacement = image("replacement.png", 600, 400, Color.RED)
                val replaced = attachment(replacement)
                FileInteraction.loadPreviewBitmap(context, replaced)?.recycle()
                image("replacement.png", 600, 400, Color.BLUE)
                val fresh = requireNotNull(FileInteraction.loadPreviewBitmap(context, replaced))
                try { check(fresh.getPixel(350, 350) == Color.BLUE) } finally { fresh.recycle() }
                checks++
                result = "QA decoder PASS $checks"
                listOf(file, content, missing, big)
            }.getOrElse { result = "QA FAIL ${it.message}"; emptyList() }
        }
    }
    var selected by remember { mutableIntStateOf(0) }
    var preview by remember { mutableStateOf<ChatAttachment?>(null) }
    val peer = remember { ConversationSummary("qa-preview", "QA original preview", PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED) }
    if (preview != null) {
        ImagePreview(requireNotNull(preview), dismiss = { preview = null })
    } else Column(Modifier.fillMaxSize()) {
        Text(result)
        Row {
            listOf("文件原图", "内容原图", "原图丢失", "大图").forEachIndexed { index, label ->
                TextButton(onClick = { selected = index }) { Text(label) }
            }
        }
        fixtures.getOrNull(selected)?.let { attachment ->
            val message = remember(attachment) { ChatItem(text = "", outgoing = false,
                status = MessageStatus.RECEIVED, kind = ChatItemKind.IMAGE, attachments = listOf(attachment)) }
            ConversationScreen(Modifier.weight(1f), peer.peerId, listOf(message), ConnectionState(ConnectionPhase.CONNECTED),
                listOf(peer), emptyList(), true, "downloads://BlueLink", close, {}, {}, {}, {}, {}, {},
                openAttachment = { preview = it }, openTransfer = {}, moreTransfer = {})
        }
    }
}
