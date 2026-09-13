package com.bluelink.android.domain

/** Startup discovery and reconnection after a live session are separate user choices. */
object AutoConnectionPolicy {
    fun shouldConnect(trusted: Boolean, connectedThisRun: Boolean, manuallyDisconnected: Boolean,
                      autoConnectTrusted: Boolean, reconnectAfterDisconnect: Boolean): Boolean {
        if (!trusted || manuallyDisconnected) return false
        return if (connectedThisRun) reconnectAfterDisconnect else autoConnectTrusted
    }
}
