package com.bluelink.android.data.local

import org.junit.Assert.*
import org.junit.Test

class LegacyPeerTrustMigrationTest {
    private val legacy = PeerEntity("peer", byteArrayOf(1), trustState = "UNKNOWN", createdAt = 1, lastSeenAt = 2, lastConnectedAt = 2)

    @Test fun legacyRevocationMigratesOnlyWhenNoAuthoritativeTrustRemains() {
        assertTrue(LegacyPeerTrustMigration.shouldRemove(legacy, false, true))
        assertFalse(LegacyPeerTrustMigration.shouldRemove(legacy, true, true))
        assertTrue(LegacyPeerTrustMigration.shouldRemove(legacy.copy(identityPublicKey = null), false, true))
    }

    @Test fun migrationDoesNotHideNewOrIntentionallyUntrustedPeers() {
        assertFalse(LegacyPeerTrustMigration.shouldRemove(legacy, false, false))
        assertFalse(LegacyPeerTrustMigration.shouldRemove(legacy.copy(identityPublicKey = null, lastConnectedAt = null), false, true))
        for (state in listOf("BLOCKED", "RETIRED", "REMOVED"))
            assertFalse(LegacyPeerTrustMigration.shouldRemove(legacy.copy(trustState = state), false, true))
    }

    @Test fun interruptedRemovalRepairsEvenAfterTheLegacyMigration() {
        assertTrue(LegacyPeerTrustMigration.shouldRemove(legacy.copy(trustState = "TRUSTED"), false, false))
        assertFalse(LegacyPeerTrustMigration.shouldRemove(legacy.copy(trustState = "TRUSTED"), true, false))
    }
}
