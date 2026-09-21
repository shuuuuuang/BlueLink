package com.bluelink.android.files

import org.junit.Assert.*
import org.junit.Test

class ChatThumbnailGeometryTest {
    @Test fun compactComposerUsesNaturalAspectAndCenterCropsExtremeImages() {
        val square = ChatThumbnailGeometry.calculate(200.0, 200.0, 88.0, 64.0)
        assertEquals(64.0, square.width, .001); assertEquals(square.width, square.height, .001)
        val wide = ChatThumbnailGeometry.calculate(2000.0, 10.0, 88.0, 64.0)
        assertEquals(88.0, wide.width, .001); assertEquals(48.0, wide.height, .001)
        assertEquals(10.0, wide.imageHeight, .001); assertEquals(1.0, wide.scale, .001)
        assertEquals(956.0, wide.cropX, .001)
        val tall = ChatThumbnailGeometry.calculate(10.0, 2000.0, 88.0, 64.0)
        assertEquals(48.0, tall.width, .001); assertEquals(64.0, tall.height, .001)
        assertEquals(968.0, tall.cropY, .001)
        val small = ChatThumbnailGeometry.calculate(10.0, 10.0, 88.0, 64.0)
        assertEquals(1.0, small.scale, .001); assertEquals(19.0, small.insetX, .001)
        val compact = ChatThumbnailGeometry.calculate(2000.0, 10.0, 88.0, 24.0, 24.0)
        assertEquals(24.0, compact.height, .001); assertEquals(10.0, compact.imageHeight, .001)
    }

    @Test fun sharedCropAndNaturalSizeCases() {
        val cases = requireNotNull(javaClass.classLoader?.getResourceAsStream("chat-thumbnail-cases.tsv"))
            .bufferedReader().use { it.readLines() }.filter { !it.startsWith("#") && it.isNotBlank() }
        for (line in cases) {
            val columns = line.split('\t'); val n = columns.drop(1).map(String::toDouble)
            val g = ChatThumbnailGeometry.calculate(n[0], n[1], n[2], n[3])
            listOf(g.scale, g.width, g.height, g.cropX, g.cropY, g.cropWidth, g.cropHeight)
                .zip(n.drop(4)).forEach { (actual, expected) -> assertEquals(columns[0], expected, actual, 0.00001) }
        }
    }
    @Test fun extremeAspectRatiosNeverShrinkSmallEdgesAndAlwaysCropTheCenter() {
        for (w in listOf(1, 24, 47, 48, 49, 100, 400, 1000, 32000))
        for (h in listOf(1, 24, 47, 48, 49, 100, 400, 1000, 32000)) {
            val g = ChatThumbnailGeometry.calculate(w.toDouble(), h.toDouble())
            assertTrue(g.width in 48.0..190.0 && g.height in 48.0..126.0)
            assertTrue(g.scale > 0 && g.scale <= 1)
            if (minOf(w, h) <= 48) assertEquals(1.0, g.scale, 0.0)
            else assertTrue(minOf(w, h) * g.scale >= 48 - 0.00001)
            assertEquals(w / 2.0, g.cropX + g.cropWidth / 2, 0.00001)
            assertEquals(h / 2.0, g.cropY + g.cropHeight / 2, 0.00001)
        }
    }
    @Test(expected = IllegalArgumentException::class) fun invalidImageHasNoGeometry() {
        ChatThumbnailGeometry.calculate(0.0, 1.0)
    }
}
