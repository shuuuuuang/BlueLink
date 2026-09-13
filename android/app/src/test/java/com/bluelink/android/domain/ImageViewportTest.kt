package com.bluelink.android.domain

import org.junit.Assert.assertEquals
import org.junit.Test

class ImageViewportTest {
    @Test fun fitAndZoomKeepPanInsideImageEdges() {
        val fit = ImageViewport.fit(1920, 1080, 320f, 600f, 0, 1f)
        assertEquals(320f, fit.width, 0.01f)
        assertEquals(180f, fit.height, 0.01f)
        assertEquals(0f, fit.limitX, 0f)
        assertEquals(0f, fit.limitY, 0f)
        val zoomed = ImageViewport.fit(1920, 1080, 320f, 600f, 0, 4f)
        assertEquals(480f, zoomed.limitX, 0.01f)
        assertEquals(60f, zoomed.limitY, 0.01f)
    }
    @Test fun quarterRotationRefitsPortraitBounds() {
        val rotated = ImageViewport.fit(1920, 1080, 320f, 600f, 1, 1f)
        assertEquals(568.889f, rotated.width, 0.01f)
        assertEquals(320f, rotated.height, 0.01f)
        assertEquals(0f, rotated.limitX, 0.01f)
        assertEquals(0f, rotated.limitY, 0.01f)
        assertEquals(rotated, ImageViewport.fit(1920, 1080, 320f, 600f, 3, 1f))
    }
    @Test fun unknownImageOrViewportDoesNotDivideByZero() {
        assertEquals(ImageViewport(0f, 0f, 0f, 0f), ImageViewport.fit(0, 0, 320f, 600f, 0, 1f))
        assertEquals(ImageViewport(0f, 0f, 0f, 0f), ImageViewport.fit(1920, 1080, 0f, 0f, 0, 1f))
    }
}
