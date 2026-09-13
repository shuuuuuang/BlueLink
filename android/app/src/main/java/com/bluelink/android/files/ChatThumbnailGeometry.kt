package com.bluelink.android.files

/** Natural-size small images; fit until the short edge is 48, then center-crop the long edge. */
internal data class ChatThumbnailGeometry(val width: Double, val height: Double, val scale: Double,
    val cropX: Double, val cropY: Double, val cropWidth: Double, val cropHeight: Double) {
    val imageWidth get() = cropWidth * scale
    val imageHeight get() = cropHeight * scale
    val insetX get() = (width - imageWidth) / 2
    val insetY get() = (height - imageHeight) / 2

    companion object {
        fun calculate(width: Double, height: Double, maxWidth: Double = 190.0, maxHeight: Double = 126.0): ChatThumbnailGeometry {
            val minimum = 48.0
            require(width.isFinite() && height.isFinite() && width > 0 && height > 0 &&
                maxWidth.isFinite() && maxHeight.isFinite() && maxWidth >= minimum && maxHeight >= minimum)
            val shortEdge = minOf(width, height)
            val fit = minOf(1.0, maxWidth / width, maxHeight / height)
            val scale = if (shortEdge <= minimum) 1.0 else maxOf(fit, minimum / shortEdge)
            val viewportWidth = (width * scale).coerceIn(minimum, maxWidth)
            val viewportHeight = (height * scale).coerceIn(minimum, maxHeight)
            val cropWidth = minOf(width, viewportWidth / scale)
            val cropHeight = minOf(height, viewportHeight / scale)
            return ChatThumbnailGeometry(viewportWidth, viewportHeight, scale, (width - cropWidth) / 2,
                (height - cropHeight) / 2, cropWidth, cropHeight)
        }
    }
}
