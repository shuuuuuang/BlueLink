package com.bluelink.android.files

import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.provider.DocumentsContract
import androidx.core.content.FileProvider
import com.bluelink.android.diagnostics.CrashReporter
import com.bluelink.android.domain.ChatAttachment
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException
import java.nio.file.Path
import kotlin.io.path.deleteIfExists
import kotlin.io.path.inputStream


object FileInteraction {
    fun copyFileName(context: android.content.Context, fileName: String) {
        context.getSystemService(android.content.ClipboardManager::class.java)
            .setPrimaryClip(android.content.ClipData.newPlainText("BlueLink", fileName))
    }

    data class ImageMetadata(val width: Int, val height: Int, val mimeType: String)

    suspend fun imageMetadata(context: Context, attachment: ChatAttachment): ImageMetadata? = withContext(Dispatchers.IO) {
        if (!attachment.canOpen) return@withContext null
        runCatching {
            val uri = Uri.parse(attachment.localUri)
            val input = if (uri.scheme == "file") File(requireNotNull(uri.path)).inputStream() else context.contentResolver.openInputStream(uri)
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            input.use { if (it != null) BitmapFactory.decodeStream(it, null, bounds) }
            if (bounds.outWidth > 0 && bounds.outHeight > 0) ImageMetadata(bounds.outWidth, bounds.outHeight, bounds.outMimeType ?: attachment.mimeType) else null
        }.getOrNull()
    }

