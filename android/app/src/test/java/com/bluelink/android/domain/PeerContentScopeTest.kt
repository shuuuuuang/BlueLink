package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class PeerContentScopeTest {
    @Test fun switchingTransportRetainsContentForSameVerifiedIdentity() {
        val scopes = PeerContentScope()
        val bt = UUID.randomUUID(); val usb = UUID.randomUUID(); val replacement = UUID.randomUUID()
        scopes.bind(bt, "ABCDEF"); scopes.bind(usb, "abcdef"); scopes.bind(replacement, "ABCDEF")
        val cache = mutableMapOf(scopes.key(bt) to "message received on Bluetooth")
        assertEquals("message received on Bluetooth", cache[scopes.key(usb)])
        cache[scopes.key(usb)] = "file progress received on USB"
        assertEquals("file progress received on USB", cache[scopes.key(replacement)])
    }
    @Test fun unrelatedAndUnverifiedConnectionsDoNotShareContent() {
        val scopes = PeerContentScope(); val a = UUID.randomUUID(); val b = UUID.randomUUID(); val pending = UUID.randomUUID()
        scopes.bind(a, "first"); scopes.bind(b, "second")
        assertNotEquals(scopes.key(a), scopes.key(b))
        assertNotEquals(scopes.key(a), scopes.key(pending))
    }
    @Test fun connectionCannotBeReassignedToDifferentIdentity() {
        val scopes = PeerContentScope(); val id = UUID.randomUUID()
        scopes.bind(id, "first")
        assertThrows(IllegalStateException::class.java) { scopes.bind(id, "second") }
    }
}
