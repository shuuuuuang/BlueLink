package com.bluelink.android.domain

/** Search rows and their menus must address the same attachment and current transfer state. */
object SearchResultActions {
    /** Keep live state authoritative and never resurrect a successfully deleted history-only result. */
    fun merge(history: List<ChatItem>, live: List<ChatItem>, deleted: Collection<String>): List<ChatItem> {
        val removed = deleted.toSet()
        return (history + live).associateBy { it.id }.values.filter { it.id.toString() !in removed }
    }

    fun attachment(message: ChatItem, query: String): ChatAttachment? =
        message.attachments.firstOrNull { it.fileName.contains(query.trim(), ignoreCase = true) }
            ?: message.attachments.firstOrNull()

    fun showsFilename(message: ChatItem, attachment: ChatAttachment?, query: String): Boolean =
        attachment != null && (query.isBlank() || attachment.fileName.contains(query.trim(), ignoreCase = true) ||
            !message.text.contains(query.trim(), ignoreCase = true))

    fun transfer(message: ChatItem, attachment: ChatAttachment, transfers: List<TransferItem>): TransferItem =
        transfers.firstOrNull { it.id == attachment.transferId }?.copy(
            messageId = message.id, attachmentId = attachment.attachmentId)
            ?: TransferItem(attachment.transferId, attachment.fileName, attachment.sizeBytes,
                attachment.completedBytes, message.outgoing,
                runCatching { TransferStatus.valueOf(attachment.state) }.getOrDefault(TransferStatus.FAILED),
                messageId = message.id, attachmentId = attachment.attachmentId,
                mimeType = attachment.mimeType, localUri = attachment.localUri,
                startedAtEpochMs = message.timestamp.toEpochMilli(), updatedAtEpochMs = message.timestamp.toEpochMilli())
}
