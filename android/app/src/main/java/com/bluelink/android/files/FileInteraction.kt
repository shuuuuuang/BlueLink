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

object ReceivedFileStore {
    fun publish(context: Context, source: Path, name: String, mimeType: String,
                destination: String = "downloads://BlueLink"): Uri {
        val resolver = context.contentResolver
        if (destination.startsWith("content://")) {
            val tree = Uri.parse(destination)
            val document = DocumentsContract.buildDocumentUriUsingTree(tree,
                DocumentsContract.getTreeDocumentId(tree))
            val uri = DocumentsContract.createDocument(resolver, document, mimeType, name)
                ?: throw IOException("无法在自定义目录中创建文件")
            try {
                resolver.openOutputStream(uri, "w").use { output ->
                    requireNotNull(output) { "无法写入自定义目录" }
                    source.inputStream().buffered(128 * 1024).use { it.copyTo(output, 128 * 1024) }
                }
                source.deleteIfExists()
                return uri
            } catch (failure: Throwable) {
                runCatching { DocumentsContract.deleteDocument(resolver, uri) }
                throw failure
            }
        }
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(MediaStore.MediaColumns.MIME_TYPE, mimeType)
            put(MediaStore.MediaColumns.RELATIVE_PATH, "${Environment.DIRECTORY_DOWNLOADS}/BlueLink")
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: throw IOException("无法在 Download/BlueLink 中创建文件")
        try {
            resolver.openOutputStream(uri, "w").use { output ->
                requireNotNull(output) { "无法打开系统下载目录" }
                source.inputStream().buffered(128 * 1024).use { it.copyTo(output, 128 * 1024) }
            }
            values.clear()
            values.put(MediaStore.MediaColumns.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
            source.deleteIfExists()
            return uri
        } catch (failure: Throwable) {
            resolver.delete(uri, null, null)
            throw failure
        }
    }
}

object FileInteraction {
    fun open(context: Context, attachment: ChatAttachment) {
        val uri = shareableUri(context, attachment)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, attachment.mimeType)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        context.startActivity(Intent.createChooser(intent, "选择打开方式"))
    }

    fun share(context: Context, attachment: ChatAttachment) {
        val uri = shareableUri(context, attachment)
        val intent = Intent(Intent.ACTION_SEND).apply {
            type = attachment.mimeType
            putExtra(Intent.EXTRA_STREAM, uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        context.startActivity(Intent.createChooser(intent, "分享文件"))
    }

    fun copyTo(context: Context, attachment: ChatAttachment, destination: Uri) {
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

    suspend fun loadBitmap(context: Context, attachment: ChatAttachment, maxDimension: Int): Bitmap? =
        withContext(Dispatchers.IO) {
            runCatching {
                val raw = attachment.previewUri ?: attachment.localUri ?: return@runCatching null
                val uri = Uri.parse(raw)
                fun open() = if (uri.scheme == "file") File(requireNotNull(uri.path)).inputStream()
                    else context.contentResolver.openInputStream(uri)
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                open().use { input -> if (input != null) BitmapFactory.decodeStream(input, null, bounds) }
                if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return@runCatching null
                val target = maxDimension.coerceIn(96, 4096)
                var sample = 1
                while (bounds.outWidth / sample > target * 2 || bounds.outHeight / sample > target * 2)
                    sample *= 2
                open().use { input -> if (input == null) null else BitmapFactory.decodeStream(input, null,
                    BitmapFactory.Options().apply {
                        inSampleSize = sample
                        inPreferredConfig = Bitmap.Config.ARGB_8888
                    }) }
            }.onFailure { CrashReporter.recordNonFatal(context, "ImageDecode", it) }.getOrNull()
        }

    private fun shareableUri(context: Context, attachment: ChatAttachment): Uri {
        val raw = attachment.localUri ?: throw IOException("文件尚未下载完成")
        val parsed = Uri.parse(raw)
        return if (parsed.scheme == "file") FileProvider.getUriForFile(context,
            "${context.packageName}.files", File(requireNotNull(parsed.path))) else parsed
    }
}
