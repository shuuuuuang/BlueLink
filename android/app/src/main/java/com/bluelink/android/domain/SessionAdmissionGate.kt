package com.bluelink.android.domain

/** Transport work started before an identity change must not attach afterward. */
class SessionAdmissionGate {
    private var generation = 0L
    private var paused = false
    @Synchronized fun ticket(): Long? = if (paused) null else generation
    @Synchronized fun pause() { paused = true; generation++ }
    @Synchronized fun resume() { paused = false }
    @Synchronized fun <T> admit(ticket: Long, action: () -> T): T? =
        if (!paused && ticket == generation) action() else null
}
