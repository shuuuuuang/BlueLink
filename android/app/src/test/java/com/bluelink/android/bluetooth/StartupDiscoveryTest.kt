package com.bluelink.android.bluetooth

import org.junit.Assert.*
import org.junit.Test

class StartupDiscoveryTest {
    @Test fun waitsForPersistedSettingsBeforeScanning() {
        val startup = StartupDiscovery()
        assertFalse(startup.claim(false, true, true))
        assertTrue(startup.claim(true, true, true))
    }

    @Test fun foregroundAndSettingsUpdatesDoNotRepeatStartupScan() {
        val startup = StartupDiscovery()
        assertTrue(startup.claim(true, true, true))
        repeat(20) { assertFalse(startup.claim(true, true, true)) }
    }

    @Test fun firstPermissionGrantOrBluetoothEnableCanCompleteDeferredStartup() {
        val startup = StartupDiscovery()
        assertFalse(startup.claim(true, true, false))
        assertTrue(startup.claim(true, true, true))
        assertFalse(startup.claim(true, true, false))
        assertFalse(startup.claim(true, true, true))
    }

    @Test fun disabledStartupSettingRemainsHandledUntilNextProcess() {
        val startup = StartupDiscovery()
        assertFalse(startup.claim(true, false, true))
        assertFalse(startup.claim(true, true, true))
        assertTrue(StartupDiscovery().claim(true, true, true))
    }

    @Test fun manualRefreshConsumesPendingStartupScan() {
        val startup = StartupDiscovery()
        startup.manualRequest()
        assertFalse(startup.claim(true, true, true))
    }
}
