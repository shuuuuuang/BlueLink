package com.bluelink.android.domain

import com.bluelink.core.AttachmentRole
import org.junit.Assert.*
import org.junit.Test

class ReceivePolicyTest {
    @Test fun chargingPolicyHasAnExplicitBoundaryAndRespectsItsSwitch() {
        val size = ReceivePolicy.LARGE_FILE_BYTES
        assertFalse(ReceivePolicy.needsCharging(size - 1, true, false))
        assertTrue(ReceivePolicy.needsCharging(size, true, false))
        assertFalse(ReceivePolicy.needsCharging(size, true, true))
        assertFalse(ReceivePolicy.needsCharging(size, false, false))
    }
    @Test fun previewIsNeverPublishedAndImagesAreIndependentOfOtherAttachments() {
        assertFalse(ReceivePolicy.saveToDirectory(AttachmentRole.IMAGE_PREVIEW, "image/jpeg", true, true))
        assertTrue(ReceivePolicy.saveToDirectory(AttachmentRole.IMAGE_ORIGINAL, "image/png", true, false))
        assertFalse(ReceivePolicy.saveToDirectory(AttachmentRole.FILE, "application/pdf", true, false))
        assertTrue(ReceivePolicy.saveToDirectory(AttachmentRole.FILE, "application/pdf", false, true))
        assertFalse(ReceivePolicy.saveToDirectory(AttachmentRole.IMAGE_ORIGINAL, "image/png", false, true))
    }
}
