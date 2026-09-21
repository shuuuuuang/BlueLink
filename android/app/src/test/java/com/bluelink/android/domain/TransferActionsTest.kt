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
            assertEquals(state == TransferStatus.COMPLETED, TransferAction.SHARE in actions)
            assertEquals(state == TransferStatus.COMPLETED, TransferAction.SAVE in actions)
        }
        assertEquals(listOf(TransferAction.DETAILS, TransferAction.DELETE), TransferActions.available(item(TransferStatus.COMPLETED)))
        assertEquals(listOf(TransferAction.OPEN, TransferAction.SHARE, TransferAction.SAVE, TransferAction.DETAILS, TransferAction.DELETE),
            TransferActions.available(item(TransferStatus.COMPLETED, uri = "content://test/1")))
    }

    @Test fun failedAndRejectedMessagesExposeRetryOnlyForTheSenderWithASource() {
        for (outgoing in listOf(true, false)) for (state in listOf(TransferStatus.FAILED, TransferStatus.REJECTED)) {
            assertEquals(if (outgoing) listOf(TransferAction.DETAILS, TransferAction.RETRY, TransferAction.FAILURE, TransferAction.DELETE)
                else listOf(TransferAction.DETAILS, TransferAction.FAILURE, TransferAction.DELETE),
                TransferActions.available(item(state, outgoing, "content://test/1")))
        }
    }

    @Test fun routeSwitchOnlyAppearsForUnstartedOutgoingUsbTask() {
        val queued = item(TransferStatus.QUEUED, true).copy(queuedForUsb = true)
        assertTrue(TransferAction.BLUETOOTH in TransferActions.available(queued))
        assertFalse(TransferAction.BLUETOOTH in TransferActions.available(queued.copy(outgoing = false)))
        assertFalse(TransferAction.BLUETOOTH in TransferActions.available(queued.copy(queuedForUsb = false)))
        assertFalse(TransferAction.BLUETOOTH in TransferActions.available(queued.copy(status = TransferStatus.TRANSFERRING)))
    }

    @Test fun sourceRepairRequiresOriginalFingerprintAndOutgoingTerminalTask() {
        val original=item(TransferStatus.FAILED,true).copy(sourceSha256="A".repeat(64))
        assertTrue(TransferAction.RESELECT in TransferActions.available(original))
        assertFalse(TransferAction.RESELECT in TransferActions.available(original.copy(outgoing=false)))
        assertFalse(TransferAction.RESELECT in TransferActions.available(original.copy(sourceSha256=null)))
        assertFalse(TransferAction.RESELECT in TransferActions.available(original.copy(status=TransferStatus.TRANSFERRING)))
    }
    @Test fun incompleteFilterIncludesCanceledAndRejectedRecordsAndPreservesScope() {
        val canceled = item(TransferStatus.CANCELED).copy(peerId = "chosen")
        val failed = item(TransferStatus.FAILED).copy(peerId = "chosen")
        val rejected = item(TransferStatus.REJECTED).copy(peerId = "other")
        val all = listOf(canceled, failed, rejected)
        assertEquals(setOf(canceled, failed), HistoryQuery.files(all, "", FileStatusFilter.INCOMPLETE, FileDirectionFilter.ALL, "chosen").toSet())
        assertEquals(all.toSet(), HistoryQuery.files(all, "", FileStatusFilter.INCOMPLETE, FileDirectionFilter.ALL, null).toSet())
    }
}
