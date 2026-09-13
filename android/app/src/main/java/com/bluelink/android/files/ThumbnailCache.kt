package com.bluelink.android.files

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.security.MessageDigest

/** Regenerable display thumbnails only. Received previews/originals are never cache-cleanup targets. */
internal object ThumbnailCache {
    private val lock = Any()
    fun load(context: Context, key: String, decode: () -> Bitmap?): Bitmap? = synchronized(lock) {
        val root = File(context.cacheDir, "display-thumbnails")
        val name = MessageDigest.getInstance("SHA-256").digest(key.toByteArray()).joinToString("") { "%02x".format(it) } + ".png"
        val file = File(root, name)
        BitmapFactory.decodeFile(file.path)?.let { return@synchronized it }
        val bitmap = decode() ?: return@synchronized null
        // Caching is optional: a full/read-only cache must not prevent previewing an original.
        runCatching {
            root.mkdirs()
            val files = root.listFiles().orEmpty().filter { it.isFile && it.extension == "png" }.sortedBy { it.lastModified() }
            var bytes = files.sumOf { it.length() }
            var count = files.size
            for (old in files) {
                if (bytes < 32L * 1024 * 1024 && count < 128) break
                val size = old.length()
                if (old.delete()) { bytes -= size; count-- }
            }
            file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        }
        bitmap
    }

    suspend fun clear(context: Context) = withContext(Dispatchers.IO) {
        synchronized(lock) {
            val root = File(context.cacheDir, "display-thumbnails")
            root.listFiles().orEmpty().filter { it.isFile && it.extension == "png" }.forEach {
                check(it.delete() || !it.exists()) { "Unable to remove cached thumbnail" }
            }
        }
    }
}
