package com.bluelink.android.domain

import org.junit.Assert.assertEquals
import org.junit.Test

class ImageNavigationTest {
    @Test fun draggingTracksTheFingerButResistsPastTheBoundary() {
        assertEquals(-240f, ImageNavigation.dragOffset(-240f, 1000f, true), .01f)
        assertEquals(240f, ImageNavigation.dragOffset(240f, 1000f, true), .01f)
        assertEquals(1000f, ImageNavigation.dragOffset(1200f, 1000f, true), .01f)
        assertEquals(-52.8f, ImageNavigation.dragOffset(-240f, 1000f, false), .01f)
    }

    @Test fun directionMatchesRequestedSwipeAndRequiresIntentionalHorizontalDistance() {
        assertEquals(1, ImageNavigation.swipeStep(-100f, 0f, 64f, 1f, false))
        assertEquals(-1, ImageNavigation.swipeStep(100f, 0f, 64f, 1f, false))
        assertEquals(0, ImageNavigation.swipeStep(63f, 0f, 64f, 1f, false))
        assertEquals(0, ImageNavigation.swipeStep(100f, 100f, 64f, 1f, false))
        assertEquals(0, ImageNavigation.swipeStep(0f, 300f, 64f, 1f, false))
    }
    @Test fun zoomAndPinchNeverChangeTheImage() {
        assertEquals(0, ImageNavigation.swipeStep(300f, 0f, 64f, 1.25f, false))
        assertEquals(0, ImageNavigation.swipeStep(-300f, 0f, 64f, 1f, true))
        assertEquals(0, ImageNavigation.swipeStep(300f, 0f, 64f, .5f, true))
    }
}
