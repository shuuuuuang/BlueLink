package com.bluelink.android.domain

import kotlin.math.abs

/** A swipe navigates only while fitted; pinch and zoomed panning stay image gestures. */
internal object ImageNavigation {
    fun dragOffset(distance: Float, pageWidth: Float, hasAdjacent: Boolean): Float =
        distance.coerceIn(-pageWidth, pageWidth) * if (hasAdjacent) 1f else 0.22f

    fun swipeStep(x: Float, y: Float, threshold: Float, zoom: Float, multiplePointers: Boolean): Int =
        if (multiplePointers || zoom > 1.01f || abs(x) < threshold || abs(x) <= abs(y) * 1.4f) 0
        else if (x < 0) 1 else -1
}

fun TransferItem.asGalleryAttachment() = ChatAttachment(
    attachmentId = attachmentId ?: id,
    transferId = id,
    fileName = name,
    mimeType = mimeType,
    sizeBytes = totalBytes,
    localUri = localUri,
    state = status.name,
    completedBytes = completedBytes,
    bytesPerSecond = bytesPerSecond,
)
