package com.bluelink.android.domain

enum class SessionTransport { BLUETOOTH, USB }

object SessionRoute {
    fun preferred(sessions: List<ManagedSessionState>, peerId: String?, usbEnabled: Boolean = true): ManagedSessionState? {
        if (peerId.isNullOrBlank()) return null
        return sessions.filter { it.phase == ConnectionPhase.CONNECTED && it.peerId.equals(peerId, true) && (usbEnabled || it.transport != SessionTransport.USB) }
            .sortedWith(compareByDescending<ManagedSessionState> { it.transport == SessionTransport.USB }
                .thenBy { it.startedAtEpochMs }).firstOrNull()
    }
    // A USB accessory address must never replace the saved Bluetooth rendezvous address.
    fun savedAddress(previous: String?, current: String): String =
        if (current.startsWith("usb:", true)) previous.orEmpty() else current
}
