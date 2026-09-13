package com.bluelink.android.session

import kotlinx.coroutines.*
import java.util.concurrent.atomic.AtomicLong

/** Only authenticated inbound records renew the USB peer's monotonic lease. */
internal class UsbSessionLiveness(private val clock: () -> Long = { System.nanoTime() / 1_000_000 }) {
    private val received = AtomicLong(clock())
    fun received() { received.set(clock()) }
    val expired: Boolean get() = clock() - received.get() >= TIMEOUT_MS

    suspend fun run(ping: () -> Deferred<Unit>, lost: (String) -> Unit) {
        var pending: Deferred<Unit>? = null
        try {
            while (currentCoroutineContext().isActive) {
                delay(PING_INTERVAL_MS)
                if (expired) { lost("USB 对端已无响应，连接已断开。"); return }
                // Never let a blocked writer stall the receive deadline or grow the queue.
                if (pending?.isCompleted == false) continue
                pending?.await()
                pending = ping()
            }
        } catch (failure: CancellationException) { throw failure }
        catch (_: Exception) { if (currentCoroutineContext().isActive) lost("USB 心跳发送失败，连接已断开。") }
    }
    companion object {
        const val PING_INTERVAL_MS = 3000L
        const val TIMEOUT_MS = 12000L
    }
}
