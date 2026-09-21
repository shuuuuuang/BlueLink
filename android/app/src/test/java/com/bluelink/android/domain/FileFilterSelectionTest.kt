package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.Locale
import java.util.UUID

class FileFilterSelectionTest {
    private val day = LocalDate.parse("2026-09-14")
    private fun options(selection: FileFilterSelection, scope: String? = null) =
        selection.options("报告", HistorySort.SIZE, false, scope, Locale.CHINA)
    private fun file(id: Long, timestamp: String, peer: String = "peer") = TransferItem(UUID(0, id), "报告.pdf", id,
        outgoing = false, status = TransferStatus.COMPLETED, peerId = peer,
        startedAtEpochMs = Instant.parse(timestamp).toEpochMilli())

    @Test fun resetKeepsSearchOrderAndConversationScope() {
        val active = FileFilterSelection(status = FileStatusFilter.FAILED, peerId = "other", start = day)
        val before = options(active, "peer")
        val reset = options(FileFilterSelection(), "peer")
        assertEquals(before.query, reset.query)
        assertEquals(before.sort, reset.sort)
        assertEquals(before.descending, reset.descending)
        assertEquals("peer", reset.peerId)
        assertEquals(FileStatusFilter.ALL, reset.status)
        assertNull(reset.start)
    }

    @Test fun fixedConversationNeverBroadensToDraftDevice() {
        val items = listOf(file(1, "2026-09-14T00:00:00Z"), file(2, "2026-09-14T00:00:00Z", "other"))
        val draft = FileFilterSelection(peerId = "other")
        assertEquals(listOf(items.first()), HistorySearch.files(items, options(draft, "peer")))
        assertEquals(0, draft.count("peer"))
        assertEquals(1, draft.count(null))
    }

    @Test fun rangeCountsOnceAndAcceptsSingleEndpoints() {
        assertEquals(1, FileFilterSelection(start = day, end = day).count(null))
        assertTrue(FileFilterSelection(start = day).valid)
        assertTrue(FileFilterSelection(end = day).valid)
        assertFalse(FileFilterSelection(start = day.plusDays(1), end = day).valid)
    }

    @Test fun previewAndAppliedQueryIncludeBothLocalDateBoundaries() {
        val items = listOf(file(1, "2026-09-13T15:59:59Z"), file(2, "2026-09-13T16:00:00Z"),
            file(3, "2026-09-14T15:59:59Z"), file(4, "2026-09-14T16:00:00Z"))
        val zone = ZoneId.of("Asia/Shanghai")
        assertEquals(listOf(items[1], items[2]), HistorySearch.files(items, options(FileFilterSelection(start = day, end = day)), zone))
        assertEquals(items.drop(1), HistorySearch.files(items, options(FileFilterSelection(start = day)), zone))
        assertEquals(items.take(3), HistorySearch.files(items, options(FileFilterSelection(end = day)), zone))
    }
}
