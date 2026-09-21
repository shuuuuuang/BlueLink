package com.bluelink.android.domain

enum class BluetoothAccessState {
    READY, OFF, REQUIRED, DENIED;

    val canUseBluetooth: Boolean get() = this == READY

    companion object {
        fun resolve(permissionsGranted: Boolean, enabled: Boolean, requestedBefore: Boolean): BluetoothAccessState = when {
            !permissionsGranted -> if (requestedBefore) DENIED else REQUIRED
            !enabled -> OFF
            else -> READY
        }
    }
}

/** Local history is independent of transport permission, adapter state and discovery. */
data class DeviceScreenState(
    val connected: List<ConversationSummary>,
    val offline: List<ConversationSummary>,
    val nearby: List<NearbyDevice>,
    val totalConnected: Int,
    val searching: Boolean,
) {
    val noResults: Boolean get() = searching && connected.isEmpty() && offline.isEmpty() && nearby.isEmpty()

    companion object {
        fun project(
            conversations: List<ConversationSummary>,
            devices: List<NearbyDevice>,
            access: BluetoothAccessState,
            query: String,
        ): DeviceScreenState {
            val history = conversations.filterNot { it.isRemoved }.groupBy { it.peerId.lowercase() }.values.map { duplicates ->
                duplicates.maxWith(compareBy<ConversationSummary> { it.availability == DeviceAvailability.CONNECTED }
                    .thenBy { it.lastActivityAt })
            }.map { summary ->
                if (!access.canUseBluetooth && summary.transport != SessionTransport.USB) summary.copy(availability = DeviceAvailability.OFFLINE) else summary
            }
            val term = query.trim()
            fun matches(vararg fields: String) = fields.any { it.contains(term, ignoreCase = true) }
            val visible = history.sortedByDescending { it.isPinned }.filter { matches(it.peerName, it.localNote, it.peerId, it.transportAddress) }
            val fresh = if (access.canUseBluetooth) DeviceProjectionPolicy.nearbyCandidates(
                devices, emptySet(), history.map { it.peerId }.toSet(),
                emptySet(), history.map { it.transportAddress }.toSet(),
            ).distinctBy { it.stableKey.ifBlank { it.discoveryId.ifBlank { it.address }.uppercase() } }
                .filter { matches(it.name, it.discoveryId, it.address) } else emptyList()
            return DeviceScreenState(
                connected = visible.filter { it.availability == DeviceAvailability.CONNECTED },
                offline = visible.filter { it.availability != DeviceAvailability.CONNECTED },
                nearby = fresh,
                totalConnected = history.count { it.availability == DeviceAvailability.CONNECTED },
                searching = term.isNotEmpty(),
            )
        }
    }
}
