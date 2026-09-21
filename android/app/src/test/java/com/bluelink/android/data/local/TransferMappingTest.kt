package com.bluelink.android.data.local

import com.bluelink.android.domain.TransferStatus
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class TransferMappingTest {
    @Test fun historyRetainsRecordedTimesMetadataAndDirectionAcrossRepeatedLoads() {
        val id = UUID.randomUUID(); val message = UUID.randomUUID()
        val row = TransferEntity(id.toString(), "other-peer", message.toString(), "INCOMING", "COMPLETED",
            "photo.png", "image/png", 200, 200, "content://qa/photo", createdAt = 1700000000000,
            updatedAt = 1700000002000, sha256 = ByteArray(32) { 0x9f.toByte() })
        val item = row.toTransferItem()
        assertEquals(row.createdAt, item.startedAtEpochMs); assertEquals(row.updatedAt, item.updatedAtEpochMs)
        assertEquals(row.toTransferItem(), item)
        assertEquals("9f".repeat(32), item.sourceSha256)
        assertEquals(id, item.id); assertEquals(message, item.messageId); assertEquals("other-peer", item.peerId)
        assertFalse(item.outgoing); assertEquals(TransferStatus.COMPLETED, item.status)
        assertEquals(row.localUri, item.localUri); assertEquals("image/png", item.mimeType)
    }

    @Test fun unknownStoredStateFailsClosedAndRetainsErrorDetails() {
        val row = TransferEntity(UUID.randomUUID().toString(), "peer", direction = "OUTGOING", status = "UNKNOWN",
            fileName = "file", totalBytes = 20, failureDetail = "interrupted", createdAt = 1, updatedAt = 2)
        val item = row.toTransferItem()
        assertTrue(item.outgoing); assertEquals(TransferStatus.FAILED, item.status)
        assertEquals("interrupted", item.failureDetail)
    }
}
