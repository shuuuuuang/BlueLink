package com.bluelink.android.files

import com.bluelink.android.domain.ChatAttachment
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class ImagePreviewDecodeTest {
    private val attachment = ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), "photo.png", "image/png", 100,
        localUri = "file:///original.png", state = "COMPLETED", previewUri = "file:///thumbnail.png")

    @Test fun completedAttachmentUsesOriginalEvenWithThumbnail() {
        assertEquals("file:///original.png", ImagePreviewDecode.source(attachment))
        assertEquals("content://media/external/images/42", ImagePreviewDecode.source(attachment.copy(localUri = "content://media/external/images/42")))
    }
    @Test fun thumbnailCannotSubstituteForUnavailableOriginal() {
        assertNull(ImagePreviewDecode.source(attachment.copy(localUri = null)))
        assertNull(ImagePreviewDecode.source(attachment.copy(localUri = " ")))
        assertNull(ImagePreviewDecode.source(attachment.copy(state = "TRANSFERRING")))
        assertNull(ImagePreviewDecode.source(attachment.copy(mimeType = "application/pdf")))
    }
    @Test fun regularOriginalKeepsAllPixels() {
        assertEquals(1, ImagePreviewDecode.sampleSize(2400, 1600))
        assertEquals(1, ImagePreviewDecode.sampleSize(4096, 2048))
        assertEquals(1, ImagePreviewDecode.sampleSize(160, 90))
    }
    @Test fun largeSquareRespectsPixelBudget() {
        assertEquals(2, ImagePreviewDecode.sampleSize(4096, 4096))
        assertEquals(4, ImagePreviewDecode.sampleSize(8000, 6000))
    }
    @Test fun oddPanoramaAndExtremeBoundsStayWithinBothLimits() {
        for ((width, height) in listOf(8193 to 511, 511 to 8193, 4097 to 2048, Int.MAX_VALUE to Int.MAX_VALUE)) {
            val sample = ImagePreviewDecode.sampleSize(width, height)
            val w = (width.toLong() + sample - 1) / sample
            val h = (height.toLong() + sample - 1) / sample
            assertTrue(w <= ImagePreviewDecode.MAX_EDGE && h <= ImagePreviewDecode.MAX_EDGE)
            assertTrue(w * h <= ImagePreviewDecode.MAX_PIXELS)
        }
    }
    @Test(expected = IllegalArgumentException::class) fun invalidBoundsAreRejected() {
        ImagePreviewDecode.sampleSize(0, 100)
    }
}
