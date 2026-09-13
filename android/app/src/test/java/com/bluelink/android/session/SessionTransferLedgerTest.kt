package com.bluelink.android.session

import com.bluelink.android.domain.*
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class SessionTransferLedgerTest {
    private fun item(status: TransferStatus, outgoing: Boolean = true) = TransferItem(UUID.randomUUID(), "qa.bin", 1024,
        completedBytes = 512, outgoing = outgoing, status = status, localUri = "content://qa/source", peerId = "peer-a")

    @Test fun disconnectEndsEveryActiveTaskAndLateProgressCannotReviveIt() {
        for (outgoing in listOf(false, true)) for (status in HistoryQuery.activeStatuses) {
            val ledger = SessionTransferLedger()
            val paused = item(status, outgoing)
            ledger.record(paused)
            val failed = ledger.close().single()
            assertEquals(TransferStatus.FAILED, failed.status)
            assertEquals(paused.id, failed.id)
            assertEquals(512L, failed.completedBytes)
            assertEquals(paused.localUri, failed.localUri)
            assertFalse(TransferAction.RESUME in TransferActions.available(failed))
            assertEquals(outgoing, TransferAction.RETRY in TransferActions.available(failed))
            assertNull(ledger.record(paused.copy(status = TransferStatus.TRANSFERRING)))
            assertNull(ledger.record(paused.copy(status = TransferStatus.CANCELED)))
            assertTrue(ledger.close().isEmpty())
        }
    }

    @Test fun newSessionCanRetrySameIdAndCancelWithoutOldOwnerChangingItsState() {
        val original = item(TransferStatus.PAUSED)
        val old = SessionTransferLedger(); old.record(original)
        val failed = old.close().single()
        val current = SessionTransferLedger()
        assertEquals(TransferStatus.QUEUED, current.record(failed.copy(status = TransferStatus.QUEUED))!!.status)
        assertNull(old.record(original.copy(status = TransferStatus.CANCELED)))
        assertEquals(TransferStatus.CANCELED, current.record(failed.copy(status = TransferStatus.CANCELED))!!.status)
        assertTrue(current.close().isEmpty())
    }

    @Test fun completedAndCanceledResultsAndOtherPeersAreUnaffected() {
        val first = SessionTransferLedger(); val other = SessionTransferLedger()
        first.record(item(TransferStatus.COMPLETED)); first.record(item(TransferStatus.CANCELED))
        val paused = item(TransferStatus.PAUSED, false).copy(peerId = "peer-b")
        other.record(paused)
        assertTrue(first.close().isEmpty())
        assertEquals(paused.id, other.close().single().id)
    }
}
