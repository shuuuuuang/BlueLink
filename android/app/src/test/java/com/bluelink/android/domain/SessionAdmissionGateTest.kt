package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class SessionAdmissionGateTest {
    @Test fun identityChangeRejectsLateTransportAndAllowsFreshConnection() {
        val gate = SessionAdmissionGate()
        val old = requireNotNull(gate.ticket())
        gate.pause()
        assertNull(gate.ticket())
        gate.resume()
        var attached = false
        assertNull(gate.admit(old) { attached = true })
        assertFalse(attached)
        assertEquals("new", gate.admit(requireNotNull(gate.ticket())) { "new" })
    }

    @Test fun failedResetStillInvalidatesOldRequestsWhenAdmissionResumes() {
        val gate = SessionAdmissionGate()
        val old = requireNotNull(gate.ticket())
        gate.pause()
        try { throw java.io.IOException("test persistence failure") }
        catch (_: java.io.IOException) { } finally { gate.resume() }
        assertNull(gate.admit(old) { "unsafe" })
        assertNotNull(gate.ticket())
    }
}
