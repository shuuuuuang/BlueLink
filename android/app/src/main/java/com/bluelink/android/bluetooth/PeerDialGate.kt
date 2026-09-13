package com.bluelink.android.bluetooth

import java.io.Closeable
import java.util.Locale

/** Both local initiation and a Windows callback must share the same RFCOMM dial lease. */
internal class PeerDialGate(private val hasActiveConnection: (String) -> Boolean) {
    private val leases = mutableMapOf<String, Any>()

    @Synchronized
    fun tryAcquire(address: String): Closeable? {
        val key = address.replace(":", "").replace("-", "").uppercase(Locale.ROOT)
        require(key.matches(Regex("[0-9A-F]{12}")))
        if (key in leases || hasActiveConnection(address)) return null
        val token = Any()
        leases[key] = token
        return Closeable { synchronized(this) { if (leases[key] === token) leases.remove(key) } }
    }
}
