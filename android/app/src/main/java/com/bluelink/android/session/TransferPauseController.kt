package com.bluelink.android.session

import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TransferStatus
import kotlinx.coroutines.CompletableDeferred

/** Local and remote pause requests are independent; neither side can clear the other's pause. */
internal class TransferPauseController(private val notify: (TransferItem) -> Unit) {
    private val lock = Any()
    private var localPaused = false
    private var remotePaused = false
    private var failure: Throwable? = null
    private var resumed = CompletableDeferred<Unit>().also { it.complete(Unit) }
    @Volatile var item: TransferItem? = null
        private set

    fun report(update: TransferItem) = synchronized(lock) {
        // A late block acknowledgement or canceled worker must not replace a final result.
        if (item?.status in setOf(TransferStatus.COMPLETED, TransferStatus.CANCELED, TransferStatus.FAILED, TransferStatus.REJECTED)) return@synchronized
        if (item?.status?.let { it !in pausable } == true && update.status in pausable) return@synchronized
        publish(update)
    }

    fun setPaused(local: Boolean, paused: Boolean): Boolean = synchronized(lock) {
        val current = item ?: return@synchronized false
        if (failure != null || current.status !in pausable) return@synchronized false
        if ((if (local) localPaused else remotePaused) == paused) return@synchronized false
        if (local) localPaused = paused else remotePaused = paused
        if (localPaused || remotePaused) {
            if (resumed.isCompleted) resumed = CompletableDeferred()
        } else resumed.complete(Unit)
        publish(current.copy(status = TransferStatus.RESUMING))
        true
    }

    private fun publish(update: TransferItem) {
        val status = if (update.status !in pausable) update.status else when {
            localPaused -> TransferStatus.PAUSED
            remotePaused -> TransferStatus.REMOTE_PAUSED
            else -> update.status
        }
        val projected = update.copy(status = status)
        item = projected
        notify(projected)
    }

    suspend fun awaitResumed() {
        while (true) {
            val signal = synchronized(lock) {
                failure?.let { throw it }
                if (!localPaused && !remotePaused) return
                resumed
            }
            signal.await()
        }
    }

    fun fail(cause: Throwable) = synchronized(lock) {
        failure = cause
        resumed.completeExceptionally(cause)
    }

    private companion object {
        val pausable = setOf(TransferStatus.OFFERED, TransferStatus.QUEUED, TransferStatus.TRANSFERRING,
            TransferStatus.PAUSED, TransferStatus.REMOTE_PAUSED, TransferStatus.RESUMING)
    }
}
