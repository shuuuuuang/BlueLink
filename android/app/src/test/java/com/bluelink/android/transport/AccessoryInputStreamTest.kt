package com.bluelink.android.transport

import org.junit.Assert.*
import org.junit.Test
import java.io.InputStream
import java.io.DataInputStream

class AccessoryInputStreamTest {
    // A short read discards the tail, like the Android accessory file descriptor.
    private class PacketDevice(private val content: ByteArray) : InputStream() {
        var position = 0
        var closed = false
        override fun read(): Int = error("raw single-byte reads lose the USB packet")
        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (position == content.size) return -1
            val packetSize = minOf(16384, content.size - position)
            val size = minOf(packetSize, len)
            content.copyInto(b, off, position, position + size)
            position += packetSize
            return size
        }
        override fun close() { closed = true }
    }
    @Test fun shortHeadersAndLargePayloadsPreserveEveryPacketByte() {
        val data = ByteArray(65536 + 89) { (it * 37).toByte() }
        val device = PacketDevice(data)
        val input = DataInputStream(AccessoryInputStream(device))
        val actual = ByteArray(data.size)
        actual[0] = input.readByte()
        input.readFully(actual, 1, 37)
        input.readFully(actual, 38, data.size - 38)
        assertArrayEquals(data, actual)
        assertEquals(-1, input.read())
        input.close(); assertTrue(device.closed)
    }
    @Test fun zeroLengthReadDoesNotConsumePacket() {
        val device = PacketDevice(byteArrayOf(7, 8))
        val input = AccessoryInputStream(device)
        assertEquals(0, input.read(ByteArray(0), 0, 0))
        assertEquals(0, device.position)
        assertEquals(7, input.read()); assertEquals(8, input.read())
    }
}
