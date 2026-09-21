package com.bluelink.android.domain

import com.bluelink.core.AttachmentRole

internal data class BackgroundTransferSummary(val activeCount: Int, val needsCpu: Boolean, val peerId: String?) {
    companion object {
        fun from(items: Collection<TransferItem>): BackgroundTransferSummary {
            val active = items.filter { it.role != AttachmentRole.IMAGE_PREVIEW && it.status in HistoryQuery.activeStatuses }
                .sortedWith(compareBy<TransferItem> { it.startedAtEpochMs }.thenBy { it.id })
            return BackgroundTransferSummary(active.size, active.any { it.status in setOf(TransferStatus.TRANSFERRING,
                TransferStatus.RESUMING, TransferStatus.VERIFYING, TransferStatus.COMMITTING) },active.firstOrNull()?.peerId)
        }
    }
}
