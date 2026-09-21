package com.bluelink.android.session

import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TransferStatus

internal object TransferRetryEligibility {
    fun allows(item: TransferItem, peerId: String?, connected: Boolean, trusted: Boolean): Boolean =
        connected && trusted && !peerId.isNullOrBlank() && item.peerId.equals(peerId, ignoreCase = true) &&
            item.outgoing && item.id != java.util.UUID(0, 0) && item.status in setOf(
                TransferStatus.FAILED, TransferStatus.REJECTED, TransferStatus.CANCELED)
}
