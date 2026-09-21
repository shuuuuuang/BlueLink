package com.bluelink.android.data.local

/** Old clients revoked the key but left an UNKNOWN historical peer in the home list. */
internal object LegacyPeerTrustMigration {
    const val MARKER = "migration.removed_trust.v1"

    fun shouldRemove(peer: PeerEntity, hasTrustedKey: Boolean, migrateUnknown: Boolean): Boolean {
        if (hasTrustedKey) return false
        // Also repair an interrupted modern removal before its database projection committed.
        if (peer.trustState == "TRUSTED") return true
        return migrateUnknown && peer.trustState == "UNKNOWN" &&
            (peer.identityPublicKey != null || peer.lastConnectedAt != null)
    }
}
