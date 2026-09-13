package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class AttachmentThumbnailTest {
    private val item = ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), "photo.png", "image/png", 1000, state = "TRANSFERRING")
    @Test fun verifiedPreviewIsVisibleBeforeOriginalFinishesButCannotBeOpened() {
        for (state in listOf("OFFERED", "TRANSFERRING", "PAUSED", "REMOTE_PAUSED", "FAILED")) {
            val attachment = item.copy(state = state, previewUri = "file:///verified-preview.png")
            assertTrue(attachment.showsThumbnail(true))
            assertFalse(attachment.canOpen)
            assertFalse(attachment.showsThumbnail(false))
        }
    }
    @Test fun noPartialOriginalOrNonImageIsDecodedAsThumbnail() {
        assertFalse(item.copy(localUri = "file:///partial.png").showsThumbnail(true))
        assertFalse(item.copy(mimeType = "application/pdf", previewUri = "file:///preview").showsThumbnail(true))
        assertFalse(item.copy(previewUri = " ").showsThumbnail(true))
    }
    @Test fun completedImageHonorsBothToggleDirections() {
        val attachment = item.copy(state = "COMPLETED", localUri = "file:///original.png")
        for (enabled in listOf(true, false, true)) assertEquals(enabled, attachment.showsThumbnail(enabled))
        assertEquals("file:///original.png", attachment.thumbnailUri)
    }
}
