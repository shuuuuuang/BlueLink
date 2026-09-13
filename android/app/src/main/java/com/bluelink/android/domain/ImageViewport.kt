package com.bluelink.android.domain

/** Pixel geometry shared by buttons and gestures; rotation changes fit and drag bounds. */
data class ImageViewport(val width: Float, val height: Float, val limitX: Float, val limitY: Float) {
    companion object {
        fun fit(imageWidth: Int, imageHeight: Int, viewportWidth: Float, viewportHeight: Float,
                quarterTurns: Int, zoom: Float): ImageViewport {
            if (imageWidth <= 0 || imageHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
                return ImageViewport(0f, 0f, 0f, 0f)
            val rotated = quarterTurns % 2 != 0
            val effectiveWidth = if (rotated) imageHeight else imageWidth
            val effectiveHeight = if (rotated) imageWidth else imageHeight
            val fit = minOf(viewportWidth / effectiveWidth, viewportHeight / effectiveHeight)
            return ImageViewport(imageWidth * fit, imageHeight * fit,
                ((effectiveWidth * fit * zoom - viewportWidth) / 2).coerceAtLeast(0f),
                ((effectiveHeight * fit * zoom - viewportHeight) / 2).coerceAtLeast(0f))
        }
    }
}
