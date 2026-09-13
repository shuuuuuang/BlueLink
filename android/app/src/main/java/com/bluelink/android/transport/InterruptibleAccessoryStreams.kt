package com.bluelink.android.transport

import java.io.Closeable
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.channels.ReadableByteChannel
import java.nio.channels.WritableByteChannel
import java.util.concurrent.atomic.AtomicBoolean

/** Close interruptible channels before releasing the shared accessory descriptor.
 * Closing only ParcelFileDescriptor can leave a native read holding /dev/usb_accessory open.
 */
internal class InterruptibleAccessoryStreams(
    private val reader: ReadableByteChannel,
    private val writer: WritableByteChannel,
    private val releaseDescriptor: () -> Unit,
) : Closeable {
    private val closed = AtomicBoolean()
    val input: InputStream = AccessoryInputStream(object : InputStream() {
        override fun read(): Int = ByteArray(1).let { if (read(it, 0, 1) < 0) -1 else it[0].toInt() and 255 }
        override fun read(b: ByteArray, off: Int, len: Int): Int = reader.read(ByteBuffer.wrap(b, off, len))
        override fun close() = this@InterruptibleAccessoryStreams.close()
    })
    val output: OutputStream = object : OutputStream() {
        override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)
        override fun write(b: ByteArray, off: Int, len: Int) {
            val buffer = ByteBuffer.wrap(b, off, len)
            while (buffer.hasRemaining()) writer.write(buffer)
        }
        override fun close() = this@InterruptibleAccessoryStreams.close()
    }
    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        runCatching { reader.close() }
        runCatching { writer.close() }
        releaseDescriptor()
    }
}
