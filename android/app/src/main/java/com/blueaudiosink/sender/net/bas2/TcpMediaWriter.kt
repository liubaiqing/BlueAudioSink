package com.blueaudiosink.sender.net.bas2

import java.io.Closeable
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** Independent bounded TCP writer. Closing the socket interrupts an outstanding write. */
internal class TcpMediaWriter(private val socket: Socket, private val failed: () -> Unit) : Closeable {
    private val frames = ArrayBlockingQueue<ByteArray>(40)
    private val watchdog = Executors.newSingleThreadScheduledExecutor()
    @Volatile private var closed = false
    private val failureReported = AtomicBoolean()
    @Volatile private var overflow = false
    @Volatile private var writeStartedNs = 0L
    private val worker = Thread({
        try {
            val output = socket.getOutputStream()
            while (!closed) {
                val packet = frames.poll(100, TimeUnit.MILLISECONDS) ?: continue
                writeStartedNs = System.nanoTime()
                output.write(ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(packet.size).array())
                output.write(packet)
                writeStartedNs = 0
            }
        } catch (_: Exception) {
            if (!closed) fail()
        }
    }, "bas2-tcp-audio").apply { isDaemon = true }

    init {
        watchdog.scheduleAtFixedRate({
            val started = writeStartedNs
            if (!closed && (overflow || (started != 0L && System.nanoTime() - started > 300_000_000))) {
                fail()
            }
        }, 50, 50, TimeUnit.MILLISECONDS)
        worker.start()
    }

    fun offer(packet: ByteArray) {
        if (!closed && !frames.offer(packet)) {
            // Re-establishing a stream is safer than accumulating stale audio.
            // The capture thread must never join a blocked network writer.
            overflow = true
        }
    }

    fun clear() { frames.clear() }

    private fun fail() {
        if (!failureReported.compareAndSet(false, true)) return
        close()
        failed()
    }

    override fun close() {
        closed = true
        frames.clear()
        watchdog.shutdownNow()
        runCatching { socket.close() }
        worker.interrupt()
        // Closing the socket cancels write(); the worker owns its own exit.
    }
}
