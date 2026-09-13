package com.bluelink.android.bluetooth

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class PeerDialGateTest {
    private val address = "AA:BB:CC:DD:EE:FF"

    @Test fun simultaneousLocalAndRemoteInitiationOnlyDialsOnce() {
        val gate = PeerDialGate { false }
        val workers = Executors.newFixedThreadPool(2)
        val start = CountDownLatch(1)
        try {
            val calls = List(2) { workers.submit<java.io.Closeable?> { start.await(); gate.tryAcquire(address) } }
            start.countDown()
            val leases = calls.map { it.get(3, TimeUnit.SECONDS) }.filterNotNull()
            assertEquals(1, leases.size)
            leases.single().close()
            assertNotNull(gate.tryAcquire(address))
        } finally { workers.shutdownNow() }
    }

    @Test fun sessionRegistrationKeepsCallbacksBlockedAfterLeaseRelease() {
        var registered = false
        val gate = PeerDialGate { registered }
        val first = requireNotNull(gate.tryAcquire(address))
        registered = true
        first.close()
        assertNull(gate.tryAcquire(address))
        registered = false
        assertNotNull(gate.tryAcquire(address))
    }

    @Test fun differentPeersProceedIndependentlyAndOldReleaseCannotUnlockANewDial() {
        val gate = PeerDialGate { false }
        val first = requireNotNull(gate.tryAcquire(address))
        assertNull(gate.tryAcquire("aa-bb-cc-dd-ee-ff"))
        assertNotNull(gate.tryAcquire("00:11:22:33:44:55"))
        first.close()
        val retry = requireNotNull(gate.tryAcquire(address))
        first.close()
        assertNull(gate.tryAcquire(address))
        retry.close()
        assertNotNull(gate.tryAcquire(address))
    }
}
