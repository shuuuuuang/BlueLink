package com.bluelink.android.domain

import java.util.UUID

enum class MessageBatchReason { MISSING, ACTIVE, FAILED }
data class MessageBatchResult(val id: UUID, val deleted: Boolean, val reason: MessageBatchReason? = null)

object MessageBatch {
    /** Rechecks each record immediately before deletion; failures never hide successful items. */
    suspend fun delete(ids: List<UUID>, lookup: (UUID) -> ChatItem?,
        activeTransfer: (ChatItem) -> Boolean, remove: suspend (UUID) -> Unit): List<MessageBatchResult> =
        ids.distinct().map { id ->
            val current = lookup(id)
            when {
                current == null -> MessageBatchResult(id, false, MessageBatchReason.MISSING)
                !canDelete(current) || activeTransfer(current) -> MessageBatchResult(id, false, MessageBatchReason.ACTIVE)
                else -> try { remove(id); MessageBatchResult(id, true) }
                catch (cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
                catch (_: Exception) { MessageBatchResult(id, false, MessageBatchReason.FAILED) }
            }
        }

    fun ordered(messages: List<ChatItem>, ids: Collection<UUID>): List<ChatItem> {
        val selected = ids.toSet()
        return messages.filter { it.id in selected }.distinctBy { it.id }.sortedWith(compareBy<ChatItem> { it.timestamp }.thenBy { it.id.toString() })
    }
    fun copyText(messages: List<ChatItem>): String = messages.flatMap { message ->
        (if (message.text.isNotBlank()) listOf(message.text) else emptyList()) + message.attachments.map { "[${it.fileName}]" }
    }.joinToString("\n")
    fun canDelete(message: ChatItem): Boolean =
        !(message.outgoing && message.status in setOf(MessageStatus.LOCAL_QUEUED, MessageStatus.SENDING)) &&
            message.attachments.none { it.isTransferActive || it.recoveryPending }
    fun attachments(messages: List<ChatItem>): List<ChatAttachment> =
        messages.flatMap { it.attachments }.distinctBy { it.attachmentId }

    /** Inclusive loaded-message range; never changes selection when an endpoint disappeared. */
    fun selectRange(messages: List<ChatItem>, selected: List<String>, anchor: String?, target: String): List<String> {
        val start = messages.indexOfFirst { it.id.toString() == anchor }
        val end = messages.indexOfFirst { it.id.toString() == target }
        if (start < 0 || end < 0) return selected
        val range = messages.subList(minOf(start, end), maxOf(start, end) + 1).map { it.id.toString() }
        return (selected + range).distinct()
    }

    /** Partial intersections (even by one pixel) cannot be range endpoints. */
    fun fullyVisible(offset: Int, size: Int, viewportStart: Int, viewportEnd: Int): Boolean =
        size > 0 && viewportEnd > viewportStart && offset >= viewportStart &&
            offset.toLong() + size <= viewportEnd.toLong()

    fun rangeTarget(messages: List<ChatItem>, selected: List<String>, anchor: String?, first: Int, last: Int): Int? {
        val start = messages.indexOfFirst { it.id.toString() == anchor }
        if (start < 0 || first !in messages.indices || last !in messages.indices || first > last) return null
        val edges = if (start > (first + last) / 2) listOf(first, last) else listOf(last, first)
        return edges.firstOrNull { end -> end != start &&
            messages.subList(minOf(start,end), maxOf(start,end)+1).any { it.id.toString() !in selected } }
    }
}
