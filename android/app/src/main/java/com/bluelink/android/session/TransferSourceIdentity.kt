package com.bluelink.android.session

import com.bluelink.android.domain.TransferItem

/** Compare the immutable retry snapshot before publishing an offer or resuming a checkpoint. */
internal object TransferSourceIdentity {
    enum class Problem { MISSING, CHANGED }
    fun problem(previous: TransferItem, size: Long, sha256: ByteArray): Problem? {
        val expected = previous.sourceSha256?.takeIf { it.matches(Regex("[0-9a-fA-F]{64}")) }
            ?: return Problem.MISSING
        val actual = sha256.joinToString("") { "%02x".format(it) }
        return if (size != previous.totalBytes || !expected.equals(actual, ignoreCase = true)) Problem.CHANGED else null
    }
}
