package com.bluelink.android.domain
import com.bluelink.core.AttachmentRole
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class TransferProgressTest {
    @Test fun waitingAndCommitDoNotReuseTransferBytePercentage() {
        for(status in TransferStatus.entries) {
            val item=TransferItem(UUID.randomUUID(),"QA",100,100,true,status)
            val attachment=ChatAttachment(UUID.randomUUID(),item.id,"QA","application/pdf",100,state=status.name,completedBytes=100)
            val unknown=status in setOf(TransferStatus.OFFERED,TransferStatus.QUEUED,TransferStatus.VERIFYING,TransferStatus.COMMITTING)
            assertEquals(unknown,item.isProgressIndeterminate)
            assertEquals(unknown,attachment.isProgressIndeterminate)
            if(status==TransferStatus.COMMITTING) assertFalse(attachment.canOpen)
        }
    }
    @Test fun summaryCountsOnlyThisPeersActiveOriginalTasksAndAdvancesAfterCompletion() {
        val first=TransferItem(UUID(0,1),"first",10,outgoing=true,status=TransferStatus.COMMITTING,peerId="qa",startedAtEpochMs=1)
        val next=first.copy(id=UUID(0,2),status=TransferStatus.QUEUED,startedAtEpochMs=2)
        val preview=first.copy(id=UUID(0,3),role=AttachmentRole.IMAGE_PREVIEW)
        val other=first.copy(id=UUID(0,4),peerId="other")
        val values=listOf(other,next,preview,first)
        assertEquals(2,DeviceActions.transferCount("qa",values))
        assertEquals(first.id,DeviceActions.transfer("qa",values)?.id)
        val finished=values.map {if(it.id==first.id) it.copy(status=TransferStatus.COMPLETED) else it}
        assertEquals(1,DeviceActions.transferCount("qa",finished))
        assertEquals(next.id,DeviceActions.transfer("qa",finished)?.id)
    }
}
