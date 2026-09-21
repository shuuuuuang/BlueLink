package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class DeviceScreenStateTest {
    private val office = ConversationSummary("AABBCCDDEEFF0011", "Office-PC", PeerPlatform.WINDOWS,
        DeviceAvailability.CONNECTED, transportAddress = "AA:BB:CC:DD:EE:FF", unreadCount = 3)
    private val phone = ConversationSummary("1122334455667788", "旅行手机", PeerPlatform.ANDROID,
        DeviceAvailability.OFFLINE, lastActivityAt = 90, lastConnectedAt = 80)
    private val nearby = NearbyDevice("New PC", "00:11:22:33:44:55", false,
        discoveryId = "998877665544", platform = PeerPlatform.WINDOWS, rendezvousAvailable = true,
        connectable = true, stableKey = "new-pc")

    @Test fun permissionsAndAdapterNeverBlockStoredHistoryOrItsSearch() {
        for (access in listOf(BluetoothAccessState.REQUIRED, BluetoothAccessState.DENIED, BluetoothAccessState.OFF)) {
            val all = DeviceScreenState.project(listOf(office, phone), listOf(nearby), access, "")
            assertEquals(2, all.offline.size)
            assertEquals(3, all.offline.first().unreadCount)
            assertEquals(0, all.totalConnected)
            assertTrue(all.connected.isEmpty())
            assertTrue(all.nearby.isEmpty())
            val result = DeviceScreenState.project(listOf(office, phone), listOf(nearby), access, " office ")
            assertEquals(listOf(office.peerId), result.offline.map { it.peerId })
            assertFalse(result.noResults)
        }
    }

    @Test fun searchUsesRealNamesAndIdentifiersWithoutChangingConnectionCount() {
        val other = phone.copy(availability = DeviceAvailability.CONNECTED)
        val byName = DeviceScreenState.project(listOf(office, other), listOf(nearby), BluetoothAccessState.READY, "OFFICE")
        assertEquals(1, byName.connected.size)
        assertEquals(2, byName.totalConnected)
        val byId = DeviceScreenState.project(listOf(office, phone), emptyList(), BluetoothAccessState.DENIED, "33445566")
        assertEquals(phone.peerId, byId.offline.single().peerId)
        val byAddress = DeviceScreenState.project(listOf(office), emptyList(), BluetoothAccessState.OFF, "AA:BB")
        assertEquals(office.peerId, byAddress.offline.single().peerId)
    }

    @Test fun knownPeersAreNotDuplicatedInNearbyAfterAddressOrIdentityMatches() {
        val sameIdentity = nearby.copy(discoveryId = "AABBCCDDEEFF", stableKey = "same-identity")
        val sameAddress = nearby.copy(address = "aa-bb-cc-dd-ee-ff", discoveryId = "556677889900", stableKey = "same-address")
        val historyDuplicate = office.copy(peerId = office.peerId.lowercase(), availability = DeviceAvailability.OFFLINE,
            lastActivityAt = 999)
        val projection = DeviceScreenState.project(listOf(historyDuplicate, office),
            listOf(sameIdentity, sameAddress, nearby, nearby), BluetoothAccessState.READY, "")
        assertEquals(listOf(office), projection.connected)
        assertTrue(projection.offline.isEmpty())
        assertEquals(listOf(nearby), projection.nearby)
    }

    @Test fun permissionRevocationHidesStaleDiscoveryAndRestorationReprojectsIt() {
        val granted = DeviceScreenState.project(listOf(office), listOf(nearby), BluetoothAccessState.READY, "")
        val revoked = DeviceScreenState.project(listOf(office), listOf(nearby), BluetoothAccessState.DENIED, "")
        val restored = DeviceScreenState.project(listOf(office), listOf(nearby), BluetoothAccessState.READY, "")
        assertEquals(listOf(nearby), granted.nearby)
        assertTrue(revoked.nearby.isEmpty())
        assertEquals(granted, restored)
    }

    @Test fun emptySearchIsDistinctFromAnEmptyHistory() {
        val noHistory = DeviceScreenState.project(emptyList(), emptyList(), BluetoothAccessState.REQUIRED, " ")
        val noMatch = DeviceScreenState.project(listOf(phone), emptyList(), BluetoothAccessState.REQUIRED, "missing")
        assertFalse(noHistory.searching)
        assertFalse(noHistory.noResults)
        assertTrue(noMatch.searching)
        assertTrue(noMatch.noResults)
    }

    @Test fun accessIsDerivedFromCurrentGrantsAndAdapterNotPastSuccess() {
        assertEquals(BluetoothAccessState.REQUIRED, BluetoothAccessState.resolve(false, true, false))
        assertEquals(BluetoothAccessState.DENIED, BluetoothAccessState.resolve(false, true, true))
        assertEquals(BluetoothAccessState.OFF, BluetoothAccessState.resolve(true, false, true))
        assertEquals(BluetoothAccessState.READY, BluetoothAccessState.resolve(true, true, true))
        assertFalse(BluetoothAccessState.resolve(false, true, true).canUseBluetooth)
    }
    @Test fun removingConnectedOrOfflinePeerOnlyKeepsCurrentlyDiscoveredEndpoint() {
        for (availability in listOf(DeviceAvailability.CONNECTED, DeviceAvailability.OFFLINE, DeviceAvailability.CONNECTABLE)) {
            val removed = office.copy(availability = availability, isRemoved = true, isTrusted = false)
            val visible = nearby.copy(name = office.peerName, address = office.transportAddress,
                discoveryId = office.peerId.take(12), stableKey = "removed-peer")
            val discovered = DeviceScreenState.project(listOf(removed, phone), listOf(visible), BluetoothAccessState.READY, "")
            assertTrue(discovered.connected.isEmpty())
            assertEquals(listOf(phone), discovered.offline)
            assertEquals(listOf(visible), discovered.nearby)
            assertEquals(0, discovered.totalConnected)
            val gone = DeviceScreenState.project(listOf(removed, phone), emptyList(), BluetoothAccessState.READY, "")
            assertTrue(gone.nearby.isEmpty())
            assertEquals(listOf(phone), gone.offline)
        }
    }

    @Test fun removedHistoryCannotHideScannedEndpointButAnotherKnownIdentityStillCan() {
        val visible = nearby.copy(address = office.transportAddress, discoveryId = office.peerId.take(12))
        val removed = office.copy(isRemoved = true)
        val restored = removed.copy(isRemoved = false, isTrusted = true, availability = DeviceAvailability.CONNECTED)
        val projection = DeviceScreenState.project(listOf(restored), listOf(visible), BluetoothAccessState.READY, "")
        assertEquals(listOf(restored), projection.connected)
        assertTrue(projection.nearby.isEmpty())
        val sharedAddress = phone.copy(transportAddress = office.transportAddress)
        assertTrue(DeviceScreenState.project(listOf(removed, sharedAddress), listOf(visible), BluetoothAccessState.READY, "").nearby.isEmpty())
        assertTrue(DeviceScreenState.project(listOf(removed), listOf(visible), BluetoothAccessState.OFF, "").nearby.isEmpty())
    }

    @Test fun pinsPreserveExistingOrderAndRemainWithinConnectionGroups() {
        val second = phone.copy(peerId = "second", lastActivityAt = 1)
        val pinned = phone.copy(peerId = "pinned", isPinned = true, localNote = "旅行备用")
        val state = DeviceScreenState.project(listOf(office, phone, second, pinned), emptyList(), BluetoothAccessState.READY, "")
        assertEquals(listOf(office), state.connected)
        assertEquals(listOf(pinned, phone, second), state.offline)
        val search = DeviceScreenState.project(listOf(pinned), emptyList(), BluetoothAccessState.OFF, "备用")
        assertEquals(listOf(pinned), search.offline)
    }

}
