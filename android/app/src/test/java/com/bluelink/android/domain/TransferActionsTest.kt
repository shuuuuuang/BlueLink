package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class TransferActionsTest {
    private fun item(state: TransferStatus, outgoing: Boolean = false, uri: String? = null) =
        TransferItem(UUID.randomUUID(), "test.pdf", 2048, outgoing = outgoing, status = state, localUri = uri)

    @Test fun pendingAndFinalizingTransfersCannotPauseOrOpen() {
        for (state in listOf(TransferStatus.OFFERED, TransferStatus.QUEUED, TransferStatus.REMOTE_PAUSED, TransferStatus.VERIFYING, TransferStatus.COMMITTING)) {
            assertEquals(listOf(TransferAction.DETAILS, TransferAction.CANCEL), TransferActions.available(item(state)))
        }
    }

    @Test fun pausedAndReceivingTransfersExposeMutuallyExclusiveControls() {
        val paused = TransferActions.available(item(TransferStatus.PAUSED))
        assertTrue(TransferAction.RESUME in paused); assertFalse(TransferAction.PAUSE in paused)
        val running = TransferActions.available(item(TransferStatus.TRANSFERRING))
        assertTrue(TransferAction.PAUSE in running); assertFalse(TransferAction.RESUME in running)
        assertFalse(TransferAction.DELETE in running)
    }

    @Test fun onlyCompletedLocalFilesExposeFileOperations() {
        for (state in TransferStatus.entries) {
            val actions = TransferActions.available(item(state, uri = "content://test/1"))
            assertEquals(state == TransferStatus.COMPLETED, TransferAction.OPEN in actions)
            assertEquals(state == TransferStatus.COMPLETED, TransferAction.COPY in actions)
            assertEquals(state == TransferStatus.COMPLETED, TransferAction.REVEAL in actions)
        }
        assertFalse(TransferAction.OPEN in TransferActions.available(item(TransferStatus.COMPLETED)))
    }

    @Test fun failedAndRejectedMessagesExposeRetryOnlyForTheSenderWithASource() {
        for (outgoing in listOf(true, false)) for (state in listOf(TransferStatus.FAILED, TransferStatus.REJECTED)) {
            assertEquals(if (outgoing) listOf(TransferAction.DETAILS, TransferAction.RETRY, TransferAction.FAILURE, TransferAction.DELETE)
                else listOf(TransferAction.DETAILS, TransferAction.FAILURE, TransferAction.DELETE),
                TransferActions.available(item(state, outgoing, "content://test/1")))
        }
    }

    @Test fun failedFilterIncludesCanceledAndRejectedRecordsAndPreservesScope() {
        val canceled = item(TransferStatus.CANCELED).copy(peerId = "chosen")
        val failed = item(TransferStatus.FAILED).copy(peerId = "chosen")
        val rejected = item(TransferStatus.REJECTED).copy(peerId = "other")
        val all = listOf(canceled, failed, rejected)
        assertEquals(setOf(canceled, failed), HistoryQuery.files(all, "", FileStatusFilter.FAILED, FileDirectionFilter.ALL, "chosen").toSet())
        assertEquals(all.toSet(), HistoryQuery.files(all, "", FileStatusFilter.FAILED, FileDirectionFilter.ALL, null).toSet())
    }
}
