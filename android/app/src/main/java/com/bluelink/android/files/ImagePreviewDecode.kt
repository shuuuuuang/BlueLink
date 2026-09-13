package com.bluelink.android.files

import com.bluelink.android.domain.ChatAttachment

/** Bound one ARGB preview to 32 MiB and a 4096px edge, while preserving smaller originals. */
internal object ImagePreviewDecode {
    const val MAX_EDGE = 4096
    const val MAX_PIXELS = 8L * 1024 * 1024

    fun source(attachment: ChatAttachment): String? =
        attachment.localUri?.takeIf { attachment.canOpen && attachment.isImage && it.isNotBlank() }

    fun sampleSize(width: Int, height: Int): Int {
        require(width > 0 && height > 0)
        var sample = 1
        // Round up: codecs can round sampled dimensions up for odd source sizes.
        fun sampled(size: Int) = (size.toLong() + sample - 1) / sample
        while (sampled(width) > MAX_EDGE || sampled(height) > MAX_EDGE ||
            sampled(width) * sampled(height) > MAX_PIXELS) sample *= 2
        return sample
    }
}
