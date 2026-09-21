package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID

class HistoryQueryTest {
    private val instant = Instant.parse("2026-09-05T16:30:00Z")
    private fun attachment(name: String, mime: String = "application/pdf", state: String = "COMPLETED") =
        ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), name, mime, 12, "file:///qa/$name", state)
    private fun message(text: String, kind: ChatItemKind = ChatItemKind.TEXT, attachments: List<ChatAttachment> = emptyList()) =
        ChatItem(text = text, outgoing = false, timestamp = instant, status = MessageStatus.RECEIVED, kind = kind, attachments = attachments)

    @Test fun queryFindsTextAndAttachmentNamesWithTrimmedCaseInsensitiveInput() {
        val text = message("Review tomorrow").copy(id = UUID(0, 1))
        val file = message("", ChatItemKind.FILE, listOf(attachment("Review.pdf"))).copy(id = UUID(0, 2))
        val image = message("", ChatItemKind.IMAGE, listOf(attachment("other.png", "image/png")))
        assertEquals(listOf(text, file), HistoryQuery.messages(listOf(text, file, image), "  rEvIeW ", HistoryKind.ALL))
        assertTrue(HistoryQuery.messages(listOf(text, file), "missing", HistoryKind.ALL).isEmpty())
        assertEquals(listOf(image), HistoryQuery.messages(listOf(text, file, image), "", HistoryKind.IMAGES))
        assertEquals(listOf(file), HistoryQuery.messages(listOf(text, file, image), "", HistoryKind.FILES))
    }

    @Test fun calendarUsesLocalDateAcrossUtcMidnight() {
        val items = listOf(message("after midnight in Shanghai"))
        assertEquals(items, HistoryQuery.messages(items, "", HistoryKind.DATE, LocalDate.parse("2026-09-06"), ZoneId.of("Asia/Shanghai")))
        assertTrue(HistoryQuery.messages(items, "", HistoryKind.DATE, LocalDate.parse("2026-09-05"), ZoneId.of("Asia/Shanghai")).isEmpty())
        assertEquals(items, HistoryQuery.messages(items, "", HistoryKind.DATE, LocalDate.parse("2026-09-05"), ZoneId.of("UTC")))
    }

    @Test fun fileFiltersCombineStablePeerDirectionStatusAndTimeOrder() {
        fun file(peer: String, state: TransferStatus, outgoing: Boolean, time: Long) = TransferItem(
            UUID.randomUUID(), "Review.pdf", 12, outgoing = outgoing, status = state, peerId = peer, startedAtEpochMs = time)
        val failed = file("peer-a", TransferStatus.FAILED, false, 3)
        val other = file("peer-b", TransferStatus.FAILED, false, 4)
        val sent = file("peer-a", TransferStatus.FAILED, true, 5)
        val paused = file("peer-a", TransferStatus.PAUSED, false, 6)
        val canceled = file("peer-a", TransferStatus.CANCELED, false, 7)
        val items = listOf(failed, other, sent, paused, canceled)
        assertEquals(listOf(canceled, failed), HistoryQuery.files(items, " review ", FileStatusFilter.INCOMPLETE, FileDirectionFilter.RECEIVED, "peer-a"))
        assertEquals(listOf(paused), HistoryQuery.files(items, "", FileStatusFilter.ACTIVE, FileDirectionFilter.ALL, "peer-a"))
        assertEquals(listOf(canceled, paused, sent, other, failed), HistoryQuery.files(items, "", FileStatusFilter.ALL, FileDirectionFilter.ALL, null))
    }

    @Test fun timeSeparatorsFollowFiveMinuteGapsAndLocalDays() {
        assertTrue(HistoryQuery.showTimestamp(null, instant))
        assertFalse(HistoryQuery.showTimestamp(instant.minusSeconds(299), instant))
        assertTrue(HistoryQuery.showTimestamp(instant.minusSeconds(300), instant))
        assertTrue(HistoryQuery.showTimestamp(Instant.parse("2026-09-05T15:59:59Z"), Instant.parse("2026-09-05T16:00:00Z"), ZoneId.of("Asia/Shanghai")))
    }

    @Test fun partialAndFailedFilesCannotBeOpenedEvenIfTheirLocalUriExists() {
        for (state in TransferStatus.entries.filter { it != TransferStatus.COMPLETED })
            assertFalse(state.name, attachment("report.pdf", state = state.name).canOpen)
        assertTrue(attachment("report.pdf").canOpen)
        assertFalse(attachment("report.pdf").copy(localUri = null).canOpen)
    }
}
