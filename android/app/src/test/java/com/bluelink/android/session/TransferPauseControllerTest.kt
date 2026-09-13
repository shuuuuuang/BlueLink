package com.bluelink.android.session

import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TransferStatus
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.util.UUID

class TransferPauseControllerTest {
    private fun item(outgoing: Boolean = false) = TransferItem(UUID.randomUUID(), "qa.pdf", 1_000,
        300, outgoing, TransferStatus.TRANSFERRING)

    @Test fun remotePauseIsProjectedForBothDirectionsAndSurvivesInFlightProgress() {
        for (outgoing in listOf(false, true)) {
            val reports = mutableListOf<TransferItem>()
            val controller = TransferPauseController(reports::add)
            controller.report(item(outgoing))
            assertTrue(controller.setPaused(local = false, paused = true))
            controller.report(controller.item!!.copy(status = TransferStatus.TRANSFERRING, completedBytes = 400))
            assertEquals(TransferStatus.REMOTE_PAUSED, reports.last().status)
            assertEquals(400L, reports.last().completedBytes)
            controller.setPaused(local = false, paused = false)
            assertEquals(TransferStatus.RESUMING, reports.last().status)
        }
    }

    @Test fun peerResumeCannotOverrideLocalPause() {
        val controller = TransferPauseController {}
        controller.report(item())
        controller.setPaused(local = true, paused = true)
        controller.setPaused(local = false, paused = true)
        controller.setPaused(local = false, paused = false)
        assertEquals(TransferStatus.PAUSED, controller.item!!.status)
        controller.setPaused(local = true, paused = false)
        assertEquals(TransferStatus.RESUMING, controller.item!!.status)
    }

    @Test fun localResumeKeepsWaitingUntilPeerAlsoResumes() = runBlocking {
        val controller = TransferPauseController {}
        controller.report(item(true))
        controller.setPaused(local = true, paused = true)
        controller.setPaused(local = false, paused = true)
        val sender = async(start = CoroutineStart.UNDISPATCHED) { controller.awaitResumed(); true }
        assertFalse(sender.isCompleted)
        controller.setPaused(local = true, paused = false)
        assertEquals(TransferStatus.REMOTE_PAUSED, controller.item!!.status)
        assertFalse(sender.isCompleted)
        controller.setPaused(local = false, paused = false)
        assertTrue(withTimeout(2_000) { sender.await() })
    }

    @Test fun repeatedPauseDoesNotLoseTheWaitingSender() = runBlocking {
        val controller = TransferPauseController {}
        controller.report(item(true))
        controller.setPaused(local = false, paused = true)
        val sender = async(start = CoroutineStart.UNDISPATCHED) { controller.awaitResumed(); true }
        controller.setPaused(local = false, paused = true)
        controller.setPaused(local = false, paused = false)
        assertTrue(withTimeout(2_000) { sender.await() })
    }

    @Test fun failureReleasesPausedWaitAndPreventsFurtherControl() = runBlocking {
        val controller = TransferPauseController {}
        controller.report(item(true))
        controller.setPaused(local = true, paused = true)
        val sender = async(start = CoroutineStart.UNDISPATCHED) {
            try { controller.awaitResumed(); null } catch (failure: IOException) { failure }
        }
        val failure = IOException("isolated test failure")
        controller.fail(failure)
        val caught = withTimeout(2_000) { sender.await() }
        assertEquals(failure.message, caught?.message)
        assertEquals(IOException::class.java, caught?.javaClass)
        assertFalse(controller.setPaused(local = false, paused = false))
    }

    @Test fun finalizationAndTerminalStatesIgnoreLateControlAndProgress() {
        for (status in listOf(TransferStatus.VERIFYING, TransferStatus.COMMITTING,
            TransferStatus.COMPLETED, TransferStatus.CANCELED, TransferStatus.FAILED)) {
            val reports = mutableListOf<TransferItem>()
            val controller = TransferPauseController(reports::add)
            val update = item()
            controller.report(update.copy(status = status))
            assertFalse(controller.setPaused(local = true, paused = true))
            assertFalse(controller.setPaused(local = false, paused = false))
            controller.report(update)
            assertEquals(status, controller.item!!.status)
            assertEquals(1, reports.size)
        }
    }
}
