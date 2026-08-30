package com.bluelink.android.bluetooth

import com.bluelink.android.domain.DeviceProjectionPolicy
import com.bluelink.android.domain.NearbyDevice
import com.bluelink.android.domain.PeerPlatform
import kotlin.math.roundToInt

/** Pure Kotlin state machine for BLE observations. */
class NearbyDeviceTracker(
    private val capabilityTtlMs: Long = 20_000L,
    private val deviceTtlMs: Long = 45_000L,
    private val sortThrottleMs: Long = 1_500L,
) {
    data class Observation(
        val name: String,
        val address: String,
        val bonded: Boolean,
        val discoveryId: String,
        val rssi: Short?,
        val platform: PeerPlatform,
        val rendezvousAvailable: Boolean,
        val connectable: Boolean,
        val observedAtEpochMs: Long,
    )

    private data class Entry(
        var device: NearbyDevice,
        var smoothedRssi: Double?,
        var observedWindow: Long,
    )

    private val entries = linkedMapOf<String, Entry>()
    private val order = mutableListOf<String>()
    private var completedWindows = 0L
    private var lastSortAt: Long? = null
    private var sortDirty = false

    @Synchronized
    fun beginScan(): List<NearbyDevice> = snapshot()

    @Synchronized
    fun observe(observation: Observation): List<NearbyDevice> {
        val now = observation.observedAtEpochMs
        val identity = DeviceProjectionPolicy.normalizeIdentity(observation.discoveryId)
        val address = DeviceProjectionPolicy.normalizeAddress(observation.address)
            ?: observation.address.trim().uppercase()
        val identityKey = identity?.let { "identity:$it" }
        val addressKey = "address:$address"
        val addressAlias = addressKey.takeIf {
            identityKey != null && it != identityKey && entries.containsKey(it)
        }
        val aliasEntry = addressAlias?.let(entries::get)
        val existingKey = when {
            identityKey != null && entries.containsKey(identityKey) -> identityKey
            entries.containsKey(addressKey) -> addressKey
            else -> entries.entries.firstOrNull { (_, entry) ->
                DeviceProjectionPolicy.normalizeAddress(entry.device.address) == address
            }?.key
        }
        val targetKey = identityKey ?: existingKey ?: addressKey
        val previousEntry = existingKey?.let(entries::get)

        if (addressAlias != null && addressAlias != existingKey) {
            entries.remove(addressAlias)
            order.remove(addressAlias)
        }

        if (existingKey != null && existingKey != targetKey) {
            entries.remove(existingKey)
            val index = order.indexOf(existingKey)
            if (index >= 0) order[index] = targetKey
        }

        val previous = previousEntry?.device ?: aliasEntry?.device
        val normalizedIdentity = identity ?: previous?.discoveryId.orEmpty()
        val stableKey = identity?.let { "identity:$it" }
            ?: previous?.stableKey?.takeIf { it.startsWith("identity:") }
            ?: addressKey
        val effectiveName = when {
            previous == null -> observation.name
            !isPlaceholder(previous.name) && isPlaceholder(observation.name) -> previous.name
            observation.name.isBlank() -> previous.name
            else -> observation.name
        }
        val effectivePlatform = observation.platform.takeUnless { it == PeerPlatform.UNKNOWN }
            ?: previous?.platform ?: PeerPlatform.UNKNOWN
        val smoothed = smooth(previousEntry?.smoothedRssi ?: aliasEntry?.smoothedRssi, observation.rssi)
        val rendezvousSeen = if (observation.rendezvousAvailable) now
            else previous?.rendezvousLastSeenEpochMs ?: 0L
        val connectableSeen = if (observation.connectable) now
            else previous?.connectableLastSeenEpochMs ?: 0L
        val device = NearbyDevice(
            name = effectiveName,
            address = observation.address,
            bonded = observation.bonded || previous?.bonded == true || aliasEntry?.device?.bonded == true,
            discoveryId = normalizedIdentity,
            rssi = smoothed?.roundToInt()?.coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt())?.toShort(),
            platform = effectivePlatform,
            rendezvousAvailable = isRecent(now, rendezvousSeen),
            connectable = isRecent(now, connectableSeen),
            lastSeenEpochMs = now,
            rendezvousLastSeenEpochMs = rendezvousSeen,
            connectableLastSeenEpochMs = connectableSeen,
            stableKey = stableKey,
        )
        entries[targetKey] = Entry(device, smoothed, completedWindows)
        if (targetKey !in order) order += targetKey
        sortDirty = true
        sortIfDue(now, force = previousEntry == null && entries.size == 1)
        return snapshot()
    }

    @Synchronized
    fun completeScanWindow(now: Long): List<NearbyDevice> {
        completedWindows += 1
        return expire(now)
    }

    @Synchronized
    fun expire(now: Long): List<NearbyDevice> {
        val expired = entries.filterValues { entry ->
            now >= entry.device.lastSeenEpochMs &&
                now - entry.device.lastSeenEpochMs > deviceTtlMs &&
                completedWindows - entry.observedWindow >= 2
        }.keys
        if (expired.isNotEmpty()) {
            expired.forEach(entries::remove)
            order.removeAll(expired)
        }
        entries.values.forEach { entry ->
            val current = entry.device
            entry.device = current.copy(
                rendezvousAvailable = isRecent(now, current.rendezvousLastSeenEpochMs),
                connectable = isRecent(now, current.connectableLastSeenEpochMs),
            )
        }
        sortIfDue(now)
        return snapshot()
    }

    @Synchronized
    fun rename(address: String, name: String): List<NearbyDevice> {
        if (name.isBlank()) return snapshot()
        val normalized = DeviceProjectionPolicy.normalizeAddress(address)
        entries.values.firstOrNull {
            DeviceProjectionPolicy.normalizeAddress(it.device.address) == normalized
        }?.let { it.device = it.device.copy(name = name) }
        return snapshot()
    }

    @Synchronized
    fun unavailable(): List<NearbyDevice> {
        entries.clear()
        order.clear()
        sortDirty = false
        return emptyList()
    }

    @Synchronized
    fun snapshot(): List<NearbyDevice> = buildList {
        order.forEach { key -> entries[key]?.device?.let(::add) }
        entries.filterKeys { it !in order }.values.forEach { add(it.device) }
    }

    private fun sortIfDue(now: Long, force: Boolean = false) {
        if (!sortDirty) return
        val previousSort = lastSortAt
        if (!force && previousSort != null && now >= previousSort && now - previousSort < sortThrottleMs) return
        order.sortWith(compareByDescending<String> { entries[it]?.smoothedRssi ?: Double.NEGATIVE_INFINITY }
            .thenBy { entries[it]?.device?.name?.lowercase().orEmpty() }
            .thenBy { it })
        lastSortAt = now
        sortDirty = false
    }

    private fun smooth(previous: Double?, current: Short?): Double? = when {
        current == null -> previous
        previous == null -> current.toDouble()
        else -> previous * 0.75 + current.toDouble() * 0.25
    }

    private fun isRecent(now: Long, observedAt: Long): Boolean =
        observedAt > 0L && now >= observedAt && now - observedAt <= capabilityTtlMs

    private fun isPlaceholder(value: String): Boolean =
        value.startsWith("BlueLink ", ignoreCase = true) ||
            value.startsWith("Windows 设备", ignoreCase = true)
}
