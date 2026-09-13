package com.bluelink.android.session

import com.bluelink.core.MtpFileCipher
import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import java.util.UUID

class MtpFileCipherTest {
    @Test fun authenticatesEmptyFilesAndMultiRecordPayloads() {
        val id = UUID.fromString("12345678-1234-5678-9abc-123456789abc")
        val key = ByteArray(32) { it.toByte() }
        for (size in listOf(0, 1, 17, 65537, 1048576, 1048593, 2097153)) {
            val source = ByteArray(size) { (it * 31 + 7).toByte() }
            val encoded = ByteArrayOutputStream()
            MtpFileCipher.encrypt(ByteArrayInputStream(source), encoded, id, size.toLong(), key) {}
            val bytes = encoded.toByteArray()
            assertEquals(MtpFileCipher.encodedSize(size.toLong()), bytes.size.toLong())
            println("BLM1 $size " + MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02X".format(it) })
            val plain = ByteArrayOutputStream()
            MtpFileCipher.decrypt(ByteArrayInputStream(bytes), id, size.toLong(), key, { data, count -> plain.write(data, 0, count) }, {})
            assertArrayEquals(source, plain.toByteArray())
            for (bad in listOf(bytes.dropLast(1).toByteArray(), bytes.clone().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }, bytes + byteArrayOf(1))) {
                var rejected = false
                try { MtpFileCipher.decrypt(ByteArrayInputStream(bad), id, size.toLong(), key, { _, _ -> }, {}) }
                catch (_: Exception) { rejected = true }
                assertTrue("Corrupted file accepted", rejected)
            }
        }
    }
}
