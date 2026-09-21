package com.bluelink.android.session

import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test

class PreparedSubmissionTest {
    @Test fun durableHandoffPrecedesAcknowledgementAndWireSubmission() = runBlocking {
        val release = CompletableDeferred<Unit>()
        val events = mutableListOf<String>()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val job = PreparedSubmission.start<String>(scope,
            { events += "persist-start"; release.await(); events += "persisted" },
            { events += "ack:$it" }) { ready -> ready("file"); events += "wire" }
        assertEquals(listOf("persist-start"), events)
        release.complete(Unit); job.join()
        assertEquals(listOf("persist-start", "persisted", "ack:file", "wire"), events)
        scope.cancel()
    }

    @Test fun persistenceFailureNeverSubmitsBytesOrReportsSuccess() = runBlocking {
        val errors = mutableListOf<Throwable>()
        val seen = mutableListOf<String?>()
        var wrote = false
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined + CoroutineExceptionHandler { _, e -> errors += e })
        PreparedSubmission.start<String>(scope, { error("disk full") }, { seen += it }) { ready ->
            ready("file"); wrote = true
        }.join()
        assertFalse(wrote); assertEquals(listOf<String?>(null), seen)
        assertEquals("disk full", errors.single().message); scope.cancel()
    }

    @Test fun canceledScopeCompletesCallbackWithoutEnteringPreparation() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined); scope.cancel()
        val seen = mutableListOf<String?>()
        PreparedSubmission.start<String>(scope, { error("must not persist") }, { seen += it }) {
            error("must not enter")
        }.join()
        assertEquals(listOf<String?>(null), seen)
    }

    @Test fun cancellationDuringPersistenceDoesNotAcknowledgeOrSend() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val seen = mutableListOf<String?>(); var wrote = false
        val job = PreparedSubmission.start<String>(scope, { awaitCancellation() }, { seen += it }) {
            it("file"); wrote = true
        }
        job.cancelAndJoin(); assertFalse(wrote); assertEquals(listOf<String?>(null), seen); scope.cancel()
    }

    @Test fun successIsReportedOnlyOnceEvenWhenLaterWorkIsCanceled() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val seen = mutableListOf<String?>()
        val job = PreparedSubmission.start<String>(scope, {}, { seen += it }) { it("file"); awaitCancellation() }
        job.cancelAndJoin(); assertEquals(listOf("file"), seen); scope.cancel()
    }
}
