package com.bluelink.android.domain

import java.time.LocalDate
import java.util.Locale

/** Filter state deliberately excludes the search keyword and ordering. */
internal data class FileFilterSelection(
    val status: FileStatusFilter = FileStatusFilter.ALL,
    val direction: FileDirectionFilter = FileDirectionFilter.ALL,
    val peerId: String? = null,
    val kind: FileKind = FileKind.ALL,
    val start: LocalDate? = null,
    val end: LocalDate? = null,
) {
    val valid: Boolean get() = start == null || end == null || start <= end
    fun count(scopePeerId: String?) = listOf(status != FileStatusFilter.ALL,
        direction != FileDirectionFilter.ALL, scopePeerId == null && peerId != null,
        kind != FileKind.ALL, start != null || end != null).count { it }
    fun options(query: String, sort: HistorySort, descending: Boolean, scopePeerId: String?, locale: Locale) =
        FileQueryOptions(query, status, direction, scopePeerId ?: peerId, kind, start, end, sort, descending, locale)
}
