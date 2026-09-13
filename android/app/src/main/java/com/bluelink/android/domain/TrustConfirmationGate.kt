package com.bluelink.android.domain

import kotlinx.coroutines.CompletableDeferred
import java.util.UUID
import java.util.concurrent.atomic.AtomicReference

/** A late tap from an expired dialog cannot accept a later device/request. */
class TrustConfirmationGate {
    private data class Pending(val id: UUID, val decision: CompletableDeferred<Boolean>)
    private val pending = AtomicReference<Pending?>()

    fun open(id: UUID): CompletableDeferred<Boolean> {
        val next = Pending(id, CompletableDeferred())
        pending.getAndSet(next)?.decision?.complete(false)
        return next.decision
    }

    fun resolve(id: UUID, accepted: Boolean): Boolean {
        val current = pending.get()?.takeIf { it.id == id } ?: return false
        return current.decision.complete(accepted)
    }

    fun cancelAll() { pending.getAndSet(null)?.decision?.complete(false) }

    fun close(id: UUID) {
        val current = pending.get()?.takeIf { it.id == id } ?: return
        if (pending.compareAndSet(current, null)) current.decision.complete(false)
    }
}
