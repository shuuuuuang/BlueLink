package com.bluelink.android.data.local

import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TransferStatus
import java.util.UUID

internal fun TransferEntity.toTransferItem() = TransferItem(
    id = UUID.fromString(transferId), name = fileName, totalBytes = totalBytes,
    completedBytes = completedBytes, outgoing = direction == "OUTGOING",
    status = runCatching { TransferStatus.valueOf(status) }.getOrDefault(TransferStatus.FAILED),
    messageId = messageId?.let(UUID::fromString), mimeType = mimeType,
    localUri = localUri, failureDetail = failureDetail, peerId = peerId,
    startedAtEpochMs = createdAt, updatedAtEpochMs = updatedAt,
    sourceSha256 = sha256?.joinToString("") { "%02x".format(it) },
)