    suspend fun locationLabel(context: Context, attachment: ChatAttachment): String? = withContext(Dispatchers.IO) {
        val raw = attachment.localUri ?: return@withContext null
        val uri = Uri.parse(raw)
        if (uri.scheme == "file") return@withContext File(uri.path ?: return@withContext raw).parent
        runCatching {
            context.contentResolver.query(uri, arrayOf(MediaStore.MediaColumns.RELATIVE_PATH), null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) cursor.getString(0) else null
            }
        }.getOrNull()?.takeIf { it.isNotBlank() }
            ?: if (DocumentsContract.isDocumentUri(context, uri)) runCatching { DocumentsContract.getDocumentId(uri) }.getOrDefault(raw) else raw
    }

    suspend fun open(context: Context, attachment: ChatAttachment) {
        require(attachment.canOpen)
        // Probe before navigation: binary files must never create a transient preview Activity.
        val previewable = !attachment.isImage && readTextDocument(context, attachment) != null
        withContext(Dispatchers.Main.immediate) {
            if (previewable) TextPreviewActivity.open(context, attachment)
            else openExternal(context, attachment)
        }
    }

    internal suspend fun readTextDocument(context: Context, attachment: ChatAttachment): TextDocument? =
        withContext(Dispatchers.IO) {
            val uri = Uri.parse(requireNotNull(attachment.localUri))
            val input = if (uri.scheme == "file") File(requireNotNull(uri.path)).inputStream()
                else requireNotNull(context.contentResolver.openInputStream(uri))
            input.use { TextDocuments.read(it, attachment.fileName, attachment.mimeType) }
        }

    fun openExternal(context: Context, attachment: ChatAttachment) {
        val uri = shareableUri(context, attachment)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, attachment.mimeType)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        context.startActivity(Intent.createChooser(intent, "选择打开方式"))
    }

    fun share(context: Context, attachment: ChatAttachment) {
        context.startActivity(shareChooserIntent(context, attachment))
    }

    internal fun shareChooserIntent(context: Context, attachment: ChatAttachment): Intent =
        Intent.createChooser(shareIntent(context, attachment), null)

    internal fun shareIntent(context: Context, attachment: ChatAttachment): Intent {
        val uri = shareableUri(context, attachment)
        // EXTRA_STREAM supplies the attachment; ClipData propagates its temporary read grant
        // through the Android chooser, including WeChat and QQ's receiving activities.
        return Intent(Intent.ACTION_SEND).apply {
            type = attachment.mimeType.takeIf { it.isNotBlank() } ?: "application/octet-stream"
            putExtra(Intent.EXTRA_STREAM, uri)
            putExtra(Intent.EXTRA_TITLE, attachment.fileName)
            clipData = android.content.ClipData.newUri(context.contentResolver, attachment.fileName, uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
    }

    internal fun multiShareIntent(context: Context, attachments: List<ChatAttachment>): Intent {
        require(attachments.isNotEmpty() && attachments.size <= com.bluelink.android.domain.FileBatchPolicy.MAXIMUM_SELECTION)
        if (attachments.size == 1) return shareIntent(context, attachments.single())
        val uris = attachments.map { shareableUri(context, it) }.distinct()
        val types = attachments.map { it.mimeType.ifBlank { "application/octet-stream" } }.distinct()
        return Intent(Intent.ACTION_SEND_MULTIPLE).apply {
            type = types.singleOrNull() ?: if(types.all { it.startsWith("image/") }) "image/*" else "*/*"
            putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(uris))
            clipData = android.content.ClipData.newUri(context.contentResolver, attachments.first().fileName, uris.first()).also { clip ->
                uris.drop(1).forEach { clip.addItem(android.content.ClipData.Item(it)) }
            }
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
    }

    /** Pure text is one compatible share; mixed runs become ordered UTF-8 text files. */
    internal fun messageShareIntent(context: Context, messages: List<com.bluelink.android.domain.ChatItem>): Intent {
        val parts = com.bluelink.android.domain.MessageShareContent.parts(messages)
        require(parts.isNotEmpty())
        val attachments = com.bluelink.android.domain.MessageBatch.attachments(messages)
        if (attachments.isEmpty()) {
            val text = (parts.single() as com.bluelink.android.domain.MessageSharePart.Text).content
            return Intent(Intent.ACTION_SEND).apply {
                type = "text/plain"
                putExtra(Intent.EXTRA_TEXT, text)
                clipData = android.content.ClipData.newPlainText("BlueLink", text)
            }
        }
        // Include generated text files in the same limit, before creating anything on disk.
        require(parts.size <= com.bluelink.android.domain.FileBatchPolicy.MAXIMUM_SELECTION)
        require(attachments.all { it.canOpen && !it.isTransferActive && !it.recoveryPending && readable(context, it.localUri) })
        val created = mutableListOf<File>()
        try {
            val reserved = attachments.map { it.fileName.lowercase(java.util.Locale.ROOT) }.toMutableSet()
            var textIndex = 0
            val files = parts.map { part -> when (part) {
                is com.bluelink.android.domain.MessageSharePart.File -> part.attachment
                is com.bluelink.android.domain.MessageSharePart.Text -> {
                    val baseName = com.bluelink.android.domain.MessageShareContent.textFileName(part.content, ++textIndex,
                        context.getString(com.bluelink.android.R.string.message_share_text_file))
                    var name = baseName
                    var suffix = 2
                    while (!reserved.add(name.lowercase(java.util.Locale.ROOT))) {
                        name = baseName.removeSuffix(".txt") + " (${suffix++}).txt"
                    }
                    MessageShareTextFiles.create(context, part.content, name, created)
                }
            } }
            return multiShareIntent(context, files).apply {
                // TXT groups are files, so use the generic file receiver route, not text-only filters.
                if (parts.any { it is com.bluelink.android.domain.MessageSharePart.Text }) type = "*/*"
            }
        } catch (failure: Exception) {
            created.forEach { file -> runCatching { file.delete(); OwnedTemporaryFiles.release(file) } }
            throw failure
        }
    }

    fun readable(context: Context, uri: String?): Boolean = uri?.let {
        runCatching {
            val source = Uri.parse(it)
            if(source.scheme == "file") File(requireNotNull(source.path)).inputStream().use { true }
            else context.contentResolver.openInputStream(source)?.use { true } ?: false
        }.getOrDefault(false)
    } ?: false

    fun copyTo(context: Context, attachment: ChatAttachment, destination: Uri) {
        require(attachment.canOpen) { "文件尚未传输完成或已不可用" }
        val source = Uri.parse(attachment.localUri ?: throw IOException("文件尚未下载完成"))
        val input = if (source.scheme == "file") File(requireNotNull(source.path)).inputStream()
            else context.contentResolver.openInputStream(source)
        input.use { from ->
            requireNotNull(from) { "无法读取源文件" }
            context.contentResolver.openOutputStream(destination, "w").use { to ->
                requireNotNull(to) { "无法写入目标位置" }
                from.copyTo(to, 128 * 1024)
            }
        }
    }

    /** Full-screen previews read the completed original, never the transfer/display thumbnail cache. */
    suspend fun loadPreviewBitmap(context: Context, attachment: ChatAttachment): Bitmap? =
        withContext(Dispatchers.IO) {
            val raw = ImagePreviewDecode.source(attachment) ?: return@withContext null
            runCatching {
                val uri = Uri.parse(raw)
                fun open() = if (uri.scheme == "file") File(requireNotNull(uri.path)).inputStream()
                    else context.contentResolver.openInputStream(uri)
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                open().use { input -> if (input != null) BitmapFactory.decodeStream(input, null, bounds) }
                if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return@runCatching null
                open().use { input -> if (input == null) null else BitmapFactory.decodeStream(input, null,
                    BitmapFactory.Options().apply {
                        inSampleSize = ImagePreviewDecode.sampleSize(bounds.outWidth, bounds.outHeight)
                        inPreferredConfig = Bitmap.Config.ARGB_8888
                    }) }
            }.onFailure { CrashReporter.recordNonFatal(context, "ImagePreviewDecode", it) }.getOrNull()
        }

    suspend fun loadBitmap(context: Context, attachment: ChatAttachment, maxDimension: Int): Bitmap? =
        withContext(Dispatchers.IO) {
            runCatching {
                val sources = listOfNotNull(attachment.previewUri?.takeIf { it.isNotBlank() },
                    attachment.localUri?.takeIf { attachment.canOpen && it.isNotBlank() }).distinct()
                for (raw in sources) {
                    val decoded = runCatching {
                        val uri = Uri.parse(raw)
                        val version = if (uri.scheme == "file") File(requireNotNull(uri.path)).lastModified() else 0L
                        ThumbnailCache.load(context, "$raw:$version:$maxDimension") {
                            fun open() = if (uri.scheme == "file") File(requireNotNull(uri.path)).inputStream()
                                else context.contentResolver.openInputStream(uri)
                            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                            open().use { input -> if (input != null) BitmapFactory.decodeStream(input, null, bounds) }
                            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return@load null
                            val target = maxDimension.coerceIn(96, 4096)
                            var sample = 1
                            while (bounds.outWidth / sample > target * 2 || bounds.outHeight / sample > target * 2)
                                sample *= 2
                            open().use { input -> if (input == null) null else BitmapFactory.decodeStream(input, null,
                                BitmapFactory.Options().apply {
                                    inSampleSize = sample
                                    inPreferredConfig = Bitmap.Config.ARGB_8888
                                }) }
                        }
                    }.getOrNull()
                    if (decoded != null) return@runCatching decoded
                }
                null
            }.onFailure { CrashReporter.recordNonFatal(context, "ImageDecode", it) }.getOrNull()
        }

    private fun shareableUri(context: Context, attachment: ChatAttachment): Uri {
        require(attachment.canOpen) { "文件尚未传输完成或已不可用" }
        val raw = attachment.localUri ?: throw IOException("文件尚未下载完成")
        val parsed = Uri.parse(raw)
        return if (parsed.scheme == "file") FileProvider.getUriForFile(context,
            "${context.packageName}.files", File(requireNotNull(parsed.path)), attachment.fileName) else parsed
    }
}
