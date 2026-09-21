package com.bluelink.android.domain

object FileBatchSelection {
    /** UI state gate; execution still rechecks connectivity and local file availability. */
    fun canRequest(items: List<TransferItem>, action: FileBatchAction): Boolean = when (action) {
        FileBatchAction.RETRY -> items.any { it.outgoing && it.status == TransferStatus.FAILED }
        FileBatchAction.CANCEL -> items.any { it.status in HistoryQuery.activeStatuses }
        FileBatchAction.SHARE, FileBatchAction.DELETE_RECORDS -> items.isNotEmpty()
    }

    fun rangeTarget(ids: List<String>, selected: List<String>, anchor: String?, first: String?, last: String?): String? {
        val start = ids.indexOf(anchor)
        val a = ids.indexOf(first)
        val b = ids.indexOf(last)
        if (start < 0 || a < 0 || b < a) return null
        val edges = if (start > (a + b) / 2) listOf(a, b) else listOf(b, a)
        return edges.firstOrNull { end -> end != start &&
            ids.subList(minOf(start, end), maxOf(start, end) + 1).any { it !in selected } }?.let(ids::get)
    }

    /** Null reports the selection limit instead of silently selecting a partial range. */
    fun selectRange(ids: List<String>, selected: List<String>, anchor: String?, target: String): List<String>? {
        val start = ids.indexOf(anchor)
        val end = ids.indexOf(target)
        if (start < 0 || end < 0) return selected
        val result = (selected + ids.subList(minOf(start, end), maxOf(start, end) + 1)).distinct()
        return result.takeIf { it.size <= FileBatchPolicy.MAXIMUM_SELECTION }
    }
}
