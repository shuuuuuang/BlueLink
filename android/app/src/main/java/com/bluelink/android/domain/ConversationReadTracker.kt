package com.bluelink.android.domain

/** Reading follows the foreground message page, independently of the selected transport/session. */
internal class ConversationReadTracker(private val markRead: (String) -> Unit) {
    private var visiblePeer: String? = null
    private var resumed = false

    @Synchronized
    fun setVisiblePeer(peerId: String?) {
        val peer = peerId?.takeIf { it.isNotBlank() }
        if (visiblePeer.equals(peer, ignoreCase = true)) return
        visiblePeer = peer
        if (resumed && peer != null) markRead(peer)
    }

    @Synchronized
    fun setResumed(value: Boolean) {
        if (resumed == value) return
        resumed = value
        if (value) visiblePeer?.let(markRead)
    }

    /** Register writes under the same lock as read events; do not defer the visibility decision. */
    @Synchronized
    fun recordMessage(peerId: String?, outgoing: Boolean, persist: (unread: Boolean) -> Unit) {
        val reading = resumed && visiblePeer != null && visiblePeer.equals(peerId, ignoreCase = true)
        persist(!outgoing && !reading)
    }
}
