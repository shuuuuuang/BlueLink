package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class ConversationReadTrackerTest {
    @Test fun selectedPeerDoesNotImplyAVisibleMessagePage() {
        val reads = mutableListOf<String>()
        val tracker = ConversationReadTracker { reads += it }
        tracker.setResumed(true)
        tracker.recordMessage("peer", false) { assertTrue(it) }
        assertTrue(reads.isEmpty())
        tracker.setVisiblePeer("peer")
        assertEquals(listOf("peer"), reads)
        tracker.recordMessage("PEER", false) { assertFalse(it) }
        tracker.recordMessage("other", false) { assertTrue(it) }
        tracker.recordMessage(null, false) { assertTrue(it) }
    }

    @Test fun filesSearchAndLeavingConversationPreserveUnreadUntilReturning() {
        val reads = mutableListOf<String>()
        val tracker = ConversationReadTracker { reads += it }
        tracker.setResumed(true)
        repeat(3) {
            tracker.setVisiblePeer("peer")
            tracker.setVisiblePeer(null)
            tracker.recordMessage("peer", false) { assertTrue(it) }
        }
        assertEquals(3, reads.size)
        tracker.setVisiblePeer("other")
        tracker.recordMessage("peer", false) { assertTrue(it) }
        assertEquals("other", reads.last())
    }

    @Test fun backgroundAndLockDoNotReadButResumeMarksVisiblePeer() {
        val reads = mutableListOf<String>()
        val tracker = ConversationReadTracker { reads += it }
        tracker.setVisiblePeer("peer")
        tracker.recordMessage("peer", false) { assertTrue(it) }
        assertTrue(reads.isEmpty())
        tracker.setResumed(true)
        tracker.setResumed(true)
        assertEquals(listOf("peer"), reads)
        tracker.setResumed(false)
        tracker.recordMessage("peer", false) { assertTrue(it) }
        tracker.setResumed(true)
        assertEquals(2, reads.size)
        tracker.setResumed(false)
        tracker.setVisiblePeer(null)
        tracker.setResumed(true)
        assertEquals(2, reads.size)
    }

    @Test fun outgoingMessagesNeverIncreaseTheUnreadBadge() {
        val tracker = ConversationReadTracker {}
        tracker.recordMessage("peer", true) { assertFalse(it) }
        tracker.setResumed(true)
        tracker.setVisiblePeer("other")
        tracker.recordMessage("peer", true) { assertFalse(it) }
    }

    @Test fun delayedPersistenceRetainsArrivalVisibilityAndReadOrder() {
        var count = 0
        val pending = mutableListOf<() -> Unit>()
        val tracker = ConversationReadTracker { pending += { count = 0 } }
        tracker.setResumed(true)
        tracker.recordMessage("peer", false) { unread -> pending += { if (unread) count++ } }
        tracker.setVisiblePeer("peer")
        tracker.recordMessage("peer", false) { unread -> pending += { if (unread) count++ } }
        tracker.setVisiblePeer(null)
        tracker.recordMessage("peer", false) { unread -> pending += { if (unread) count++ } }
        pending.forEach { it() }
        assertEquals(1, count)
    }
}
