package com.bluelink.android.transport

import org.junit.Assert.*
import org.junit.Test
import java.nio.channels.Pipe
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class InterruptibleAccessoryStreamsTest {
    @Test fun closeInterruptsBlockedReadAndWriteBeforeReleasingDescriptor() {
        val incoming = Pipe.open()
        val outgoing = Pipe.open()
        val pool = Executors.newFixedThreadPool(2)
        var released = 0
        val streams = InterruptibleAccessoryStreams(incoming.source(), outgoing.sink()) {
            assertFalse(incoming.source().isOpen); assertFalse(outgoing.sink().isOpen)
            released++
        }
        try {
            val started = CountDownLatch(2)
            val read = pool.submit<Boolean> { started.countDown(); runCatching { streams.input.read() }.isFailure }
            val write = pool.submit<Boolean> { started.countDown(); runCatching { streams.output.write(ByteArray(4 * 1024 * 1024)) }.isFailure }
            assertTrue(started.await(2, TimeUnit.SECONDS))
            Thread.sleep(100)
            assertFalse(read.isDone); assertFalse(write.isDone)
            streams.close()
            assertTrue(read.get(2, TimeUnit.SECONDS)); assertTrue(write.get(2, TimeUnit.SECONDS))
            streams.close(); assertEquals(1, released)
        } finally { streams.close(); incoming.sink().close(); outgoing.source().close(); pool.shutdownNow() }
    }
}
