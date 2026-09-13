package com.bluelink.android.usb

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.withTimeoutOrNull

/** Changes wake the one discovery worker; periodic announces never extend the fast-retry window. */
internal class MtpProbeSignal(private val clock: () -> Long = { System.nanoTime() / 1_000_000 }) {
    private val wake = Channel<Unit>(Channel.CONFLATED)
    @Volatile private var fastUntil = 0L
    fun request(fastRetry: Boolean = true) {
        if (fastRetry) fastUntil = clock() + 10_000
        wake.trySend(Unit)
    }
    fun delayMillis(enabled: Boolean, ready: Boolean): Long = when {
        !enabled -> 30_000
        !ready && clock() < fastUntil -> 250
        else -> 3000
    }
    suspend fun await(enabled: Boolean, ready: Boolean) {
        withTimeoutOrNull(delayMillis(enabled, ready)) { wake.receive() }
    }
}

/** Missing vendor extras retain polling fallback; an explicit non-MTP state cannot be ready. */
internal fun mtpAvailable(connected: Boolean?, configured: Boolean?, mtp: Boolean?, dataUnlocked: Boolean? = null): Boolean =
    connected != false && configured != false && mtp != false && dataUnlocked != false

/** USB_DATA_UNLOCKED may arrive before the gadget reconfigures. Never bind that transient old interface. */
internal class MtpAvailabilityGate(private val clock: () -> Long = { System.nanoTime() / 1_000_000 }) {
    @Volatile var available = true
        private set
    @Volatile private var stableAfter = clock() + 1200
    val stable: Boolean get() = available && clock() >= stableAfter
    fun update(value: Boolean): Boolean {
        if (available == value) return false
        available = value
        stableAfter = if (value) clock() + 1200 else Long.MAX_VALUE
        return true
    }
}
