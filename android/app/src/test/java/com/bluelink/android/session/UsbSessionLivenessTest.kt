package com.bluelink.android.session

import org.junit.Assert.*
import org.junit.Test

class UsbSessionLivenessTest {
    @Test fun inboundRecordsRenewLeaseAndExactDeadlineExpires() {
        var time = 40L
        val monitor = UsbSessionLiveness { time }
        time += 11999; assertFalse(monitor.expired)
        monitor.received()
        time += 11999; assertFalse(monitor.expired)
        time++; assertTrue(monitor.expired)
    }
    @Test fun localActivityCannotRenewSilentPeer() {
        var time = 0L
        val monitor = UsbSessionLiveness { time }
        repeat(4) { time += UsbSessionLiveness.PING_INTERVAL_MS }
        assertTrue(monitor.expired)
    }
}
