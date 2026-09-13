package com.bluelink.android.session

import com.bluelink.android.domain.HistoryQuery
import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TransferStatus
import java.util.UUID

/** Owned by one session, under the supervisor lock. A new connection gets a new ledger. */
internal class SessionTransferLedger {
    private val active = linkedMapOf<UUID, TransferItem>()
    private var closed = false
    fun record(value: TransferItem): TransferItem? {
        if (closed) return null
        if (value.status in HistoryQuery.activeStatuses) active[value.id] = value else active.remove(value.id)
        return value
    }
    fun close(): List<TransferItem> {
        if (closed) return emptyList()
        closed = true
        return active.values.map { it.copy(status = TransferStatus.FAILED, bytesPerSecond = 0.0,
            failureDetail = "设备通道已断开，请由发送方重试传输") }.also { active.clear() }
    }
}
