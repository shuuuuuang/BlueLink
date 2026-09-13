package com.bluelink.android.domain

import java.util.UUID

/** All authenticated channels for one identity share a content cache; connections remain independent. */
internal class PeerContentScope {
    private val peerScopes = mutableMapOf<String, UUID>()
    private val aliases = mutableMapOf<UUID, Pair<String, UUID>>()
    @Synchronized fun bind(session: UUID, peerId: String) {
        val peer = peerId.lowercase(java.util.Locale.ROOT)
        require(peer.isNotBlank())
        aliases[session]?.let { check(it.first == peer) { "Connection identity cannot change" }; return }
        aliases[session] = peer to peerScopes.getOrPut(peer) { session }
    }
    @Synchronized fun key(session: UUID): UUID = aliases[session]?.second ?: session
}
