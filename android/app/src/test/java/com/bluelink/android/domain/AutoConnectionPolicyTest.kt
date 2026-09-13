package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class AutoConnectionPolicyTest {
    @Test fun initialConnectionUsesOnlyStartupChoice() {
        assertTrue(AutoConnectionPolicy.shouldConnect(true, false, false, true, false))
        assertFalse(AutoConnectionPolicy.shouldConnect(true, false, false, false, true))
    }
    @Test fun disconnectedLiveSessionUsesOnlyReconnectChoice() {
        assertTrue(AutoConnectionPolicy.shouldConnect(true, true, false, false, true))
        assertFalse(AutoConnectionPolicy.shouldConnect(true, true, false, true, false))
    }
    @Test fun manualDisconnectAndUntrustedDevicesNeverAutoConnect() {
        for (connected in listOf(false, true)) {
            assertFalse(AutoConnectionPolicy.shouldConnect(true, connected, true, true, true))
            assertFalse(AutoConnectionPolicy.shouldConnect(false, connected, false, true, true))
        }
    }
}
