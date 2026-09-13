package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class TransferSessionSelectorTest {
    private fun session(peer: String, phase: ConnectionPhase = ConnectionPhase.CONNECTED) =
        ManagedSessionState(UUID.randomUUID(), peer, peer, "address", phase, "", 0)
    private fun transfer(peer: String? = null) =
        TransferItem(UUID.randomUUID(), "private.txt", 12, outgoing = true, status = TransferStatus.FAILED, peerId = peer)

    @Test fun moreThanEightConnectedPeersKeepIndependentTransferRoutes() {
        val sessions = (1..12).map { session("peer-$it") }
        val owners = sessions.map { it.sessionId }.toSet()
        sessions.forEach { target ->
            assertEquals(target.sessionId, TransferSessionSelector.resolve(transfer(target.peerId), sessions, owners))
            assertEquals(target, SessionRoute.preferred(sessions, target.peerId))
        }
        val disconnected = sessions.first()
        val remaining = sessions.drop(1)
        assertNull(TransferSessionSelector.resolve(transfer(disconnected.peerId), remaining, owners))
        assertEquals(sessions.last(), SessionRoute.preferred(remaining, sessions.last().peerId))
    }

    @Test fun offlinePeerCannotFallBackToAnotherConnectedDevice() {
        val other = session("other")
        assertNull(TransferSessionSelector.resolve(transfer("original"), listOf(other), setOf(other.sessionId)))
    }

    @Test fun knownPeerWinsOverCachedOwnershipAndUsesCaseInsensitiveIdentity() {
        val target = session("ABCD"); val other = session("other")
        assertEquals(target.sessionId, TransferSessionSelector.resolve(transfer("abcd"), listOf(other, target), setOf(other.sessionId)))
    }

    @Test fun legacyTransferNeedsExactlyOneConnectedOwner() {
        val first = session("first"); val second = session("second")
        assertNull(TransferSessionSelector.resolve(transfer(), listOf(first, second), emptySet()))
        assertNull(TransferSessionSelector.resolve(transfer(), listOf(first, second), setOf(first.sessionId, second.sessionId)))
        assertEquals(first.sessionId, TransferSessionSelector.resolve(transfer(), listOf(first, second), setOf(first.sessionId)))
    }

    @Test fun handshakingOrDisconnectedSessionsAreNotTransferTargets() {
        val pending = session("peer", ConnectionPhase.SECURE_HANDSHAKE)
        assertNull(TransferSessionSelector.resolve(transfer("peer"), listOf(pending), setOf(pending.sessionId)))
        val disconnected = session("peer", ConnectionPhase.DISCONNECTED)
        assertNull(TransferSessionSelector.resolve(transfer(), listOf(disconnected), setOf(disconnected.sessionId)))
    }
}
