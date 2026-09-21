package com.bluelink.android.session

import com.bluelink.android.domain.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class TransferAttemptRegistryTest {
    private fun item(id: UUID, status: TransferStatus) = TransferItem(id, "qa.bin", 100, outgoing = true, status = status)
    @Test fun terminalNotificationDoesNotReleaseOwnershipBeforeWorkerCleanup() = runBlocking {
        val registry = TransferAttemptRegistry()
        val id = UUID.randomUUID()
        val entered = CompletableDeferred<Unit>(); val finish = CompletableDeferred<Unit>()
        val worker = launch {
            val lease = registry.acquire(id)!!
            try { lease.publish(item(id, TransferStatus.CANCELED)) { }; entered.complete(Unit); finish.await() }
            finally { lease.close() }
        }
        entered.await()
        assertNull(registry.acquire(id))
        registry.acquire(UUID.randomUUID())!!.close()
        finish.complete(Unit); worker.join()
        registry.acquire(id)!!.close()
    }
    @Test fun retryHasNewIdentityAndLateReportsCannotReverseItsTerminalState() {
        val registry = TransferAttemptRegistry(); val ledger = SessionTransferLedger()
        val id = UUID.randomUUID(); val old = registry.acquire(id)!!
        val published = mutableListOf<TransferItem>()
        val record: (TransferItem) -> Unit = { ledger.record(it)?.let(published::add) }
        old.publish(item(id, TransferStatus.QUEUED), record)
        val delayed = published.single()
        old.publish(item(id, TransferStatus.CANCELED), record)
        assertNull(ledger.record(delayed.copy(status = TransferStatus.COMPLETED)))
        assertNull(registry.acquire(id)); old.close()
        val current = registry.acquire(id)!!
        assertNotEquals(old.id, current.id)
        current.publish(item(id, TransferStatus.QUEUED), record)
        assertNull(ledger.record(delayed))
        assertNull(ledger.record(delayed.copy(status = TransferStatus.FAILED)))
        old.publish(item(id, TransferStatus.COMPLETED), record)
        current.publish(item(id, TransferStatus.COMPLETED), record)
        current.publish(item(id, TransferStatus.FAILED), record)
        assertEquals(listOf(TransferStatus.QUEUED, TransferStatus.CANCELED, TransferStatus.QUEUED, TransferStatus.COMPLETED), published.map { it.status })
        assertTrue(ledger.close().isEmpty()); current.close(); old.close()
    }
    @Test fun canceledScopeStillReleasesPreparationOwnership() = runBlocking {
        val registry = TransferAttemptRegistry(); val id = UUID.randomUUID(); val lease = registry.acquire(id)!!
        val scope = CoroutineScope(Job().also { it.cancel() } + Dispatchers.Unconfined)
        PreparedSubmission.start<TransferItem>(scope, {}, {}) { error("Must not execute") }
            .also { it.invokeOnCompletion { lease.close() } }.join()
        assertNotNull(registry.acquire(id))
    }
    @Test fun unseenOldQueueCannotReplaceANewerAttempt() {
        val registry = TransferAttemptRegistry(); val ledger = SessionTransferLedger()
        val id = UUID.randomUUID(); val old = registry.acquire(id)!!
        lateinit var delayed: TransferItem
        old.publish(item(id, TransferStatus.QUEUED)) { delayed = it }; old.close()
        val newer = registry.acquire(id)!!
        newer.publish(item(id, TransferStatus.QUEUED)) { ledger.record(it) }; newer.close()
        assertNull(ledger.record(delayed))
        assertNull(ledger.record(delayed.copy(status = TransferStatus.COMPLETED)))
    }
    @Test fun retryRequiresCurrentTrustConnectionSamePeerAndTerminalState() {
        val task = item(UUID.randomUUID(), TransferStatus.FAILED).copy(peerId = "peer-a")
        assertTrue(TransferRetryEligibility.allows(task, "PEER-A", true, true))
        assertFalse(TransferRetryEligibility.allows(task, "peer-b", true, true))
        assertFalse(TransferRetryEligibility.allows(task, "peer-a", true, false))
        assertFalse(TransferRetryEligibility.allows(task, "peer-a", false, true))
        assertFalse(TransferRetryEligibility.allows(task, null, true, true))
        assertFalse(TransferRetryEligibility.allows(task.copy(outgoing = false), "peer-a", true, true))
        TransferStatus.values().forEach { status ->
            assertEquals(status in setOf(TransferStatus.FAILED, TransferStatus.REJECTED, TransferStatus.CANCELED),
                TransferRetryEligibility.allows(task.copy(status = status), "peer-a", true, true))
        }
    }
}
