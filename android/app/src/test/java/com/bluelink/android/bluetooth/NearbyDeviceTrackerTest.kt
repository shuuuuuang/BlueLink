package com.bluelink.android.bluetooth

import com.bluelink.android.domain.DeviceAvailability
import com.bluelink.android.domain.DeviceProjectionPolicy
import com.bluelink.android.domain.NearbyDevice
import com.bluelink.android.domain.PeerPlatform
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class NearbyDeviceTrackerTest {
    @Test
    fun `sixteen and twelve digit identities merge`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000, id = "A1B2C3D4E5F60708"))
        val values = tracker.observe(observation(2_000, id = "A1B2C3D4E5F6"))
        assertEquals(1, values.size)
        assertEquals("identity:A1B2C3D4E5F6", values.single().stableKey)
        assertEquals("A1B2C3D4E5F6", values.single().discoveryId)
    }

    @Test
    fun `address entry migrates to identity key and changing address stays one entry`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000, id = ""))
        var values = tracker.observe(observation(2_000, id = "A1B2C3D4E5F60708"))
        assertEquals(1, values.size)
        assertEquals("identity:A1B2C3D4E5F6", values.single().stableKey)
        values = tracker.observe(observation(3_000, id = "A1B2C3D4E5F6", address = "AA:BB:CC:DD:EE:FF"))
        assertEquals(1, values.size)
        assertEquals("AA:BB:CC:DD:EE:FF", values.single().address)
        assertEquals("identity:A1B2C3D4E5F6", values.single().stableKey)
    }

    @Test
    fun `address only packet cannot demote an identity entry`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000, id = "A1B2C3D4E5F60708"))
        val values = tracker.observe(observation(2_000, id = ""))
        assertEquals(1, values.size)
        assertEquals("identity:A1B2C3D4E5F6", values.single().stableKey)
        assertEquals("A1B2C3D4E5F6", values.single().discoveryId)
    }

    @Test
    fun `identity observation merges an existing identity and address alias`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000, id = "A1B2C3D4E5F60708", address = "10:20:30:40:50:60"))
        tracker.observe(observation(2_000, id = "", address = "AA:BB:CC:DD:EE:FF"))
        val values = tracker.observe(
            observation(3_000, id = "A1B2C3D4E5F6", address = "AA:BB:CC:DD:EE:FF"))
        assertEquals(1, values.size)
        assertEquals("identity:A1B2C3D4E5F6", values.single().stableKey)
        assertEquals("AA:BB:CC:DD:EE:FF", values.single().address)
    }

    @Test
    fun `rescan does not clear and list order is stable`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000, id = "1111111111111111", rssi = -80))
        tracker.observe(observation(1_100, id = "2222222222222222", address = "20:20:30:40:50:60", rssi = -30))
        val before = tracker.snapshot().map { it.stableKey }
        assertEquals(before, tracker.beginScan().map { it.stableKey })
        assertEquals(before, tracker.snapshot().map { it.stableKey })
    }

    @Test
    fun `capability expires after twenty seconds but card remains`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000))
        val values = tracker.expire(21_001)
        assertEquals(1, values.size)
        assertFalse(values.single().connectable)
        assertFalse(values.single().rendezvousAvailable)
    }

    @Test
    fun `device requires forty five seconds and two missed windows before eviction`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000))
        tracker.completeScanWindow(16_000)
        tracker.completeScanWindow(31_000)
        assertEquals(1, tracker.expire(45_999).size)
        assertTrue(tracker.expire(46_001).isEmpty())
    }

    @Test
    fun `one missed window never removes a stale card`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000))
        tracker.completeScanWindow(60_000)
        assertEquals(1, tracker.snapshot().size)
    }

    @Test
    fun `unavailable clears the whole projection`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000))
        assertTrue(tracker.unavailable().isEmpty())
        assertTrue(tracker.snapshot().isEmpty())
    }

    @Test
    fun `projection priority is connected then trusted offline then nearby`() {
        assertEquals(DeviceAvailability.CONNECTED, DeviceProjectionPolicy.availability(true, true, true))
        assertEquals(DeviceAvailability.OFFLINE, DeviceProjectionPolicy.availability(false, true, true))
        assertEquals(DeviceAvailability.CONNECTABLE, DeviceProjectionPolicy.availability(false, false, true))
    }

    @Test
    fun `trusted and active devices never remain nearby candidates`() {
        val tracker = NearbyDeviceTracker()
        val trusted = tracker.observe(observation(1_000)).single()
        val candidate = tracker.observe(observation(2_000, id = "BBBBBBBBBBBB0000", address = "AA:BB:CC:DD:EE:FF")).last()
        val values = DeviceProjectionPolicy.nearbyCandidates(
            listOf(trusted, candidate), emptySet(), setOf("A1B2C3D4E5F6"), emptySet(), emptySet())
        assertEquals(listOf(candidate.stableKey), values.map { it.stableKey })
    }

    @Test
    fun `known transport address removes nearby candidate`() {
        val value = NearbyDevice("Known", "10:20:30:40:50:60", false, stableKey = "address:102030405060")
        val values = DeviceProjectionPolicy.nearbyCandidates(
            listOf(value), emptySet(), emptySet(), emptySet(), setOf("10-20-30-40-50-60"))
        assertTrue(values.isEmpty())
    }

    @Test
    fun `identity normalization accepts separators and stable prefix`() {
        assertEquals("A1B2C3D4E5F6", DeviceProjectionPolicy.normalizeIdentity("a1:b2:c3:d4:e5:f6:07:08"))
        assertEquals("A1B2C3D4E5F6", DeviceProjectionPolicy.normalizeIdentity("A1B2C3D4E5F6"))
        assertEquals(null, DeviceProjectionPolicy.normalizeIdentity("not-an-id"))
    }

    @Test
    fun `rssi is smoothed instead of jumping to latest packet`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000, rssi = -80))
        val value = tracker.observe(observation(1_100, rssi = -20)).single()
        assertEquals((-65).toShort(), value.rssi)
    }

    @Test
    fun `rssi ordering is throttled then refreshed`() {
        val tracker = NearbyDeviceTracker(sortThrottleMs = 1_500)
        tracker.observe(observation(1_000, id = "1111111111111111", rssi = -80))
        tracker.observe(observation(1_100, id = "2222222222222222", address = "20:20:30:40:50:60", rssi = -30))
        assertEquals("identity:111111111111", tracker.snapshot().first().stableKey)
        assertEquals("identity:222222222222", tracker.expire(2_601).first().stableKey)
    }

    @Test
    fun `placeholder name cannot replace a useful name`() {
        val tracker = NearbyDeviceTracker()
        tracker.observe(observation(1_000, name = "Office PC"))
        val value = tracker.observe(observation(2_000, name = "Windows 设备 E5F6")).single()
        assertEquals("Office PC", value.name)
    }

    @Test
    fun `rename updates tracker without changing stable identity`() {
        val tracker = NearbyDeviceTracker()
        val before = tracker.observe(observation(1_000)).single()
        val after = tracker.rename(before.address, "Renamed PC").single()
        assertEquals(before.stableKey, after.stableKey)
        assertEquals("Renamed PC", after.name)
    }

    private fun observation(
        at: Long,
        id: String = "A1B2C3D4E5F60708",
        address: String = "10:20:30:40:50:60",
        connectable: Boolean = true,
        rssi: Short = -55,
        name: String = "Windows PC",
    ) = NearbyDeviceTracker.Observation(
        name = name,
        address = address,
        bonded = false,
        discoveryId = id,
        rssi = rssi,
        platform = PeerPlatform.WINDOWS,
        rendezvousAvailable = true,
        connectable = connectable,
        observedAtEpochMs = at,
    )
}
