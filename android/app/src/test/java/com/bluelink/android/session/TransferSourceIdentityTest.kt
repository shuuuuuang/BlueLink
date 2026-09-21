package com.bluelink.android.session

import com.bluelink.android.domain.*
import org.junit.Assert.*
import org.junit.Test
import java.security.MessageDigest
import java.util.UUID

class TransferSourceIdentityTest {
    private val original = "file version A".toByteArray()
    private fun hash(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes)
    private fun item() = TransferItem(UUID.randomUUID(), "qa.bin", original.size.toLong(), outgoing = true,
        status = TransferStatus.FAILED, sourceSha256 = hash(original).joinToString("") { "%02x".format(it) })

    @Test fun unchangedSnapshotCanResumeButSameLengthReplacementCannot() {
        val saved = item()
        assertNull(TransferSourceIdentity.problem(saved, original.size.toLong(), hash(original)))
        assertEquals(TransferSourceIdentity.Problem.CHANGED,
            TransferSourceIdentity.problem(saved, original.size.toLong(), hash("file version B".toByteArray())))
        assertEquals(TransferSourceIdentity.Problem.CHANGED,
            TransferSourceIdentity.problem(saved, original.size + 1L, hash(original)))
        assertNull(TransferSourceIdentity.problem(saved.copy(sourceSha256 = saved.sourceSha256!!.uppercase()), original.size.toLong(), hash(original)))
    }

    @Test fun legacyAndMalformedFingerprintsRequireSelectingTheSourceAgain() {
        for (value in listOf(null, "", "00", "x".repeat(64)))
            assertEquals(TransferSourceIdentity.Problem.MISSING,
                TransferSourceIdentity.problem(item().copy(sourceSha256 = value), original.size.toLong(), hash(original)))
    }

    @Test fun freshProgressAndFailureRetainTheInitialFingerprint() {
        val first = item().copy(status = TransferStatus.OFFERED)
        val progress = TransferPauseController { }
        progress.report(first)
        progress.report(first.copy(sourceSha256 = null, status = TransferStatus.TRANSFERRING))
        assertEquals(first.sourceSha256, progress.item!!.sourceSha256)
        progress.report(first.copy(sourceSha256 = null, status = TransferStatus.FAILED))
        assertEquals(first.sourceSha256, progress.item!!.sourceSha256)
    }
}
