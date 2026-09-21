package com.bluelink.android.session

import com.bluelink.android.domain.HistoryQuery
import com.bluelink.android.domain.TransferItem
import java.util.UUID

/** One owner until the worker and its stream/snapshot cleanup have actually finished. */
internal class TransferAttemptRegistry {
    companion object { private val sequence = java.util.concurrent.atomic.AtomicLong(); internal fun nextSequence() = sequence.incrementAndGet() }
    private val lock = Any()
    private val owners = mutableMapOf<UUID, Lease>()
    suspend fun awaitRelease(taskId: UUID) { synchronized(lock) { owners[taskId]?.releasedSignal }?.await() }
    fun acquire(taskId: UUID): Lease? = synchronized(lock) {
        if (owners.containsKey(taskId)) null else Lease(taskId).also { owners[taskId] = it }
    }
    inner class Lease internal constructor(private val taskId: UUID) : AutoCloseable {
        val id: UUID = UUID.randomUUID()
        val sequence: Long = nextSequence()
        internal val releasedSignal = kotlinx.coroutines.CompletableDeferred<Unit>()
        private var released = false
        private var terminal = false
        fun publish(value: TransferItem, notify: (TransferItem) -> Unit) {
            val snapshot = synchronized(lock) {
                if (released || terminal) return
                require(value.id == taskId)
                terminal = value.status !in HistoryQuery.activeStatuses
                value.copy(attemptId = id, attemptSequence = sequence)
            }
            notify(snapshot)
        }
        override fun close() = synchronized(lock) {
            if (!released) {
                released = true
                if (owners[taskId] === this) owners.remove(taskId)
                releasedSignal.complete(Unit)
            }
            Unit
        }
    }
}
