package com.bluelink.android.usb

import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test

class MtpProbeSignalTest {
    @Test fun interfaceMustSettleButLossIsImmediate() {
        var time = 100L
        val gate = MtpAvailabilityGate { time }
        assertFalse(gate.stable)
        time += 1200
        assertTrue(gate.stable)
        assertTrue(gate.update(false))
        assertFalse(gate.stable)
        gate.update(true)
        time += 930 // phone exposes data before re-enumerating the gadget
        assertFalse(gate.stable)
        gate.update(false)
        time += 80
        gate.update(true)
        time += 1199
        assertFalse(gate.stable)
        time++
        assertTrue(gate.stable)
        assertFalse(gate.update(true)) // duplicate broadcasts cannot defer readiness forever
        assertTrue(gate.stable)
    }

    @Test fun onlyRecentChangesAccelerateUnreadyDetection() {
        var now = 100L
        val probe = MtpProbeSignal { now }
        assertEquals(3000L, probe.delayMillis(true, false))
        probe.request()
        assertEquals(250L, probe.delayMillis(true, false))
        assertEquals(3000L, probe.delayMillis(true, true))
        assertEquals(30000L, probe.delayMillis(false, false))
        now += 10001
        assertEquals(3000L, probe.delayMillis(true, false))
        probe.request(fastRetry = false)
        assertEquals(3000L, probe.delayMillis(true, false))
    }
    @Test fun eventBeforeOrDuringWaitIsNotLost() = runBlocking {
        val probe = MtpProbeSignal()
        probe.request()
        withTimeout(1000) { probe.await(false, false) }
        val waiting = async { probe.await(false, false) }
        delay(30)
        probe.request()
        withTimeout(1000) { waiting.await() }
    }
    @Test fun burstsCoalesceAndCancellationStopsWaiting() = runBlocking {
        val probe = MtpProbeSignal()
        repeat(1000) { probe.request() }
        withTimeout(1000) { probe.await(false, false) }
        assertNull(withTimeoutOrNull(80) { probe.await(false, false); "unexpected queued wake" })
    }
    @Test fun usbModeUsesSystemExtrasAndMissingExtrasRetainFallback() {
        assertTrue(mtpAvailable(true, true, true))
        assertFalse(mtpAvailable(false, false, false))
        assertFalse(mtpAvailable(true, false, true))
        assertFalse(mtpAvailable(true, true, false))
        assertTrue(mtpAvailable(true, true, null))
        assertTrue(mtpAvailable(null, null, null))
        assertFalse(mtpAvailable(true, true, true, false))
        assertTrue(mtpAvailable(true, true, true, true))
    }
}
