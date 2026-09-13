package com.bluelink.android.domain

enum class TransferAction { OPEN, COPY, REVEAL, SAVE, DETAILS, PAUSE, RESUME, RETRY, FAILURE, CANCEL, DELETE }

/** UI availability follows real transport states; verification/commit must not be interrupted by pause. */
object TransferActions {
    fun available(item: TransferItem): List<TransferAction> = buildList {
        if (item.status == TransferStatus.COMPLETED && !item.localUri.isNullOrBlank()) {
            add(TransferAction.OPEN); add(TransferAction.COPY); add(TransferAction.REVEAL); add(TransferAction.SAVE)
        }
        add(TransferAction.DETAILS)
        when (item.status) {
            TransferStatus.TRANSFERRING, TransferStatus.RESUMING -> add(TransferAction.PAUSE)
            TransferStatus.PAUSED -> add(TransferAction.RESUME)
            TransferStatus.FAILED, TransferStatus.REJECTED, TransferStatus.CANCELED -> {
                if (item.outgoing && !item.localUri.isNullOrBlank()) add(TransferAction.RETRY)
                if (item.status != TransferStatus.CANCELED) add(TransferAction.FAILURE)
            }
            else -> Unit
        }
        add(if (item.status in HistoryQuery.activeStatuses) TransferAction.CANCEL else TransferAction.DELETE)
    }
}
