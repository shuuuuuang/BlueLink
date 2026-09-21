package com.bluelink.android.files

internal data class HeadingPosition(val id: String, val top: Double)

internal object DocumentReadingPosition {
    fun current(headings: List<HeadingPosition>, scrollTop: Double, viewportHeight: Double, documentHeight: Double): String? {
        if (headings.isEmpty()) return null
        // At the end, short final sections cannot reach the viewport's top edge.
        if (scrollTop > 0 && scrollTop + viewportHeight >= documentHeight - 2) return headings.last().id
        return headings.lastOrNull { it.top <= scrollTop + 8 }?.id ?: headings.first().id
    }
}
