package com.bluelink.android.bluetooth

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** One bounded scan per request. A cancelled or late callback cannot affect a newer scan. */
internal class SingleDiscoveryScan(
    private val scope: CoroutineScope,
    private val startRadio: ((() -> Unit) -> Unit, (String) -> Unit) -> Unit,
    private val stopRadio: () -> Unit,
    private val onStarted: () -> Unit,
    private val onTick: () -> Unit,
    private val onFinished: (Boolean, String?) -> Unit,
    private val ticks: Int = 5,
    private val waitForTick: suspend () -> Unit = { delay(1_000) },
) {
    private var active: Any? = null
    private var job: Job? = null

    @Synchronized
    fun start(): Boolean {
        if (active != null || !scope.isActive) return false
        val request = Any()
        active = request
        onStarted()
        val task = scope.launch(start = CoroutineStart.LAZY) {
            try {
                synchronized(this@SingleDiscoveryScan) {
                    if (active !== request) return@launch
                    startRadio({ action ->
                        synchronized(this@SingleDiscoveryScan) { if (active === request) action() }
                    }, { failure -> finish(request, failure) })
                }
                repeat(ticks) {
                    waitForTick()
                    synchronized(this@SingleDiscoveryScan) {
                        if (active !== request) return@launch
                        onTick()
                    }
                }
                finish(request, null, completed = true)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                finish(request, failure.message ?: failure.javaClass.simpleName)
            }
        }
        job = task
        task.invokeOnCompletion { finish(request, null) }
        task.start()
        return true
    }

    @Synchronized
    fun stop() { active?.let { finish(it, null) } }

    @Synchronized
    private fun finish(request: Any, failure: String?, completed: Boolean = false) {
        if (active !== request) return
        active = null
        val previous = job
        job = null
        // Stop the radio before allowing the next request to start.
        runCatching(stopRadio)
        onFinished(completed, failure)
        previous?.cancel()
    }
}
