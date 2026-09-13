package com.bluelink.android.bluetooth

/** Process lifetime, not Activity lifetime: returning from settings never starts a new scan. */
internal class StartupDiscovery {
    private var handled = false

    @Synchronized
    fun claim(settingsLoaded: Boolean, enabled: Boolean, bluetoothReady: Boolean): Boolean {
        if (handled || !settingsLoaded) return false
        if (!enabled) { handled = true; return false }
        if (!bluetoothReady) return false
        handled = true
        return true
    }

    @Synchronized
    fun manualRequest() { handled = true }
}
