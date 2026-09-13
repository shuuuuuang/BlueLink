package com.bluelink.android.data.local

import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test

class MessagePersistenceQueueTest {
    @Test fun immediateReceiptWaitsForInitialInsertAndDeleteStaysAfterBoth() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        try {
            val queue = MessagePersistenceQueue(scope)
            val releaseInsert = CompletableDeferred<Unit>()
            val events = mutableListOf<String>()
            val insert = queue.enqueue { releaseInsert.await(); events += "insert" }
            val receipt = queue.enqueue { events += "read" }
            val delete = queue.enqueue { events += "delete" }
            assertTrue(events.isEmpty())
            releaseInsert.complete(Unit)
            withTimeout(5_000) { joinAll(insert, receipt, delete) }
            assertEquals(listOf("insert", "read", "delete"), events)
        } finally { scope.cancel() }
    }

    @Test fun failedWriteReleasesQueueForLaterActions() = runBlocking {
        val failure = CompletableDeferred<Throwable>()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default + CoroutineExceptionHandler { _, e -> failure.complete(e) })
        try {
            val queue = MessagePersistenceQueue(scope)
            queue.enqueue { error("isolated write failure") }
            val next = CompletableDeferred<Boolean>()
            queue.enqueue { next.complete(true) }
            withTimeout(5_000) {
                assertEquals("isolated write failure", failure.await().message)
                assertTrue(next.await())
            }
        } finally { scope.cancel() }
    }
}
