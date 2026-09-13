package com.bluelink.android.domain

import com.bluelink.core.AttachmentRole
import java.util.UUID

/** Global history is independent of the selected conversation. Pending events bridge Room writes. */
internal data class TransferHistoryIndex(
    private val stored: Map<UUID, TransferItem> = emptyMap(),
    private val pending: Map<UUID, TransferItem> = emptyMap(),
    private val removed: Set<UUID> = emptySet(),
) {
    val items: Map<UUID, TransferItem> get() = stored + pending
    fun accepts(id: UUID): Boolean = id !in removed

    fun restore(values: List<TransferItem>): TransferHistoryIndex {
        val current = items
        val snapshot = values.filter { accepts(it.id) && it.role != AttachmentRole.IMAGE_PREVIEW }
            .associate { it.id to it.copy(bytesPerSecond = current[it.id]?.bytesPerSecond ?: it.bytesPerSecond) }
        // Once Room acknowledges an event, future deletion/retention must not revive its memory copy.
        val unacknowledged = pending.filter { (id, item) ->
            snapshot[id]?.updatedAtEpochMs?.let { it < item.updatedAtEpochMs } ?: true
        }
        return copy(stored = snapshot, pending = unacknowledged)
    }

    fun receive(item: TransferItem): TransferHistoryIndex {
        if (!accepts(item.id) || item.role == AttachmentRole.IMAGE_PREVIEW) return this
        if ((items[item.id]?.updatedAtEpochMs ?: Long.MIN_VALUE) > item.updatedAtEpochMs) return this
        return copy(pending = pending + (item.id to item))
    }

    fun forget(ids: Set<UUID>): TransferHistoryIndex = copy(
        stored = stored - ids, pending = pending - ids, removed = removed + ids)
    fun clear(): TransferHistoryIndex = forget(items.keys)
    fun retainSince(cutoff: Long): TransferHistoryIndex = forget(items.values
        .filter { it.updatedAtEpochMs < cutoff }.map { it.id }.toSet())
}
