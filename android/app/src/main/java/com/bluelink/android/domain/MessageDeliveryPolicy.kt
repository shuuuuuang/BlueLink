package com.bluelink.android.domain

/** Transport completion is weaker evidence than a peer receipt, even when it arrives later. */
internal object MessageDeliveryPolicy {
    fun merge(current: MessageStatus, incoming: MessageStatus): MessageStatus = when {
        current == MessageStatus.RECEIVED -> current
        incoming == MessageStatus.RECEIVED -> current
        current == MessageStatus.READ || incoming == MessageStatus.READ -> MessageStatus.READ
        current == MessageStatus.DELIVERED || incoming == MessageStatus.DELIVERED -> MessageStatus.DELIVERED
        current == MessageStatus.FAILED || incoming == MessageStatus.FAILED -> MessageStatus.FAILED
        current == MessageStatus.SENT || incoming == MessageStatus.SENT -> MessageStatus.SENT
        // A failed local write returns to the queue; it has not reached the peer yet.
        else -> incoming
    }
}
