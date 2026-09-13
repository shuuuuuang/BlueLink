package com.bluelink.android.files

import android.content.Context
import android.graphics.*
import android.net.Uri
import com.bluelink.android.domain.ChatAttachment
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.InputStream
import kotlin.math.ceil
import kotlin.math.floor

internal data class ChatThumbnail(val bitmap: Bitmap, val geometry: ChatThumbnailGeometry)

internal object ChatThumbnailLoader {
    suspend fun load(context: Context, attachment: ChatAttachment): ChatThumbnail? = withContext(Dispatchers.IO) {
        if (!attachment.isImage) return@withContext null
        // Prefer original dimensions once available; incomplete originals remain unreadable.
        val sources = listOfNotNull(attachment.localUri?.takeIf { attachment.canOpen && it.isNotBlank() },
            attachment.previewUri?.takeIf { it.isNotBlank() }).distinct()
        for (raw in sources) {
            val result = runCatching {
                val uri = Uri.parse(raw)
                fun open(): InputStream = if (uri.scheme == "file") File(requireNotNull(uri.path)).inputStream()
                    else requireNotNull(context.contentResolver.openInputStream(uri))
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                open().use { BitmapFactory.decodeStream(it, null, bounds) }
                if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return@runCatching null
                val geometry = ChatThumbnailGeometry.calculate(bounds.outWidth.toDouble(), bounds.outHeight.toDouble())
                val version = if (uri.scheme == "file") File(requireNotNull(uri.path)).let { "${it.lastModified()}:${it.length()}" } else "0"
                val bitmap = ThumbnailCache.load(context, "chat-min48-max190x126-v1:$raw:$version:${bounds.outWidth}:${bounds.outHeight}") {
                    decode(::open, bounds.outWidth, bounds.outHeight, geometry)
                } ?: return@runCatching null
                ChatThumbnail(bitmap, geometry)
            }.getOrNull()
            if (result != null) return@withContext result
        }
        null
    }

    @Suppress("DEPRECATION")
    private fun decode(open: () -> InputStream, width: Int, height: Int, g: ChatThumbnailGeometry): Bitmap? {
        val crop = Rect(floor(g.cropX).toInt(), floor(g.cropY).toInt(),
            minOf(width, ceil(g.cropX + g.cropWidth).toInt()), minOf(height, ceil(g.cropY + g.cropHeight).toInt()))
        var sample = 1
        while (crop.width() / sample > g.imageWidth * 4 || crop.height() / sample > g.imageHeight * 4) sample *= 2
        // Decode only the visible center, avoiding a huge bitmap for panoramas and long screenshots.
        var decodedBounds = crop
        val region = runCatching {
            open().use { input ->
                val decoder = requireNotNull(BitmapRegionDecoder.newInstance(input, false))
                try { decoder.decodeRegion(crop, BitmapFactory.Options().apply {
                    inSampleSize = sample; inPreferredConfig = Bitmap.Config.ARGB_8888
                }) } finally { decoder.recycle() }
            }
        }.getOrNull()
        val source = region ?: run {
            decodedBounds = Rect(0, 0, width, height)
            var fallbackSample = 1
            while (width / fallbackSample > 2048 || height / fallbackSample > 2048) fallbackSample *= 2
            open().use { BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply {
                inSampleSize = fallbackSample; inPreferredConfig = Bitmap.Config.ARGB_8888
            }) }
        } ?: return null
        try {
            val result = Bitmap.createBitmap(ceil(g.width * 2).toInt(), ceil(g.height * 2).toInt(), Bitmap.Config.ARGB_8888)
            val canvas = Canvas(result)
            val destination = RectF((g.insetX * 2).toFloat(), (g.insetY * 2).toFloat(),
                ((g.insetX + g.imageWidth) * 2).toFloat(), ((g.insetY + g.imageHeight) * 2).toFloat())
            canvas.clipRect(destination)
            val left = (g.insetX + (decodedBounds.left - g.cropX) * g.scale) * 2
            val top = (g.insetY + (decodedBounds.top - g.cropY) * g.scale) * 2
            canvas.drawBitmap(source, null, RectF(left.toFloat(), top.toFloat(),
                (left + decodedBounds.width() * g.scale * 2).toFloat(),
                (top + decodedBounds.height() * g.scale * 2).toFloat()), Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG))
            return result
        } finally { source.recycle() }
    }
}
