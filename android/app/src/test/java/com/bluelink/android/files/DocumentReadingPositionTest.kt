package com.bluelink.android.files

import org.junit.Assert.*
import org.junit.Test

class DocumentReadingPositionTest {
    private val headings = listOf(HeadingPosition("section-1", 20.0), HeadingPosition("section-2", 500.0), HeadingPosition("section-3", 950.0))
    @Test fun followsReadingLineInsteadOfTitleLevelOrVisibleNextHeading() {
        assertEquals("section-1", DocumentReadingPosition.current(headings, 0.0, 600.0, 1500.0))
        assertEquals("section-1", DocumentReadingPosition.current(headings, 400.0, 600.0, 1500.0))
        assertEquals("section-2", DocumentReadingPosition.current(headings, 500.0, 600.0, 1500.0))
        assertEquals("section-1", DocumentReadingPosition.current(headings, 100.0, 600.0, 1500.0))
    }
    @Test fun finalShortSectionAndSmallRoundingDifferenceAreHandled() {
        assertEquals("section-3", DocumentReadingPosition.current(headings, 900.0, 600.0, 1500.0))
        assertEquals("section-2", DocumentReadingPosition.current(headings, 499.5, 600.0, 1500.0))
        assertEquals("section-1", DocumentReadingPosition.current(headings, 0.0, 2000.0, 1500.0))
    }
    @Test fun emptyOutlineHasNoSelection() {
        assertNull(DocumentReadingPosition.current(emptyList(), 50.0, 600.0, 1000.0))
    }
}
