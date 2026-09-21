package com.bluelink.android.session

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CancellationException

internal class QueuedRouteGate {
    private var state=0 // waiting, switching, started, stopped
    private var decision: CompletableDeferred<Boolean>?=null
    @Synchronized fun request(): CompletableDeferred<Boolean>? {
        if(state != 0) return null
        state=1
        return CompletableDeferred<Boolean>().also { decision=it }
    }
    @Synchronized fun resolve(accepted: Boolean) {
        if(state != 1) return
        state=if(accepted) 3 else 0
        decision!!.complete(accepted)
    }
    suspend fun start() {
        while(true) {
            val pending=synchronized(this) {
                when(state) {
                    0 -> { state=2; return }
                    1 -> requireNotNull(decision)
                    else -> throw CancellationException("USB queue ended")
                }
            }
            pending.await()
        }
    }
}
