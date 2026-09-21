package com.bluelink.android.domain

import com.bluelink.core.AttachmentRole
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class BackgroundTransferSummaryTest {
    @Test fun onlyActualFileWorkKeepsCpuAwake() {
        for (state in TransferStatus.entries) {
            val item = TransferItem(UUID.randomUUID(),"file",1024,outgoing=true,status=state,peerId="peer")
            val result = BackgroundTransferSummary.from(listOf(item))
            assertEquals(state in HistoryQuery.activeStatuses,result.activeCount == 1)
            assertEquals(state in setOf(TransferStatus.TRANSFERRING,TransferStatus.RESUMING,TransferStatus.VERIFYING,TransferStatus.COMMITTING),result.needsCpu)
        }
    }
    @Test fun notificationTargetsOldestActiveOriginalAndDoesNotDoubleCountPreview() {
        val first = TransferItem(UUID.randomUUID(),"one",1,outgoing=true,status=TransferStatus.PAUSED,peerId="first",startedAtEpochMs=1)
        val later = first.copy(id=UUID.randomUUID(),peerId="later",startedAtEpochMs=2)
        val preview = first.copy(id=UUID.randomUUID(),role=AttachmentRole.IMAGE_PREVIEW)
        val result = BackgroundTransferSummary.from(listOf(later,preview,first))
        assertEquals(2,result.activeCount); assertEquals("first",result.peerId); assertFalse(result.needsCpu)
    }
}
