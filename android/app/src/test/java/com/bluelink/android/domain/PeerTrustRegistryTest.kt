package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class PeerTrustRegistryTest {
    private val storage = mutableMapOf<String, ByteArray>()
    private val registry = PeerTrustRegistry(storage::get, { id, key -> storage[id] = key }, { storage.remove(it) })

    @Test fun incompleteHandshakeDoesNotPersistTrust() {
        assertFalse(registry.begin("ABC", byteArrayOf(1)).wasTrusted)
        assertTrue(storage.isEmpty())
    }

    @Test fun confirmedEncryptedHandshakePersistsAndRecognizesTrustedPeer() {
        registry.complete(registry.begin("ABC", byteArrayOf(1)))
        assertTrue(registry.begin("abc", byteArrayOf(1)).wasTrusted)
        assertArrayEquals(byteArrayOf(1), storage["abc"])
    }

    @Test fun revocationInvalidatesBothTrustedAndUntrustedHandshakes() {
        val first = registry.begin("ABC", byteArrayOf(1))
        registry.revoke("abc")
        assertThrows(IllegalStateException::class.java) { registry.complete(first) }
        registry.complete(registry.begin("abc", byteArrayOf(1)))
        val reconnect = registry.begin("abc", byteArrayOf(1))
        registry.revoke("ABC")
        assertThrows(IllegalStateException::class.java) { registry.complete(reconnect) }
        assertTrue(storage.isEmpty())
    }

    @Test fun concurrentDifferentKeysCannotReplaceFirstVerifiedIdentity() {
        val one = registry.begin("abc", byteArrayOf(1))
        val two = registry.begin("ABC", byteArrayOf(2))
        registry.complete(one)
        assertThrows(SecurityException::class.java) { registry.complete(two) }
        assertThrows(SecurityException::class.java) { registry.begin("abc", byteArrayOf(2)) }
        assertArrayEquals(byteArrayOf(1), storage["abc"])
    }
    @Test fun removingAllTrustInvalidatesPendingUnknownPeerAsWellAsTrustedPeer() {
        registry.complete(registry.begin("old", byteArrayOf(1)))
        val reconnect = registry.begin("old", byteArrayOf(1))
        val pending = registry.begin("new", byteArrayOf(2))
        registry.invalidateAll { storage.clear() }
        assertThrows(IllegalStateException::class.java) { registry.complete(reconnect) }
        assertThrows(IllegalStateException::class.java) { registry.complete(pending) }
        assertTrue(storage.isEmpty())
        assertFalse(registry.begin("old", byteArrayOf(1)).wasTrusted)
    }

    @Test fun failedPersistenceKeepsExistingTrustButRejectsOldHandshake() {
        registry.complete(registry.begin("old", byteArrayOf(1)))
        val pending = registry.begin("old", byteArrayOf(1))
        assertThrows(java.io.IOException::class.java) {
            registry.invalidateAll { throw java.io.IOException("test storage unavailable") }
        }
        assertThrows(IllegalStateException::class.java) { registry.complete(pending) }
        assertArrayEquals(byteArrayOf(1), storage["old"])
        assertTrue(registry.begin("old", byteArrayOf(1)).wasTrusted)
    }

}
