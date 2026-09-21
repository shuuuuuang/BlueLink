package com.bluelink.android.domain

import com.bluelink.core.AttachmentRole
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class TransferHistoryIndexTest {
    private fun file(peer: String, start: Long = 10, update: Long = 20) = TransferItem(
        UUID.randomUUID(), "history.pdf", 100, 40, false, TransferStatus.TRANSFERRING,
        peerId = peer, startedAtEpochMs = start, updatedAtEpochMs = update)

    @Test fun restoresAllPeersWithoutOpeningAnyConversationAndPreservesTimeOrder() {
        val a = file("a", 1, 5); val b = file("b", 2, 4)
        val index = TransferHistoryIndex().restore(listOf(a, b))
        assertEquals(setOf(a.id, b.id), index.items.keys)
        assertEquals(listOf(b, a), HistoryQuery.files(index.items.values.toList(), "", FileStatusFilter.ALL, FileDirectionFilter.ALL, null))
        assertEquals(listOf(a), HistoryQuery.files(index.items.values.toList(), "", FileStatusFilter.ALL, FileDirectionFilter.ALL, "a"))
    }

    @Test fun newerProgressWinsUntilDatabaseAcknowledgesItAndSpeedSurvivesAcknowledgement() {
        val a = file("a"); val live = a.copy(completedBytes = 75, updatedAtEpochMs = 30, bytesPerSecond = 120.0)
        val pending = TransferHistoryIndex().restore(listOf(a)).receive(live).restore(listOf(a))
        assertEquals(live, pending.items[a.id])
        val acknowledged = pending.restore(listOf(live.copy(bytesPerSecond = 0.0)))
        assertEquals(live, acknowledged.items[a.id])
        assertTrue(acknowledged.restore(emptyList()).items.isEmpty())
    }

    @Test fun unpersistedRecordsRemainVisibleWhenHistorySavingIsDisabled() {
        val a = file("a")
        assertEquals(a, TransferHistoryIndex().receive(a).restore(emptyList()).items[a.id])
    }

    @Test fun deletionRejectsLateDatabaseSnapshotsAndProgressForThatRecordOnly() {
        val a = file("a"); val b = file("b")
        val index = TransferHistoryIndex().restore(listOf(a, b)).forget(setOf(a.id))
            .restore(listOf(a, b)).receive(a.copy(updatedAtEpochMs = 99))
        assertEquals(setOf(b.id), index.items.keys)
        assertFalse(index.accepts(a.id))
        assertTrue(index.accepts(b.id))
    }

    @Test fun clearCannotBeUndoneByInFlightCallbacksButNewTransfersStillAppear() {
        val a = file("a"); val b = file("b")
        val index = TransferHistoryIndex().receive(a).clear().restore(listOf(a))
            .receive(a.copy(updatedAtEpochMs = 100)).receive(b)
        assertEquals(setOf(b.id), index.items.keys)
    }

    @Test fun retentionDropsExpiredMemoryAndKeepsBoundaryAndRecentRecords() {
        val expired = file("a", 1, 9).copy(status = TransferStatus.COMPLETED); val boundary = file("b", 1, 10).copy(status = TransferStatus.COMPLETED)
        val index = TransferHistoryIndex().receive(expired).receive(boundary).retainSince(10)
            .restore(listOf(expired, boundary))
        assertEquals(setOf(boundary.id), index.items.keys)
    }

    @Test fun staleCallbacksAndImagePreviewsDoNotReplaceUserVisibleRecords() {
        val a = file("a"); val newer = a.copy(updatedAtEpochMs = 30, status = TransferStatus.COMPLETED)
        val preview = file("a").copy(role = AttachmentRole.IMAGE_PREVIEW)
        val index = TransferHistoryIndex().restore(listOf(a, preview)).receive(newer).receive(a).receive(preview)
        assertEquals(mapOf(a.id to newer), index.items)
    }
}
