package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class SearchExcerptTest {
    @Test fun showsLateMatchAndKeepsOriginalInput() {
        val source = "开头".repeat(100) + "Needle-needle" + "结尾".repeat(100)
        val result = SearchExcerpt.create(source, " NEEDLE ", 40)
        assertTrue(result.text.startsWith("…"))
        assertTrue(result.text.endsWith("…"))
        assertEquals(listOf("Needle", "needle"), result.highlights.map { result.text.substring(it) })
        assertEquals(413, source.length)
    }

    @Test fun preservesExtensionAndHighlightsFilenameNearItsEnd() {
        val source = "报告".repeat(80) + "Needle" + "后续".repeat(20) + ".pdf"
        val result = SearchExcerpt.create(source, "needle", 28, true)
        assertTrue(result.text.contains("Needle"))
        assertTrue(result.text.endsWith(".pdf"))
        assertEquals("Needle", result.text.substring(result.highlights.single()))
    }

    @Test fun neverSplitsEmojiOrCombiningCharacters() {
        val source = "👩‍💻e\u0301🇨🇳".repeat(40)
        for (budget in 1..24) {
            val result = SearchExcerpt.create(source, "", budget)
            val shown = result.text.removeSuffix("…")
            assertTrue(source.startsWith(shown))
            assertFalse(shown.endsWith("\u200d"))
            assertEquals(budget, Regex("\\X").findAll(shown).count())
        }
    }

    @Test fun clipsVeryLongMatchButRetainsVisibleHighlight() {
        val source = "x".repeat(100)
        val result = SearchExcerpt.create(source, source, 12)
        assertEquals("x".repeat(12) + "…", result.text)
        assertEquals(0 until 12, result.highlights.single())
    }

    @Test fun emptyWhitespaceAndLineBreaksRemainDisplayOnly() {
        assertEquals("", SearchExcerpt.create("", "needle").text)
        assertTrue(SearchExcerpt.create("report.pdf", " ", fileName = true).highlights.isEmpty())
        val result = SearchExcerpt.create("a\r\nb", "a\r\nb")
        assertEquals("a  b", result.text)
        assertEquals(0 until 4, result.highlights.single())
    }

    @Test fun attachmentCaptionMatchUsesBodyWithoutChangingFileActionTarget() {
        val attachment = ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), "report.pdf", "application/pdf", 10)
        val message = ChatItem(text = "late Needle caption", outgoing = false, status = MessageStatus.RECEIVED, attachments = listOf(attachment))
        assertFalse(SearchResultActions.showsFilename(message, attachment, "needle"))
        assertTrue(SearchResultActions.showsFilename(message, attachment, "report"))
        assertTrue(SearchResultActions.showsFilename(message, attachment, ""))
        assertEquals(attachment, SearchResultActions.attachment(message, "needle"))
    }

    @Test fun terminalFiltersAreDistinctWithoutRemovingCanceledRetry() {
        val items = TransferStatus.entries.map { TransferItem(UUID.randomUUID(), "report.pdf", 10, 0,
            true, it, localUri = "file:///qa/report.pdf") }
        fun selected(filter: FileStatusFilter) = HistoryQuery.files(items, "", filter, FileDirectionFilter.ALL, null).map { it.status }.toSet()
        assertEquals(setOf(TransferStatus.FAILED), selected(FileStatusFilter.FAILED))
        assertEquals(setOf(TransferStatus.REJECTED), selected(FileStatusFilter.REJECTED))
        assertEquals(setOf(TransferStatus.CANCELED), selected(FileStatusFilter.CANCELED))
        assertEquals(setOf(TransferStatus.FAILED, TransferStatus.REJECTED, TransferStatus.CANCELED), selected(FileStatusFilter.INCOMPLETE))
        val canceled = items.single { it.status == TransferStatus.CANCELED }
        assertTrue(TransferActions.available(canceled).contains(TransferAction.RETRY))
        assertFalse(TransferActions.available(canceled).contains(TransferAction.FAILURE))
        assertFalse(TransferActions.available(canceled.copy(outgoing = false)).contains(TransferAction.RETRY))
    }
}
