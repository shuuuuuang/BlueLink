package com.bluelink.android.domain

import java.util.UUID

/** Counts only messages appended while reading history; pagination and receipts are not new messages. */
internal data class ConversationScrollState(
    val lastObservedId: UUID? = null,
    val observedCount: Int = 0,
    val pendingIds: Set<UUID> = emptySet(),
) {
    fun observe(messages: List<ChatItem>, atBottom: Boolean, locating: Boolean = false): Update {
        val anchor = messages.indexOfFirst { it.id == lastObservedId }
        val appended = if (anchor >= 0) messages.drop(anchor + 1) else emptyList()
        val follow = !locating && messages.isNotEmpty() &&
            (observedCount == 0 || (atBottom && appended.isNotEmpty()) || appended.any { it.outgoing })
        val pending = if (follow || (atBottom && !locating)) emptySet() else
            (pendingIds + appended.filterNot { it.outgoing }.map { it.id }).intersect(messages.map { it.id }.toSet())
        return Update(ConversationScrollState(messages.lastOrNull()?.id, messages.size, pending), follow)
    }

    fun readToBottom() = copy(pendingIds = emptySet())
    data class Update(val state: ConversationScrollState, val followLatest: Boolean)
}
