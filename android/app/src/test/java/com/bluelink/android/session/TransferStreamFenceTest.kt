package com.bluelink.android.session

import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TransferStatus
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.util.UUID

class TransferStreamFenceTest {
    @Test fun staleControlsCannotAffectNewAttempts() = runBlocking {
        val fence=TransferStreamFence(); val id=UUID.randomUUID()
        val item=TransferItem(id,"file",10,outgoing=false,status=TransferStatus.OFFERED)
        val first=requireNotNull(fence.receive(id,19,true,false,true,true))
        withContext(fence.context(first)) {
            assertEquals(19,fence.streamFor(id,2,true))
            assertEquals(first.attempt,fence.stamp(item).attemptId)
            fence.stamp(item.copy(status=TransferStatus.FAILED))
        }
        assertNull(fence.receive(id,19,true,false,true,true))
        assertNull(fence.receive(id,21,true,true,true,true))
        val second=requireNotNull(fence.receive(id,21,true,false,true,true))
        assertTrue(second.sequence>first.sequence)
        assertNull(fence.receive(id,19,false,false,true,true))
        assertNull(fence.receive(UUID.randomUUID(),21,true,false,true,true))
        assertNull(fence.receive(UUID.randomUUID(),20,true,false,true,true))
        withContext(fence.context(first)) {
            assertEquals(19,fence.streamFor(id,2,true))
            assertEquals(first.attempt,fence.stamp(item).attemptId)
        }
        assertEquals(21,fence.streamFor(id,2,true))
    }
    @Test fun coroutineCaptureSurvivesWorkerSuspension() = runBlocking {
        val fence=TransferStreamFence(); val id=UUID.randomUUID()
        val first=fence.startOutgoing(id,true,true)
        val waiting=CompletableDeferred<Unit>(); val resume=CompletableDeferred<Unit>()
        val old=async(Dispatchers.Default+fence.context(first)) {
            waiting.complete(Unit); resume.await(); fence.streamFor(id,2,true)
        }
        waiting.await()
        val second=fence.startOutgoing(id,true,true)
        resume.complete(Unit)
        assertEquals(first.stream,old.await()); assertNotEquals(first.stream,second.stream)
    }
    @Test fun repeatedRetriesHaveBoundedSessionMemory() {
        val fence = TransferStreamFence(); val id = UUID.randomUUID()
        repeat(16384) { fence.startOutgoing(id, true, true) }
        try { fence.startOutgoing(id, true, true); fail("attempt capacity") } catch (_: IOException) { }
        assertNull(fence.receive(UUID.randomUUID(), 99999, true, false, true, true))
    }
    @Test fun legacyFramingRequiresFreshSessionForRetry() {
        val fence=TransferStreamFence(); val id=UUID.randomUUID()
        assertEquals(2,fence.startOutgoing(id,false,true).stream)
        try { fence.startOutgoing(id,false,true); fail("legacy stream reuse") } catch (_:IOException) { }
        assertEquals(2,TransferStreamFence().startOutgoing(id,false,true).stream)
    }
}
