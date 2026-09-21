package com.bluelink.android.ui.files

import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntRect
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.window.PopupPositionProvider

/** Prefer above the field; keep choices reachable on short windows and in RTL. */
internal class FileFilterPopupPosition(private val margin: Int, private val gap: Int) : PopupPositionProvider {
    override fun calculatePosition(anchorBounds: IntRect, windowSize: IntSize,
                                   layoutDirection: LayoutDirection, popupContentSize: IntSize): IntOffset {
        val right = (windowSize.width - popupContentSize.width - margin).coerceAtLeast(0)
        val bottom = (windowSize.height - popupContentSize.height - margin).coerceAtLeast(0)
        val left = margin.coerceAtMost(right)
        val top = margin.coerceAtMost(bottom)
        val preferredX = if(layoutDirection == LayoutDirection.Ltr) anchorBounds.left else anchorBounds.right - popupContentSize.width
        val above = anchorBounds.top - gap - popupContentSize.height
        val below = anchorBounds.bottom + gap
        val y = when {
            above >= top -> above
            below <= bottom -> below
            else -> above.coerceIn(top, bottom)
        }
        return IntOffset(preferredX.coerceIn(left, right), y.coerceIn(top, bottom))
    }
}
