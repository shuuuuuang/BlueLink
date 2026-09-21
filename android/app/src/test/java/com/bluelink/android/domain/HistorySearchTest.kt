package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID
import java.util.Locale
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.delay

class HistorySearchTest {
    private val epoch = Instant.parse("2026-09-05T16:00:00Z")
    private val shanghai = ZoneId.of("Asia/Shanghai")
    private fun file(id: Long, name: String = "计划.png", size: Long = 10, time: Long = epoch.toEpochMilli()) = TransferItem(
        UUID(0, id), name, size, outgoing = false, status = TransferStatus.COMPLETED,
        peerId = "PEER", startedAtEpochMs = time)

    @Test fun dateRangeCombinesWithTypeAndIncludesLocalBoundaries() {
        val day = LocalDate.parse("2026-09-06")
        val items = listOf(file(1), file(2, time = epoch.toEpochMilli() - 1), file(3, name = "计划.pdf"), file(4).copy(peerId = "another"))
        val options = FileQueryOptions("计划", FileStatusFilter.COMPLETED, FileDirectionFilter.RECEIVED, "peer", FileKind.IMAGES, day, day)
        assertEquals(listOf(items[0]), HistorySearch.files(items, options, shanghai))
        assertTrue(HistorySearch.files(items, options.copy(start = day.plusDays(1)), shanghai).isEmpty())
        val message = ChatItem(text = "命中", outgoing = false, timestamp = epoch, status = MessageStatus.RECEIVED)
        assertEquals(listOf(message), HistoryQuery.messages(listOf(message), "命中", HistoryKind.TEXT, day, shanghai, day))
        assertTrue(HistoryQuery.messages(listOf(message), "命中", HistoryKind.IMAGES, day, shanghai, day).isEmpty())
    }

    @Test fun tiesHaveStableIdsAcrossReorderInsertionAndDeletion() {
        val items = listOf(file(3), file(1), file(2))
        for(sort in HistorySort.entries) for(descending in listOf(false, true)) {
            val options = FileQueryOptions(sort = sort, descending = descending)
            assertEquals(listOf(1L, 2L, 3L), HistorySearch.files(items, options).map { it.id.leastSignificantBits })
            assertEquals(listOf(1L, 2L, 3L), HistorySearch.files(items.reversed(), options).map { it.id.leastSignificantBits })
        }
        val all = (1L..250).map { file(it) }
        val sorted = HistorySearch.files(all.reversed(), FileQueryOptions())
        assertEquals(250, sorted.chunked(100).flatten().map { it.id }.distinct().size)
        val changed = HistorySearch.files(all.filterNot { it.id.leastSignificantBits == 100L } + file(0), FileQueryOptions())
        assertEquals(250, changed.map { it.id }.distinct().size)
        assertEquals(0L, changed.first().id.leastSignificantBits)
    }

    @Test fun nameSizeAndDirectionAreIndependent() {
        val items = listOf(file(1, "Z.pdf", 30), file(2, "a.pdf", 10), file(3, "中文.pdf", 20))
        assertEquals(listOf(2L,3L,1L), HistorySearch.files(items, FileQueryOptions(sort = HistorySort.SIZE, descending = false)).map { it.id.leastSignificantBits })
        assertEquals(listOf(1L,3L,2L), HistorySearch.files(items, FileQueryOptions(sort = HistorySort.SIZE)).map { it.id.leastSignificantBits })
        val names = HistorySearch.files(items, FileQueryOptions(sort = HistorySort.NAME, descending = false, locale = Locale.ENGLISH))
        assertEquals("a.pdf", names.first().name)
        assertTrue(HistorySearch.files(items, FileQueryOptions(direction = FileDirectionFilter.SENT)).isEmpty())
        assertEquals(3, HistorySearch.files(items, FileQueryOptions(kind = FileKind.FILES)).size)
    }

    @Test fun desktopJvmBenchmarkRecordsDataWithoutClaimingPhonePerformance() = runBlocking {
        for(count in listOf(1000,10000,100000)) {
            val items = (1L..count.toLong()).map { file(it, "资料-${it % 100}报告.PDF", it) }
            val times = mutableListOf<Double>()
            repeat(3) { withContext(Dispatchers.Default) { HistorySearch.files(items, FileQueryOptions(sort = HistorySort.NAME)) } }
            repeat(20) {
                val started = System.nanoTime()
                delay(125)
                val result = withContext(Dispatchers.Default) { HistorySearch.files(items, FileQueryOptions(sort = HistorySort.NAME)) }
                assertEquals(count, result.size)
                times += (System.nanoTime() - started) / 1_000_000.0
            }
            println("HistorySearch desktop JVM count=$count p95Ms=${times.sorted()[18]} includesDebounce=125 phoneVerified=false")
        }
    }
}
