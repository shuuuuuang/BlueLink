package com.bluelink.android.domain

import org.junit.Test
import org.junit.Assert.*
import kotlinx.coroutines.*
import java.time.Instant
import java.util.UUID

class MessageBatchTest {
    private val file = ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), "报告🌍.pdf", "application/pdf", 128,
        "file:///fixture.pdf", "COMPLETED")
    private fun message(n: Long, text: String = "text$n", files: List<ChatAttachment> = emptyList()) =
        ChatItem(UUID(0,n), text, false, Instant.ofEpochSecond(n), MessageStatus.RECEIVED, attachments = files)

    @Test fun copyUsesTimelineOrderWithoutLosingWhitespaceOrFileNames() {
        val items = listOf(message(3,"last  "), message(1,"first\nline"), message(2,"",listOf(file)))
        val ordered = MessageBatch.ordered(items + items, items.map { it.id }.reversed() + UUID.randomUUID())
        assertEquals("first\nline\n[报告🌍.pdf]\nlast  ",MessageBatch.copyText(ordered))
        assertEquals(listOf(file),MessageBatch.attachments(ordered))
        assertEquals(listOf(file),MessageBatch.attachments(listOf(items[2],items[2])))
        assertTrue(MessageBatch.attachments(emptyList()).isEmpty())
        assertEquals(listOf(file),MessageBatch.attachments(listOf(message(4,"caption",listOf(file)))))
    }
    @Test fun deletePreservesEveryActiveAndRecoveryState() {
        for (state in TransferStatus.entries) {
            val item=message(1,"",listOf(file.copy(state=state.name)))
            assertEquals(state !in HistoryQuery.activeStatuses,MessageBatch.canDelete(item))
            assertFalse(MessageBatch.canDelete(item.copy(attachments=listOf(file.copy(state=state.name,recoveryPending=true)))))
        }
        for (state in MessageStatus.entries) assertEquals(state !in setOf(MessageStatus.LOCAL_QUEUED,MessageStatus.SENDING),
            MessageBatch.canDelete(message(2).copy(outgoing=true,status=state)))
    }
    @Test fun deleteReportsMissingActiveAndFailuresThenContinues() = runBlocking {
        val rows=(1L..5L).map {message(it)}.associateBy {it.id}
        val removed=mutableListOf<UUID>()
        val result=MessageBatch.delete(rows.keys.toList()+rows.keys+UUID(0,9), rows::get,
            { it.id == UUID(0,2) }) { id -> if(id==UUID(0,3)) error("disk error"); removed+=id }
        assertEquals(listOf(UUID(0,1),UUID(0,4),UUID(0,5)),removed)
        assertEquals(listOf(null,MessageBatchReason.ACTIVE,MessageBatchReason.FAILED,null,null,MessageBatchReason.MISSING),result.map{it.reason})
    }
    @Test fun cancellationDoesNotBecomeAReportedFailure() = runBlocking {
        try { MessageBatch.delete(listOf(UUID(0,1)),{message(1)},{false}) { throw CancellationException() }; fail() }
        catch (expected: CancellationException) { }
    }
    @Test fun rangeSelectionWorksInBothDirectionsAndPreservesOtherChoices() {
        val rows=(1L..8L).map { message(it) }; fun id(n:Int)=rows[n-1].id.toString()
        val selected=listOf(id(4),id(8))
        assertEquals(listOf(id(4),id(8),id(2),id(3)),MessageBatch.selectRange(rows,selected,id(4),id(2)))
        assertEquals(listOf(id(4),id(8),id(5),id(6)),MessageBatch.selectRange(rows,selected,id(4),id(6)))
        assertEquals(selected,MessageBatch.selectRange(rows,selected,"missing",id(1)))
        assertEquals(0,MessageBatch.rangeTarget(rows,selected,id(4),0,1))
        assertEquals(7,MessageBatch.rangeTarget(rows,selected,id(4),5,7))
        assertNull(MessageBatch.rangeTarget(rows,rows.map{it.id.toString()},id(4),0,7))
        assertNull(MessageBatch.rangeTarget(rows,selected,null,0,7))
    }
    @Test fun rangeEndpointsMustFitCompletelyIncludingSinglePixelClipping() {
        assertTrue(MessageBatch.fullyVisible(-20, 100, -20, 80))
        assertTrue(MessageBatch.fullyVisible(0, 40, -20, 80))
        assertFalse(MessageBatch.fullyVisible(-21, 100, -20, 80))
        assertFalse(MessageBatch.fullyVisible(-20, 101, -20, 80))
        assertFalse(MessageBatch.fullyVisible(-119, 100, -20, 80))
        assertFalse(MessageBatch.fullyVisible(79, 100, -20, 80))
        assertFalse(MessageBatch.fullyVisible(-21, 102, -20, 80))
        assertFalse(MessageBatch.fullyVisible(0, 0, -20, 80))
        assertFalse(MessageBatch.fullyVisible(0, 1, 0, 0))
        assertFalse(MessageBatch.fullyVisible(Int.MAX_VALUE - 10, 20, 0, Int.MAX_VALUE))
    }

    @Test fun rangeSelectionSkipsClippedRowsAtBothViewportEdges() {
        val rows = (1L..8L).map { message(it) }
        val visible = listOf(Triple(1, -1, 60), Triple(2, 70, 60), Triple(3, 140, 60), Triple(4, 210, 60))
        val complete = visible.filter { MessageBatch.fullyVisible(it.second, it.third, 0, 269) }
        fun target(anchor: Int) = MessageBatch.rangeTarget(rows, listOf(rows[anchor].id.toString()), rows[anchor].id.toString(),
            complete.firstOrNull()?.first ?: -1, complete.lastOrNull()?.first ?: -1)
        assertEquals(2, target(7))
        assertEquals(3, target(0))
        val selected = MessageBatch.selectRange(rows, listOf(rows[0].id.toString()), rows[0].id.toString(), rows[target(0)!!].id.toString())
        assertFalse(rows[4].id.toString() in selected)
        val none = visible.filter { MessageBatch.fullyVisible(it.second, it.third, 60, 120) }
        assertTrue(none.isEmpty())
        assertNull(MessageBatch.rangeTarget(rows, emptyList(), rows[0].id.toString(),
            none.firstOrNull()?.first ?: -1, none.lastOrNull()?.first ?: -1))
    }

}
