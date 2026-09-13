package com.bluelink.android.data.local

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Register each write before returning to a sender that can immediately receive a reply. */
internal class MessagePersistenceQueue(private val scope: CoroutineScope) {
    private val mutex = Mutex()

    fun enqueue(block: suspend () -> Unit) = scope.launch(start = CoroutineStart.UNDISPATCHED) {
        run(block)
    }

    suspend fun <T> run(block: suspend () -> T): T = mutex.withLock { block() }
}
