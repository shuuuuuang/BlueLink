package com.bluelink.android.files

import kotlinx.coroutines.CancellationException

/** Serializes cancellation with the final provider commit, without holding a lock while copying. */
internal class PublicationGuard {
    private var canceled = false
    private var committed = false
    @Synchronized fun cancel(): Boolean {
        if (committed) return false
        canceled = true
        return true
    }
    @Synchronized fun checkpoint() { if (canceled) throw CancellationException("File publication canceled") }
    @Synchronized fun active(action: () -> Unit) { checkpoint(); check(!committed); action() }
    @Synchronized fun <T> commit(action: () -> T): T {
        checkpoint()
        check(!committed) { "File already published" }
        return action().also { committed = true }
    }
}
