package com.bluelink.android.domain

import org.junit.Assert.assertEquals
import org.junit.Test

class MessageDeliveryPolicyTest {
    @Test fun readReceiptCannotBeReplacedByAnyLateCallback() {
        MessageStatus.entries.forEach { late ->
            assertEquals(MessageStatus.READ, MessageDeliveryPolicy.merge(MessageStatus.READ, late))
        }
    }

    @Test fun deliveryProofSurvivesWriteFailureAndOutOfOrderRead() {
        val events = listOf(MessageStatus.READ, MessageStatus.DELIVERED, MessageStatus.SENT,
            MessageStatus.LOCAL_QUEUED, MessageStatus.FAILED)
        events.indices.forEach { offset ->
            val reordered = events.drop(offset) + events.take(offset)
            assertEquals(MessageStatus.READ, reordered.fold(MessageStatus.SENDING, MessageDeliveryPolicy::merge))
        }
        assertEquals(MessageStatus.DELIVERED, MessageDeliveryPolicy.merge(MessageStatus.FAILED, MessageStatus.DELIVERED))
        assertEquals(MessageStatus.DELIVERED, MessageDeliveryPolicy.merge(MessageStatus.DELIVERED, MessageStatus.FAILED))
    }

    @Test fun failedLocalWriteCanQueueAndLaterSendButCannotUndoRejection() {
        val queued = MessageDeliveryPolicy.merge(MessageStatus.SENDING, MessageStatus.LOCAL_QUEUED)
        assertEquals(MessageStatus.LOCAL_QUEUED, queued)
        assertEquals(MessageStatus.SENT, MessageDeliveryPolicy.merge(queued, MessageStatus.SENT))
        assertEquals(MessageStatus.FAILED, MessageDeliveryPolicy.merge(MessageStatus.FAILED, MessageStatus.SENT))
        assertEquals(MessageStatus.FAILED, MessageDeliveryPolicy.merge(MessageStatus.SENT, MessageStatus.FAILED))
    }

    @Test fun receiptsCannotChangeIncomingMessages() {
        MessageStatus.entries.forEach {
            assertEquals(MessageStatus.RECEIVED, MessageDeliveryPolicy.merge(MessageStatus.RECEIVED, it))
        }
    }
}
