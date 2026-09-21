package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class SearchResultActionsTest {
    private fun attachment(name: String, state: String = "COMPLETED", uri: String? = "content://qa/file") =
        ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), name, "application/pdf", 2048,
            uri, state, completedBytes = 1024)
    private fun message(vararg attachments: ChatAttachment) = ChatItem(text = "caption", outgoing = false,
        status = MessageStatus.RECEIVED, attachments = attachments.toList())

    @Test fun filenameMatchSelectsTheSameAttachmentForRowAndMenu() {
        val first = attachment("first.pdf")
        val match = attachment("Needle-报告.pdf")
        assertEquals(match, SearchResultActions.attachment(message(first, match), "  NEEDLE  "))
    }

    @Test fun captionAndEmptyQueriesFallBackToFirstAttachment() {
        val first = attachment("first.pdf")
        val item = message(first, attachment("second.pdf"))
        for (query in listOf("caption", "", "   ")) assertEquals(first, SearchResultActions.attachment(item, query))
        assertNull(SearchResultActions.attachment(message(), "caption"))
    }

    @Test fun liveStatePreventsOpeningAStaleCompletedAttachment() {
        val attachment = attachment("first.pdf")
        val message = message(attachment)
        val live = TransferItem(attachment.transferId, attachment.fileName, 2048, outgoing = false,
            status = TransferStatus.TRANSFERRING, completedBytes = 512, peerId = "qa-peer")
        val resolved = SearchResultActions.transfer(message, attachment, listOf(live))
        assertEquals(live.copy(messageId = message.id, attachmentId = attachment.attachmentId), resolved)
        assertFalse(TransferAction.OPEN in TransferActions.available(resolved))
        assertTrue(TransferAction.PAUSE in TransferActions.available(resolved))
    }

    @Test fun unrelatedTransferCannotReplaceSearchTarget() {
        val attachment = attachment("first.pdf")
        val message = message(attachment)
        val other = TransferItem(UUID.randomUUID(), "other.pdf", 99, outgoing = true, status = TransferStatus.PAUSED)
        val resolved = SearchResultActions.transfer(message, attachment, listOf(other))
        assertEquals(attachment.transferId, resolved.id)
        assertEquals(message.id, resolved.messageId)
        assertEquals(attachment.attachmentId, resolved.attachmentId)
        assertEquals(attachment.localUri, resolved.localUri)
        assertEquals(attachment.completedBytes, resolved.completedBytes)
        assertFalse(resolved.outgoing)
    }

    @Test fun historicalFilesRequireBothCompletionAndALocalUri() {
        for (state in TransferStatus.entries) for (uri in listOf(null, "", "content://qa/file")) {
            val attachment = attachment("first.pdf", state.name, uri)
            val resolved = SearchResultActions.transfer(message(attachment), attachment, emptyList())
            for (action in listOf(TransferAction.OPEN, TransferAction.SHARE, TransferAction.SAVE)) {
                assertEquals(state == TransferStatus.COMPLETED && !uri.isNullOrBlank(), action in TransferActions.available(resolved))
            }
        }
    }

    @Test fun unknownHistoricalStateFailsClosed() {
        val attachment = attachment("first.pdf", "FUTURE_STATE")
        val resolved = SearchResultActions.transfer(message(attachment), attachment, emptyList())
        assertEquals(TransferStatus.FAILED, resolved.status)
        assertFalse(TransferAction.OPEN in TransferActions.available(resolved))
        assertFalse(TransferAction.RETRY in TransferActions.available(resolved))
    }
    @Test fun searchMergeKeepsHistoryOnlyMessagesAndUsesLatestLiveState() {
        val historyOnly = message().copy(timestamp = java.time.Instant.ofEpochSecond(1))
        val stale = message().copy(timestamp = java.time.Instant.ofEpochSecond(2))
        val live = stale.copy(text = "updated", status = MessageStatus.FAILED)
        val merged = SearchResultActions.merge(listOf(historyOnly, stale), listOf(live), emptyList())
        assertEquals(listOf(historyOnly, live), merged)
        assertEquals(listOf(historyOnly.id, live.id), MessageBatch.ordered(merged, listOf(historyOnly.id, live.id)).map { it.id })
    }

    @Test fun successfulDeletionCannotResurrectFromOldHistoryButSkippedItemsRemain() {
        val deleted = message()
        val skipped = message()
        val merged = SearchResultActions.merge(listOf(deleted, skipped), listOf(deleted), listOf(deleted.id.toString()))
        assertEquals(listOf(skipped), merged)
        assertEquals(listOf(skipped.id.toString()), MessageBatch.selectRange(merged, listOf(skipped.id.toString()), deleted.id.toString(), skipped.id.toString()))
    }
}
