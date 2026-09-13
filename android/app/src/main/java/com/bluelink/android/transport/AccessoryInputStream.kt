package com.bluelink.android.transport

import java.io.InputStream

/** AOA may discard the unread tail of a USB packet. Always read a complete 16 KiB packet. */
internal class AccessoryInputStream(private val source: InputStream) : InputStream() {
    private val packet = ByteArray(16 * 1024)
    private var offset = 0
    private var count = 0
    override fun read(): Int {
        if (!fill()) return -1
        return packet[offset++].toInt() and 255
    }
    override fun read(buffer: ByteArray, off: Int, len: Int): Int {
        require(off >= 0 && len >= 0 && off <= buffer.size - len)
        if (len == 0) return 0
        if (!fill()) return -1
        val size = minOf(len, count - offset)
        packet.copyInto(buffer, off, offset, offset + size)
        offset += size
        return size
    }
    private fun fill(): Boolean {
        if (offset < count) return true
        do { count = source.read(packet, 0, packet.size) } while (count == 0)
        offset = 0
        return count > 0
    }
    override fun close() = source.close()
}
