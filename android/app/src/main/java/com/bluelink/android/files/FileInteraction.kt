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

    fun copyReference(context: Context, attachment: ChatAttachment) {
        val uri = shareableUri(context, attachment)
        context.contentResolver.openAssetFileDescriptor(uri, "r")?.use { }
            ?: throw IOException("文件已移动或无法读取")
        context.getSystemService(android.content.ClipboardManager::class.java)
            .setPrimaryClip(android.content.ClipData.newUri(context.contentResolver, attachment.fileName, uri))
    }

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

    /** Directory navigation depends on the installed document provider/file manager. */
    suspend fun reveal(context: Context, attachment: ChatAttachment) {
        val folder = withContext(Dispatchers.IO) {
            if (!attachment.canOpen) throw IOException(context.getString(com.bluelink.android.R.string.content_folder_unavailable))
            val uri = Uri.parse(attachment.localUri)
            when {
                DocumentsContract.isTreeUri(uri) && DocumentsContract.isDocumentUri(context, uri) -> {
                    // https://developer.android.com/reference/android/provider/DocumentsContract#findDocumentPath(android.content.ContentResolver,android.net.Uri)
                    val path = DocumentsContract.findDocumentPath(context.contentResolver, uri)?.path.orEmpty()
                    val parent = path.getOrNull(path.lastIndex - 1)
                        ?: throw IOException(context.getString(com.bluelink.android.R.string.content_folder_unavailable))
                    DocumentsContract.buildDocumentUriUsingTree(uri, parent)
                }
                uri.authority == MediaStore.AUTHORITY -> {
                    val projection = arrayOf(MediaStore.MediaColumns.RELATIVE_PATH, MediaStore.MediaColumns.VOLUME_NAME)
                    context.contentResolver.query(uri, projection, null, null, null)?.use { cursor ->
                        if (!cursor.moveToFirst()) throw IOException(context.getString(com.bluelink.android.R.string.content_folder_unavailable))
                        val relative = cursor.getString(0)?.trim('/')?.takeIf { it.isNotBlank() && it.split('/').none { part -> part == ".." } }
                            ?: throw IOException(context.getString(com.bluelink.android.R.string.content_folder_unavailable))
                        val volume = cursor.getString(1)?.takeIf { it.isNotBlank() }
                            ?: throw IOException(context.getString(com.bluelink.android.R.string.content_folder_unavailable))
                        val storage = if (volume == MediaStore.VOLUME_EXTERNAL_PRIMARY) "primary" else volume
                        DocumentsContract.buildDocumentUri("com.android.externalstorage.documents", "$storage:$relative")
                    } ?: throw IOException(context.getString(com.bluelink.android.R.string.content_folder_unavailable))
                }
                uri.scheme == "file" || uri.authority == "${context.packageName}.files" ->
                    throw IOException(context.getString(com.bluelink.android.R.string.content_folder_private))
                else -> throw IOException(context.getString(com.bluelink.android.R.string.content_folder_unavailable))
            }
        }
        withContext(Dispatchers.Main) {
            try { context.startActivity(Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(folder, DocumentsContract.Document.MIME_TYPE_DIR)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }) } catch (failure: android.content.ActivityNotFoundException) {
                throw IOException(context.getString(com.bluelink.android.R.string.content_folder_unavailable), failure)
            } catch (failure: SecurityException) {
                throw IOException(context.getString(com.bluelink.android.R.string.content_folder_unavailable), failure)
            }
        }
    }

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
            "${context.packageName}.files", File(requireNotNull(parsed.path))) else parsed
    }
}
