package com.bluelink.android.domain

import com.bluelink.android.usb.UsbStatePolicy
import com.bluelink.android.usb.UsbStage
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class SessionRouteTest {
    private fun session(peer: String, transport: SessionTransport, phase: ConnectionPhase = ConnectionPhase.CONNECTED) =
        ManagedSessionState(UUID.randomUUID(), peer, peer, "address", phase, "", 0, transport)
    @Test fun usbWinsOnlyAfterSecureHandshakeAndOnlyForSamePeer() {
        val bt = session("peer", SessionTransport.BLUETOOTH)
        val usb = session("peer", SessionTransport.USB, ConnectionPhase.TRUST_REQUIRED)
        val other = session("other", SessionTransport.USB)
        assertEquals(bt, SessionRoute.preferred(listOf(other, usb, bt), "PEER"))
        val ready = usb.copy(phase = ConnectionPhase.CONNECTED)
        assertEquals(ready, SessionRoute.preferred(listOf(other, ready, bt), "peer"))
        assertNull(SessionRoute.preferred(listOf(other), "peer"))
    }
    @Test fun switchOffBlocksNewUsbSendsBeforeAsyncCloseFinishes() {
        val usb = session("peer", SessionTransport.USB)
        val bt = session("peer", SessionTransport.BLUETOOTH)
        assertEquals(bt, SessionRoute.preferred(listOf(usb, bt), "peer", usbEnabled = false))
        assertNull(SessionRoute.preferred(listOf(usb), "peer", usbEnabled = false))
    }
    @Test fun readinessRequiresEnabledAndAuthenticatedMatchingPeer() {
        val ready = session("peer", SessionTransport.BLUETOOTH).copy(usbFileReady = true)
        assertTrue(UsbStatePolicy.isReady(true, "PEER", listOf(ready)))
        assertFalse(UsbStatePolicy.isReady(false, "peer", listOf(ready)))
        assertFalse(UsbStatePolicy.isReady(true, "other", listOf(ready)))
        assertFalse(UsbStatePolicy.isReady(true, null, listOf(ready)))
        assertFalse(UsbStatePolicy.isReady(true, "peer", listOf(ready.copy(usbFileReady = false))))
        ConnectionPhase.values().filter { it != ConnectionPhase.CONNECTED }.forEach {
            assertFalse(UsbStatePolicy.isReady(true, "peer", listOf(ready.copy(phase = it))))
        }
    }
    @Test fun usbIndicatorsAreIndependentAcrossPeersAndDisappearAfterDisconnect() {
        val first = session("first", SessionTransport.BLUETOOTH).copy(usbFileReady = true)
        val second = session("second", SessionTransport.BLUETOOTH).copy(usbFileReady = true)
        val bt = session("third", SessionTransport.BLUETOOTH)
        assertEquals(listOf(true, true, false), listOf("first", "second", "third").map {
            UsbStatePolicy.isReady(true, it, listOf(first, second, bt))
        })
        assertFalse(UsbStatePolicy.isReady(true, "first", listOf(second, bt)))
        assertTrue(UsbStatePolicy.isReady(true, "second", listOf(second, bt)))
    }
    @Test fun onlySamePeerAuthenticatedBluetoothAllowsFallback() {
        val other = session("other", SessionTransport.BLUETOOTH)
        val pending = session("peer", SessionTransport.BLUETOOTH, ConnectionPhase.TRUST_REQUIRED)
        assertEquals(UsbStage.UNAVAILABLE, UsbStatePolicy.disconnected(listOf(other, pending), "peer"))
        assertEquals(UsbStage.FALLBACK, UsbStatePolicy.disconnected(listOf(pending.copy(phase = ConnectionPhase.CONNECTED)), "PEER"))
    }
    @Test fun usbCannotOverwriteBluetoothReconnectAddress() {
        assertEquals("AA:BB:CC:DD:EE:FF", SessionRoute.savedAddress("AA:BB:CC:DD:EE:FF", "usb:BlueLink:123"))
        assertEquals("", SessionRoute.savedAddress(null, "usb:BlueLink:123"))
        assertEquals("11:22:33:44:55:66", SessionRoute.savedAddress("old", "11:22:33:44:55:66"))
    }
    @Test fun controlsStayOnTheirLiveOwnerInsteadOfPreferredUsb() {
        val bt = session("peer", SessionTransport.BLUETOOTH)
        val usb = session("peer", SessionTransport.USB)
        val file = TransferItem(UUID.randomUUID(), "qa.bin", 99, outgoing = true, status = TransferStatus.TRANSFERRING, peerId = "peer")
        assertEquals(bt.sessionId, TransferSessionSelector.resolve(file, listOf(bt, usb), setOf(bt.sessionId)))
        assertEquals(usb.sessionId, TransferSessionSelector.resolve(file.copy(status = TransferStatus.FAILED), listOf(bt, usb), emptySet()))
    }
    @Test fun bluetoothOffDoesNotHideConnectedUsbPeer() {
        val usb = ConversationSummary("peer", "PC", PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED,
            transport = SessionTransport.USB)
        val bt = usb.copy(peerId = "other", transport = SessionTransport.BLUETOOTH)
        val view = DeviceScreenState.project(listOf(usb, bt), emptyList(), BluetoothAccessState.OFF, "")
        assertEquals(listOf(usb), view.connected)
        assertEquals(1, view.totalConnected)
        assertEquals("other", view.offline.single().peerId)
    }
    @Test fun usbNoticesNeverBorrowAnotherPeerIdentity() {
        val state = com.bluelink.android.usb.UsbSnapshot(UsbStage.FALLBACK, peerId = "peer")
        assertEquals(UsbStage.FALLBACK, UsbStatePolicy.noticeStage(true, "PEER", false, state))
        assertNull(UsbStatePolicy.noticeStage(true, "other", false, state))
        assertNull(UsbStatePolicy.noticeStage(true, "peer", false, state.copy(peerId = null)))
        assertNull(UsbStatePolicy.noticeStage(false, "peer", false, state))
        assertNull(UsbStatePolicy.noticeStage(true, "peer", true, state))
    }
    @Test fun unrelatedAccessoriesAreNeverOpened() {
        assertTrue(UsbStatePolicy.isBlueLink("BlueLink", "BlueLink"))
        assertFalse(UsbStatePolicy.isBlueLink("BlueLink", "Other"))
        assertFalse(UsbStatePolicy.isBlueLink("Other", "BlueLink"))
        assertFalse(UsbStatePolicy.isBlueLink(null, null))
    }
}
