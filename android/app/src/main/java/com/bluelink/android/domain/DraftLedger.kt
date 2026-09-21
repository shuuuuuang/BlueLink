package com.bluelink.android.domain

data class DraftSnapshot(val peerId: String, val text: String, val revision: Long)

/** Revisions protect later edits (including A -> B -> A) from asynchronous send/save callbacks. */
class DraftLedger {
    private val values = linkedMapOf<String, DraftSnapshot>()
    private val dirty = mutableSetOf<String>()
    private var revision = 0L
    private fun key(peerId: String) = peerId.lowercase(java.util.Locale.ROOT)

    @Synchronized fun load(stored: Map<String, String>) {
        values.clear(); dirty.clear()
        stored.forEach { (peer, text) -> values[key(peer)] = DraftSnapshot(key(peer), text, ++revision) }
    }
    @Synchronized fun get(peerId: String): DraftSnapshot =
        values[key(peerId)] ?: DraftSnapshot(key(peerId), "", 0)
    @Synchronized fun edit(peerId: String, text: String): DraftSnapshot {
        val peer = key(peerId)
        if (get(peer).text == text) return get(peer)
        return DraftSnapshot(peer, text, ++revision).also { values[peer] = it; dirty += peer }
    }
    @Synchronized fun clearAfterSend(snapshot: DraftSnapshot): Boolean {
        if (get(snapshot.peerId) != snapshot) return false
        edit(snapshot.peerId, "")
        return true
    }
    @Synchronized fun acknowledge(snapshot: DraftSnapshot) {
        if (get(snapshot.peerId) == snapshot) dirty -= key(snapshot.peerId)
    }
    @Synchronized fun pending(): List<DraftSnapshot> = dirty.mapNotNull(values::get)
    @Synchronized fun texts(): Map<String, String> = values.mapValues { it.value.text }
    @Synchronized fun clear(peerId: String? = null) {
        if (peerId != null) edit(peerId, "") else values.keys.toList().forEach { edit(it, "") }
    }
}
