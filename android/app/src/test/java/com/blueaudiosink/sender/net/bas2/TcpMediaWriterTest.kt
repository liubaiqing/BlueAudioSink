package com.blueaudiosink.sender.net.bas2

import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.io.OutputStream
import java.net.Socket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

class TcpMediaWriterTest {
    private class BlockedSocket : Socket() {
        val entered = CountDownLatch(1)
        val released = CountDownLatch(1)
        override fun getOutputStream(): OutputStream = object : OutputStream() {
            override fun write(value: Int) {
                entered.countDown()
                try { released.await() } catch (_: InterruptedException) { }
                throw IOException("closed by owner")
            }
        }
        override fun close() { released.countDown() }
    }

    @Test fun stalledWriteIsClosedAndFailureReportedOnce() {
        val socket = BlockedSocket()
        val failed = CountDownLatch(1)
        val count = AtomicInteger()
        val writer = TcpMediaWriter(socket) { count.incrementAndGet(); failed.countDown() }
        try {
            writer.offer(ByteArray(1000))
            assertTrue(socket.entered.await(1, TimeUnit.SECONDS))
            assertTrue(failed.await(2, TimeUnit.SECONDS))
            assertEquals(0, socket.released.count)
            Thread.sleep(100)
            assertEquals(1, count.get())
        } finally { writer.close() }
    }

    @Test fun fullQueueNeverWaitsForBlockedWriterOnCaptureThread() {
        val socket = BlockedSocket()
        val failed = CountDownLatch(1)
        val writer = TcpMediaWriter(socket) { failed.countDown() }
        try {
            writer.offer(ByteArray(1000))
            assertTrue(socket.entered.await(1, TimeUnit.SECONDS))
            repeat(40) { writer.offer(ByteArray(1000)) }
            val returned = CountDownLatch(1)
            val capture = Thread { writer.offer(ByteArray(1000)); returned.countDown() }
            capture.start()
            assertTrue("Capture blocked on network teardown", returned.await(200, TimeUnit.MILLISECONDS))
            assertTrue(failed.await(1, TimeUnit.SECONDS))
            capture.join(1000)
        } finally { writer.close() }
    }
}
