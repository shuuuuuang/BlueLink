package com.bluelink.android.domain

import com.bluelink.android.files.FileTypeCatalog
import java.time.LocalDate
import java.time.Instant
import java.time.ZoneId
import java.text.Collator
import java.util.Locale

enum class FileKind { ALL, IMAGES, FILES }
enum class HistorySort { TIME, NAME, SIZE }
data class FileQueryOptions(val query: String = "", val status: FileStatusFilter = FileStatusFilter.ALL,
    val direction: FileDirectionFilter = FileDirectionFilter.ALL, val peerId: String? = null,
    val kind: FileKind = FileKind.ALL, val start: LocalDate? = null, val end: LocalDate? = null,
    val sort: HistorySort = HistorySort.TIME, val descending: Boolean = true, val locale: Locale = Locale.SIMPLIFIED_CHINESE)

object HistorySearch {
    fun files(items: List<TransferItem>, options: FileQueryOptions, zone: ZoneId = ZoneId.systemDefault(), checkCancelled: () -> Unit = {}): List<TransferItem> {
        val query = options.query.trim()
        val filtered = items.filter { item ->
            checkCancelled()
            val image = FileTypeCatalog.classify(item.name, item.mimeType) == "file-image"
            val day = Instant.ofEpochMilli(item.startedAtEpochMs).atZone(zone).toLocalDate()
            (options.peerId == null || item.peerId.equals(options.peerId, true)) &&
            item.name.contains(query, ignoreCase = true) &&
            (options.kind == FileKind.ALL || (options.kind == FileKind.IMAGES) == image) &&
            (options.start == null || day >= options.start) && (options.end == null || day <= options.end) &&
            when(options.direction) { FileDirectionFilter.ALL -> true; FileDirectionFilter.SENT -> item.outgoing; FileDirectionFilter.RECEIVED -> !item.outgoing } &&
            when(options.status) {
                FileStatusFilter.ALL -> true; FileStatusFilter.ACTIVE -> item.status in HistoryQuery.activeStatuses
                FileStatusFilter.COMPLETED -> item.status == TransferStatus.COMPLETED
                FileStatusFilter.INCOMPLETE -> item.status in setOf(TransferStatus.FAILED, TransferStatus.REJECTED, TransferStatus.CANCELED)
                FileStatusFilter.FAILED -> item.status == TransferStatus.FAILED
                FileStatusFilter.REJECTED -> item.status == TransferStatus.REJECTED
                FileStatusFilter.CANCELED -> item.status == TransferStatus.CANCELED
            }
        }
        val collator = Collator.getInstance(options.locale).apply { strength = Collator.SECONDARY }
        // Collation is expensive; build each distinct name key once per immutable query.
        val nameKeys = if(options.sort == HistorySort.NAME) filtered.map { it.name }.distinct().associateWith {
            checkCancelled(); collator.getCollationKey(it)
        } else emptyMap()
        val primary = when(options.sort) {
            HistorySort.TIME -> compareBy<TransferItem> { it.startedAtEpochMs }
            HistorySort.SIZE -> compareBy<TransferItem> { it.totalBytes }
            HistorySort.NAME -> Comparator<TransferItem> { a, b -> nameKeys.getValue(a.name).compareTo(nameKeys.getValue(b.name)) }
        }
        return filtered.sortedWith((if(options.descending) primary.reversed() else primary).thenBy { it.id.toString() })
    }
}
