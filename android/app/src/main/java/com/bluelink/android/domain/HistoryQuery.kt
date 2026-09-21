package com.bluelink.android.domain

import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId

enum class HistoryKind(val label: String) { ALL("全部"), TEXT("文本"), IMAGES("图片"), FILES("文件"), DATE("日期") }
enum class FileStatusFilter(val label: String) { ALL("全部状态"), ACTIVE("传输中"), COMPLETED("已完成"), INCOMPLETE("未完成"), FAILED("失败"), REJECTED("未接收"), CANCELED("已取消") }
enum class FileDirectionFilter(val label: String) { ALL("全部方向"), SENT("已发送"), RECEIVED("已接收") }

object HistoryQuery {
    val activeStatuses = setOf(TransferStatus.OFFERED, TransferStatus.QUEUED, TransferStatus.TRANSFERRING,
        TransferStatus.PAUSED, TransferStatus.REMOTE_PAUSED, TransferStatus.RESUMING, TransferStatus.VERIFYING, TransferStatus.COMMITTING)

    fun messages(items: List<ChatItem>, query: String, kind: HistoryKind, date: LocalDate? = null,
                 zone: ZoneId = ZoneId.systemDefault(), endDate: LocalDate? = date): List<ChatItem> = items.filter { item ->
        val needle = query.trim()
        val day = item.timestamp.atZone(zone).toLocalDate()
        (date == null || day >= date) && (endDate == null || day <= endDate) &&
        (needle.isEmpty() || item.text.contains(needle, ignoreCase = true) ||
            item.attachments.any { it.fileName.contains(needle, ignoreCase = true) }) && when (kind) {
            HistoryKind.ALL -> true
            HistoryKind.TEXT -> item.kind == ChatItemKind.TEXT
            HistoryKind.IMAGES -> item.attachments.any { it.isImage }
            HistoryKind.FILES -> item.attachments.any { !it.isImage }
            HistoryKind.DATE -> true
        }
    }.sortedWith(compareByDescending<ChatItem> { it.timestamp }.thenBy { it.id.toString() })

    fun files(items: List<TransferItem>, query: String, status: FileStatusFilter, direction: FileDirectionFilter,
              peerId: String?): List<TransferItem> = HistorySearch.files(items, FileQueryOptions(query, status, direction, peerId))

    fun showTimestamp(previous: Instant?, current: Instant, zone: ZoneId = ZoneId.systemDefault()): Boolean =
        previous == null || previous.atZone(zone).toLocalDate() != current.atZone(zone).toLocalDate() ||
            Duration.between(previous, current).toMinutes() >= 5
}
