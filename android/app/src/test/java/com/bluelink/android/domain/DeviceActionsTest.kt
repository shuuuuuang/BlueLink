package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class DeviceActionsTest {
    private val peer = ConversationSummary("peer-a", "PC", PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED, isTrusted = true)
    private fun task(state: TransferStatus, peerId: String = peer.peerId, time: Long = 1L) = TransferItem(
        UUID.randomUUID(), "test.pdf", 100, 68, false, state, peerId = peerId, startedAtEpochMs = time)

    @Test fun cardAndMenuKeepTheSameOldestActiveTaskWhileItsStateChanges() {
        val first = task(TransferStatus.PAUSED)
        val second = task(TransferStatus.TRANSFERRING, time = 2)
        val other = task(TransferStatus.TRANSFERRING, "other", 0)
        assertEquals(first.id, DeviceActions.transfer(peer.peerId, listOf(other, second, first))?.id)
        assertEquals(second.id, DeviceActions.transfer(peer.peerId, listOf(first.copy(status = TransferStatus.COMPLETED), second))?.id)
        assertNull(DeviceActions.transfer(peer.peerId, listOf(other, first.copy(status = TransferStatus.FAILED))))
    }

    @Test fun controlsFollowLocalAndRemotePauseAndFinalization() {
        for (state in TransferStatus.entries) {
            val actions = DeviceActions.available(peer, task(state), false)
            assertEquals(state in setOf(TransferStatus.TRANSFERRING, TransferStatus.RESUMING), DeviceAction.PAUSE in actions)
            assertEquals(state == TransferStatus.PAUSED, DeviceAction.RESUME in actions)
            assertEquals(state in HistoryQuery.activeStatuses, DeviceAction.TRANSFERS in actions)
            if (state in HistoryQuery.activeStatuses) {
                assertFalse(DeviceAction.CLEAR in actions)
                assertFalse(DeviceAction.REMOVE_TRUST in actions)
            }
        }
    }

    @Test fun offlineOrMismatchedPeerCannotControlATransfer() {
        val expected = listOf(DeviceAction.OPEN, DeviceAction.PIN, DeviceAction.NOTE, DeviceAction.INFO, DeviceAction.CLEAR, DeviceAction.REMOVE_TRUST)
        assertEquals(expected, DeviceActions.available(peer.copy(availability = DeviceAvailability.OFFLINE), task(TransferStatus.PAUSED), false))
        assertEquals(expected, DeviceActions.available(peer, task(TransferStatus.TRANSFERRING, "other"), false))
        assertFalse(DeviceAction.REMOVE_TRUST in DeviceActions.available(peer.copy(isTrusted = false), null, false))
    }

    @Test fun nearbyConnectRequiresTheSupportedPublishedTransport() {
        val ready = NearbyDevice("PC", "00:11:22:33:44:55", false, platform = PeerPlatform.WINDOWS,
            rendezvousAvailable = true, connectable = true)
        assertTrue(DeviceActions.canConnect(ready))
        val uuidOnly = ready.copy(platform = PeerPlatform.UNKNOWN, discoveryId = "")
        assertTrue(DeviceActions.canConnect(uuidOnly))
        assertFalse(DeviceActions.canConnect(uuidOnly.copy(connectable = false)))
        assertFalse(DeviceActions.canConnect(uuidOnly.copy(rendezvousAvailable = false)))
        assertFalse(DeviceActions.canConnect(ready.copy(rendezvousAvailable = false)))
        assertFalse(DeviceActions.canConnect(ready.copy(connectable = false)))
        assertFalse(DeviceActions.canConnect(ready.copy(platform = PeerPlatform.ANDROID)))
        assertFalse(DeviceAction.CONNECT in DeviceActions.available(peer, null, true))
        assertTrue(DeviceAction.CONNECT in DeviceActions.available(peer.copy(availability = DeviceAvailability.CONNECTABLE), null, true))
    }
}
