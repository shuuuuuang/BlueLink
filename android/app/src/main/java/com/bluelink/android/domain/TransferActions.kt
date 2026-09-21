package com.bluelink.android.domain

enum class TransferAction { OPEN, SHARE, SAVE, DETAILS, PAUSE, RESUME, RETRY, RESELECT, BLUETOOTH, FAILURE, CANCEL, DELETE }

/** UI availability follows real transport states; verification/commit must not be interrupted by pause. */
object TransferActions {
    fun available(item: TransferItem): List<TransferAction> = buildList {
        if (item.status == TransferStatus.COMPLETED && !item.localUri.isNullOrBlank()) {
            add(TransferAction.OPEN); add(TransferAction.SHARE); add(TransferAction.SAVE)
        }
        add(TransferAction.DETAILS)
        if (item.canSwitchToBluetooth) add(TransferAction.BLUETOOTH)
        when (item.status) {
            TransferStatus.TRANSFERRING, TransferStatus.RESUMING -> add(TransferAction.PAUSE)
            TransferStatus.PAUSED -> add(TransferAction.RESUME)
            TransferStatus.FAILED, TransferStatus.REJECTED, TransferStatus.CANCELED -> {
                if (item.outgoing && !item.localUri.isNullOrBlank()) add(TransferAction.RETRY)
                if (item.outgoing && item.sourceSha256?.length == 64) add(TransferAction.RESELECT)
                if (item.status != TransferStatus.CANCELED) add(TransferAction.FAILURE)
            }
            else -> Unit
        }
        add(if (item.status in HistoryQuery.activeStatuses) TransferAction.CANCEL else TransferAction.DELETE)
    }
}
