package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class SecurityRequestTest {
    private fun request() = SecurityRequest("Peer", "123 456", "local", "remote", true)
    @Test fun confirmationIsSingleUseAndCancelPreventsCommit() {
        val request = request()
        assertTrue(request.confirm()); assertFalse(request.confirm())
        request.cancel()
        var committed = false
        assertFalse(request.complete { committed = true })
        assertFalse(committed); assertTrue(request.abort.isCompleted)
    }
    @Test fun expiredAndRejectedRequestsCannotBeConfirmed() {
        for (stage in listOf(TrustStage.TIMED_OUT, TrustStage.REJECTED, TrustStage.IDENTITY_CHANGED)) {
            val request = request(); request.finish(stage)
            assertFalse(request.confirm()); assertFalse(request.complete { fail("Unexpected trust") })
            request.cancel(); assertEquals(stage, request.stage.value)
        }
    }
    @Test fun failedCommitDoesNotReportSuccess() {
        val request = request(); request.confirm()
        try { request.complete { error("disk failure") }; fail() } catch (_: IllegalStateException) { }
        request.finish(TrustStage.FAILED)
        assertEquals(TrustStage.FAILED, request.stage.value)
    }
    @Test fun identitySnapshotBeforeResetCannotBeginNewTrust() {
        val registry = PeerTrustRegistry({ null }, { _, _ -> fail("Unexpected trust") }, {})
        val epoch = registry.snapshot { "identity" }.second
        registry.invalidateAll { }
        try { registry.begin("peer", byteArrayOf(1), epoch); fail() } catch (_: IllegalStateException) { }
    }
}
