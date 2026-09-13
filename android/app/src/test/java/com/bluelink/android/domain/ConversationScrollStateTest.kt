package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class ConversationScrollStateTest {
    private fun message(outgoing: Boolean = false) = ChatItem(text = "QA", outgoing = outgoing,
        status = if (outgoing) MessageStatus.SENT else MessageStatus.RECEIVED)

    @Test fun readingHistoryAccumulatesOnlyNewIncomingAndKeepsPosition() {
        val history = List(10) { message() }
        val initial = ConversationScrollState().observe(history, true)
        assertTrue(initial.followLatest)
        val added = List(3) { message() }
        val next = initial.state.observe(history + added, false)
        assertFalse(next.followLatest)
        assertEquals(added.map { it.id }.toSet(), next.state.pendingIds)
        assertEquals(4, next.state.observe(history + added + message(), false).state.pendingIds.size)
        assertTrue(next.state.readToBottom().pendingIds.isEmpty())
    }

    @Test fun deliveryChangesAndLoadingOlderHistoryDoNotIncreaseCount() {
        val history = List(10) { message() }
        val initial = ConversationScrollState().observe(history, true).state
        val older = List(5) { message() } + history
        val paginated = initial.observe(older, false)
        assertFalse(paginated.followLatest)
        assertTrue(paginated.state.pendingIds.isEmpty())
        assertTrue(paginated.state.observe(older.map { it.copy(status = MessageStatus.READ) }, false).state.pendingIds.isEmpty())
    }

    @Test fun ownSendAndNewMessagesAtBottomFollowLatest() {
        val history = List(10) { message() }
        val initial = ConversationScrollState().observe(history, true).state
        assertTrue(initial.observe(history + message(), true).followLatest)
        val incoming = message()
        val reading = initial.observe(history + incoming, false).state
        val ownSend = reading.observe(history + incoming + message(true), false)
        assertTrue(ownSend.followLatest)
        assertTrue(ownSend.state.pendingIds.isEmpty())
    }

    @Test fun locateAndDeletionDoNotForceUserToLatestOrKeepDeletedBadge() {
        val history = List(10) { message() }
        assertFalse(ConversationScrollState().observe(history, true, locating = true).followLatest)
        val added = message()
        val reading = ConversationScrollState().observe(history, true).state.observe(history + added, false).state
        val removed = reading.observe(history, false)
        assertFalse(removed.followLatest)
        assertTrue(removed.state.pendingIds.isEmpty())
        assertEquals(ConversationScrollState(), removed.state.observe(emptyList(), false).state)
    }
}
