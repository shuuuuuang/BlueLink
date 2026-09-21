package com.bluelink.android.session

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import java.util.concurrent.atomic.AtomicBoolean

/** A prepared result is acknowledged only after its durable handoff, including canceled scopes. */
internal object PreparedSubmission {
    fun <T : Any> start(scope: CoroutineScope, beforeQueue: suspend (T) -> Unit,
                        onPrepared: (T?) -> Unit, work: suspend (suspend (T) -> Unit) -> Unit): Job {
        val reported = AtomicBoolean()
        fun report(item: T?) { if(reported.compareAndSet(false,true)) onPrepared(item) }
        return scope.launch {
            work { item ->
                beforeQueue(item)
                currentCoroutineContext().ensureActive()
                report(item)
            }
        }.also { job -> job.invokeOnCompletion { report(null) } }
    }
}
