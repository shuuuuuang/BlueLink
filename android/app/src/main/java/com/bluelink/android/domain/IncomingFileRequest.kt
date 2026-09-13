package com.bluelink.android.domain

import java.util.UUID

data class IncomingFileRequest(val requestId: UUID = UUID.randomUUID(), val sessionId: UUID,
                               val peerName: String, val transfer: TransferItem)
