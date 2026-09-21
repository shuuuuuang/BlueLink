package com.bluelink.android.domain
import org.junit.Test
import org.junit.Assert.*
import kotlinx.coroutines.*
import java.util.UUID
class FileBatchTest {
    @Test fun actionMatrixPreservesDirectionStateAndReadability() {
        for(status in TransferStatus.entries) for(outgoing in listOf(true,false)) for(online in listOf(true,false)) for(readable in listOf(true,false)) {
            val item=TransferItem(UUID.randomUUID(),"QA.pdf",10,outgoing=outgoing,status=status)
            fun eligible(action:FileBatchAction)=FileBatchPolicy.check(item.id,item,action,online,readable).eligible
            assertEquals(outgoing && online && readable && status in setOf(TransferStatus.FAILED,TransferStatus.REJECTED,TransferStatus.CANCELED),eligible(FileBatchAction.RETRY))
            assertEquals(online && status in HistoryQuery.activeStatuses,eligible(FileBatchAction.CANCEL))
            assertEquals(status !in HistoryQuery.activeStatuses,eligible(FileBatchAction.DELETE_RECORDS))
            assertEquals(status==TransferStatus.COMPLETED && readable,eligible(FileBatchAction.SHARE))
        }
    }
    @Test fun changedStateAndPartialFailureNeverStopOtherItems()=runBlocking {
        val ids=(0L..3L).map {UUID(0,it)};val sent=mutableListOf<UUID>()
        val result=FileBatchRunner.run(ids+ids,{FileBatchCheck(it,"QA",it!=ids[1],FileBatchReason.STATE_CHANGED)}) {
            sent+=it; if(it==ids[2]) error("Injected item failure")
        }
        assertEquals(listOf(FileBatchOutcome.SUBMITTED,FileBatchOutcome.SKIPPED,FileBatchOutcome.FAILED,FileBatchOutcome.SUBMITTED),result.map {it.outcome})
        assertEquals(listOf(ids[0],ids[2],ids[3]),sent)
    }
    @Test fun missingAndOversizedSelectionsNeverExecute()=runBlocking {
        assertFalse(FileBatchPolicy.check(UUID.randomUUID(),null,FileBatchAction.DELETE_RECORDS,true,true).eligible)
        try { FileBatchRunner.run((0L..100L).map {UUID(0,it)},{error("must not check")},{error("must not run")}); fail() }
        catch(expected:IllegalArgumentException) { }
    }
}
