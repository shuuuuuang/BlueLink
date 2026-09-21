package com.bluelink.android.ui.files

import androidx.compose.ui.unit.*
import org.junit.Assert.*
import org.junit.Test

class FileFilterPopupPositionTest {
    private val position = FileFilterPopupPosition(12, 6)
    @Test fun prefersAboveEvenWhenSpaceExistsBelow() {
        assertEquals(IntOffset(70, 294), position.calculatePosition(IntRect(70, 500, 270, 544), IntSize(400, 900), LayoutDirection.Ltr, IntSize(200, 200)))
    }
    @Test fun fallsBelowWhenTopHasNoRoom() {
        assertEquals(IntOffset(70, 70), position.calculatePosition(IntRect(70, 20, 270, 64), IntSize(400, 900), LayoutDirection.Ltr, IntSize(200, 200)))
    }
    @Test fun rightToLeftAlignsTrailingEdgeAndClampsToWindow() {
        assertEquals(IntOffset(170, 294), position.calculatePosition(IntRect(70, 500, 270, 544), IntSize(400, 900), LayoutDirection.Rtl, IntSize(100, 200)))
        assertEquals(IntOffset(188, 294), position.calculatePosition(IntRect(350, 500, 550, 544), IntSize(400, 900), LayoutDirection.Ltr, IntSize(200, 200)))
    }
    @Test fun shortWindowKeepsPopupWithinAvailableBounds() {
        val result = position.calculatePosition(IntRect(70, 90, 270, 134), IntSize(300, 250), LayoutDirection.Ltr, IntSize(200, 220))
        assertEquals(IntOffset(70, 12), result)
    }
}
