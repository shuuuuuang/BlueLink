package com.bluelink.android.bluetooth

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import org.junit.Assert.*
import org.junit.Test

class SingleDiscoveryScanTest {
    private class Fixture(throwOnStart: Boolean = false) : AutoCloseable {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val clock = Channel<Unit>(Channel.UNLIMITED)
        var starts = 0
        var stops = 0
        var observations = 0
        var refreshing = false
        val failures = mutableListOf<(String) -> Unit>()
        val results = mutableListOf<(() -> Unit) -> Unit>()
        val finishes = mutableListOf<Pair<Boolean, String?>>()
        val scan = SingleDiscoveryScan(scope,
            startRadio = { accept, failed ->
                starts++
                results += accept
                failures += failed
                if (throwOnStart) error("radio unavailable")
            }, stopRadio = { stops++ }, onStarted = { refreshing = true },
            onTick = {}, onFinished = { completed, failure ->
                refreshing = false
                finishes += completed to failure
            }, waitForTick = { clock.receive() })
        fun tick(count: Int) { repeat(count) { check(clock.trySend(Unit).isSuccess) } }
        override fun close() { scope.cancel() }
    }

    @Test fun oneRequestStopsAfterOneWindowAndNeverRestartsOnFurtherTicks() = Fixture().use {
        assertTrue(it.scan.start())
        assertTrue(it.refreshing)
        it.tick(4)
        assertEquals(0, it.stops)
        it.tick(1)
        assertFalse(it.refreshing)
        assertEquals(listOf(true to null), it.finishes)
        it.tick(100)
        assertEquals(1, it.starts)
        assertEquals(1, it.stops)
    }

    @Test fun repeatedPullsDoNotRestartOrExtendAnActiveWindow() = Fixture().use {
        it.scan.start()
        it.tick(2)
        repeat(5) { _ -> assertFalse(it.scan.start()) }
        it.tick(3)
        assertFalse(it.refreshing)
        assertEquals(1, it.starts)
        assertTrue(it.scan.start())
        assertEquals(2, it.starts)
    }

    @Test fun stoppedScanIgnoresLateResultsAndFailureDuringNewScan() = Fixture().use {
        it.scan.start()
        val oldResult = it.results.single()
        val oldFailure = it.failures.single()
        it.scan.stop()
        assertEquals(listOf(false to null), it.finishes)
        it.scan.start()
        oldResult { it.observations++ }
        oldFailure("late failure")
        assertTrue(it.refreshing)
        assertEquals(0, it.observations)
        it.results.last().invoke { it.observations++ }
        assertEquals(1, it.observations)
        assertEquals(1, it.stops)
    }

    @Test fun callbackFailureStopsRefreshAndAllowsManualRetry() = Fixture().use {
        it.scan.start()
        it.failures.single()("scan failed")
        assertFalse(it.refreshing)
        assertEquals(listOf(false to "scan failed"), it.finishes)
        assertEquals(1, it.stops)
        assertTrue(it.scan.start())
    }

    @Test fun synchronousStartFailureReleasesRadioAndRefresh() = Fixture(throwOnStart = true).use {
        it.scan.start()
        assertFalse(it.refreshing)
        assertEquals(listOf(false to "radio unavailable"), it.finishes)
        assertEquals(1, it.stops)
    }

    @Test fun connectionOrBackgroundStopDoesNotTriggerAnotherScan() = Fixture().use {
        it.scan.start()
        it.scan.stop()
        it.scan.stop()
        it.tick(100)
        assertFalse(it.refreshing)
        assertEquals(1, it.starts)
        assertEquals(1, it.stops)
    }

    @Test fun runtimeCancellationCleansUpRadioAndCannotRestart() = Fixture().use {
        it.scan.start()
        it.scope.cancel()
        assertFalse(it.refreshing)
        assertEquals(1, it.stops)
        assertFalse(it.scan.start())
    }
}
