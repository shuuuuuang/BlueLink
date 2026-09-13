package com.bluelink.android.domain

import java.util.UUID

object TransferSessionSelector {
    fun resolve(item: TransferItem, sessions: List<ManagedSessionState>, knownOwners: Set<UUID>): UUID? {
        val connected = sessions.filter { it.phase == ConnectionPhase.CONNECTED }
        // A known peer is authoritative. Never fall back to whichever conversation is open.
        if (!item.peerId.isNullOrBlank()) {
            val peers = connected.filter { it.peerId.equals(item.peerId, true) }
            val owners = peers.filter { it.sessionId in knownOwners }
            // Controls stay with the original channel while it is still connected.
            if (owners.size == 1) return owners.single().sessionId
            if (owners.size > 1) return null
            return SessionRoute.preferred(peers, item.peerId)?.sessionId
        }
        // Legacy records without a peer require an unambiguous live transfer owner.
        return connected.singleOrNull { it.sessionId in knownOwners }?.sessionId
    }
}
