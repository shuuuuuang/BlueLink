package com.bluelink.android.session

import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TransferStatus
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class QueuedRouteGateTest {
    @Test fun acceptedSwitchPreventsOpeningOldStream() = runBlocking {
        val gate = QueuedRouteGate()
        val decision = requireNotNull(gate.request())
        val started = async(start = CoroutineStart.UNDISPATCHED) { runCatching { gate.start() } }
        assertFalse(started.isCompleted)
        assertNull(gate.request())
        gate.resolve(true)
        assertTrue(decision.await())
        assertTrue(started.await().exceptionOrNull() is CancellationException)
        assertNull(gate.request())
    }
    @Test fun deniedSwitchLetsOriginalQueueContinue() = runBlocking {
        val gate = QueuedRouteGate()
        val decision = requireNotNull(gate.request())
        val started = async(start = CoroutineStart.UNDISPATCHED) { gate.start() }
        assertFalse(started.isCompleted)
        gate.resolve(false)
        assertFalse(decision.await())
        started.await()
        assertNull(gate.request())
    }
    @Test fun terminalPublicationDoesNotReleasePhysicalOwner() = runBlocking {
        val registry = TransferAttemptRegistry()
        val id = UUID.randomUUID()
        val lease = requireNotNull(registry.acquire(id))
        val released = async(start = CoroutineStart.UNDISPATCHED) { registry.awaitRelease(id) }
        lease.publish(TransferItem(id, "test", 1, outgoing = true, status = TransferStatus.CANCELED)) {}
        assertFalse(released.isCompleted)
        assertNull(registry.acquire(id))
        lease.close()
        released.await()
        requireNotNull(registry.acquire(id)).close()
    }
}
