package com.bluelink.android.files

import android.content.Context
import android.content.ContentUris
import android.net.Uri
import android.provider.MediaStore
import com.bluelink.android.BlueLinkApplication
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withContext
import java.nio.file.Files
import java.nio.file.LinkOption

internal data class StorageUsage(val imageBytes: Long, val otherBytes: Long) {
    val totalBytes: Long get() = imageBytes + otherBytes
    val imageFraction: Float get() = if (totalBytes == 0L) 0f else imageBytes.toFloat() / totalBytes
}

internal suspend fun readStorageUsage(context: Context): StorageUsage = withContext(Dispatchers.IO) {
    var images = 0L
    var other = 0L
    fun add(bytes: Long, image: Boolean) { if (image) images += bytes.coerceAtLeast(0) else other += bytes.coerceAtLeast(0) }
    val imageExtensions = setOf("png", "jpg", "jpeg", "webp", "gif", "heic", "avif", "bmp")
    val root = java.io.File(context.applicationInfo.dataDir).toPath()
    Files.walk(root).use { paths -> paths.filter { Files.isRegularFile(it, LinkOption.NOFOLLOW_LINKS) }.forEach { path ->
        add(Files.size(path), path.fileName.toString().substringAfterLast('.', "").lowercase() in imageExtensions)
    } }
    val seen = mutableSetOf<String>()
    val resolver = context.contentResolver
    resolver.query(MediaStore.Downloads.EXTERNAL_CONTENT_URI,
        arrayOf(MediaStore.MediaColumns._ID, MediaStore.MediaColumns.SIZE, MediaStore.MediaColumns.MIME_TYPE),
        "${MediaStore.MediaColumns.OWNER_PACKAGE_NAME} = ?", arrayOf(context.packageName), null)?.use { cursor ->
        while (cursor.moveToNext()) {
            seen += ContentUris.withAppendedId(MediaStore.Downloads.EXTERNAL_CONTENT_URI, cursor.getLong(0)).toString()
            add(cursor.getLong(1), cursor.getString(2).orEmpty().startsWith("image/"))
        }
    }
    // Custom SAF files are counted only when referenced by BlueLink records.
    val records = (context.applicationContext as BlueLinkApplication).localRepository.transfers.first()
    records.filter { it.direction == "INCOMING" && it.status == "COMPLETED" }.forEach { item ->
        val raw = item.localUri ?: return@forEach
        val uri = Uri.parse(raw)
        if (uri.scheme == "file" || !seen.add(raw)) return@forEach
        val file = try { resolver.openAssetFileDescriptor(uri, "r") } catch (_: java.io.FileNotFoundException) { null }
        file?.use { descriptor ->
            val bytes = descriptor.length.takeIf { it >= 0 } ?: item.totalBytes
            add(bytes, item.mimeType.startsWith("image/"))
        }
    }
    StorageUsage(images, other)
}
