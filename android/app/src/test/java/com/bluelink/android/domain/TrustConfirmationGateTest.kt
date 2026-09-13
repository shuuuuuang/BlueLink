package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID
import kotlinx.coroutines.runBlocking

class TrustConfirmationGateTest {
    @Test fun staleDialogCannotAcceptFollowingRequest() = runBlocking {
        val gate = TrustConfirmationGate()
        val oldId = UUID.randomUUID(); val nextId = UUID.randomUUID()
        val old = gate.open(oldId); val next = gate.open(nextId)
        assertFalse(old.await())
        assertFalse(gate.resolve(oldId, true))
        assertFalse(next.isCompleted)
        assertTrue(gate.resolve(nextId, true))
        assertTrue(next.await())
    }
    @Test fun timeoutAndDuplicateActionsCannotChangeDecision() = runBlocking {
        val gate = TrustConfirmationGate(); val id = UUID.randomUUID()
        val decision = gate.open(id); gate.close(id)
        assertFalse(gate.resolve(id, true)); assertFalse(decision.await())
        val nextId = UUID.randomUUID(); val next = gate.open(nextId)
        assertTrue(gate.resolve(nextId, false)); assertFalse(gate.resolve(nextId, true)); assertFalse(next.await())
    }
}
