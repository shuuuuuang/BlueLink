package com.bluelink.android.domain

import com.bluelink.core.AttachmentRole

object ReceivePolicy {
    const val LARGE_FILE_BYTES = 100L * 1024 * 1024
    fun needsCharging(bytes: Long, onlyWhileCharging: Boolean, charging: Boolean) =
        onlyWhileCharging && bytes >= LARGE_FILE_BYTES && !charging
    fun saveToDirectory(role: AttachmentRole, mimeType: String, images: Boolean, other: Boolean): Boolean =
        role != AttachmentRole.IMAGE_PREVIEW && if (mimeType.startsWith("image/", true)) images else other
}
