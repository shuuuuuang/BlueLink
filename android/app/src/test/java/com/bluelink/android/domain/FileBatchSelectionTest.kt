package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class FileBatchSelectionTest {
    private fun item(status: TransferStatus, outgoing: Boolean = true) =
        TransferItem(UUID.randomUUID(), "QA.txt", 10, outgoing = outgoing, status = status)

    @Test fun gatesFollowCurrentSelectionAndTransferDirection() {
        for (action in FileBatchAction.entries) assertFalse(FileBatchSelection.canRequest(emptyList(), action))
        for (status in TransferStatus.entries) for (outgoing in listOf(true, false)) {
            val items = listOf(item(status, outgoing))
            assertEquals(status == TransferStatus.FAILED && outgoing, FileBatchSelection.canRequest(items, FileBatchAction.RETRY))
            assertEquals(status in HistoryQuery.activeStatuses, FileBatchSelection.canRequest(items, FileBatchAction.CANCEL))
            assertTrue(FileBatchSelection.canRequest(items, FileBatchAction.SHARE))
            assertTrue(FileBatchSelection.canRequest(items, FileBatchAction.DELETE_RECORDS))
        }
    }
    @Test fun mixedSelectionEnablesEachApplicableActionAndReactsToCompletion() {
        val failed = item(TransferStatus.FAILED)
        val active = item(TransferStatus.TRANSFERRING)
        val selected = listOf(item(TransferStatus.COMPLETED), failed, active)
        assertTrue(FileBatchSelection.canRequest(selected, FileBatchAction.RETRY))
        assertTrue(FileBatchSelection.canRequest(selected, FileBatchAction.CANCEL))
        val completed = selected.map { it.copy(status = TransferStatus.COMPLETED) }
        assertFalse(FileBatchSelection.canRequest(completed, FileBatchAction.RETRY))
        assertFalse(FileBatchSelection.canRequest(completed, FileBatchAction.CANCEL))
    }
    @Test fun rangesFollowDisplayOrderBothWaysAndPreserveExistingSelection() {
        val ids = listOf("active", "new", "old", "failed")
        assertEquals(listOf("new", "active", "old"), FileBatchSelection.selectRange(ids, listOf("new", "active"), "new", "old"))
        assertEquals(listOf("failed", "new", "old"), FileBatchSelection.selectRange(ids, listOf("failed"), "failed", "new"))
        assertEquals("failed", FileBatchSelection.rangeTarget(ids, listOf("new"), "new", "active", "failed"))
        assertEquals("active", FileBatchSelection.rangeTarget(ids, listOf("failed"), "failed", "active", "old"))
    }
    @Test fun missingCollapsedAndAlreadySelectedEndpointsDoNotSelectHiddenRows() {
        val ids = listOf("b", "d")
        assertEquals(listOf("a"), FileBatchSelection.selectRange(ids, listOf("a"), "a", "d"))
        assertNull(FileBatchSelection.rangeTarget(ids, listOf("a"), "a", "b", "d"))
        assertNull(FileBatchSelection.rangeTarget(ids, ids, "b", "b", "d"))
        assertNull(FileBatchSelection.rangeTarget(ids, listOf("b"), "b", null, null))
    }
    @Test fun rangeLimitIsAtomicIncludingPreexistingSelections() {
        val ids = (1..101).map(Int::toString)
        assertEquals(100, FileBatchSelection.selectRange(ids, listOf("1"), "1", "100")!!.size)
        assertNull(FileBatchSelection.selectRange(ids, listOf("1"), "1", "101"))
        assertNull(FileBatchSelection.selectRange(ids, listOf("outside"), "1", "100"))
    }
    @Test fun partiallyVisibleCardsCannotBecomeEndpoints() {
        assertFalse(MessageBatch.fullyVisible(-1, 100, 0, 200))
        assertFalse(MessageBatch.fullyVisible(101, 100, 0, 200))
        assertTrue(MessageBatch.fullyVisible(100, 100, 0, 200))
        val ids = listOf("partial-top", "full-a", "full-b", "partial-bottom")
        assertEquals("full-b", FileBatchSelection.rangeTarget(ids, listOf("partial-top"), "partial-top", "full-a", "full-b"))
    }
}
