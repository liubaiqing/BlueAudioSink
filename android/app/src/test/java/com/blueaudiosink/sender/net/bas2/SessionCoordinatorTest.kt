package com.blueaudiosink.sender.net.bas2

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

class SessionCoordinatorTest {
    private val wifi = LocalRoute("wlan0", "192.168.0.116", 24, 101, 1)
    private val ap = LocalRoute("ap0", "192.168.233.231", 24, null, 2)
    private fun await(condition: () -> Boolean) {
        val end = System.nanoTime() + TimeUnit.SECONDS.toNanos(4)
        while (!condition()) { if (System.nanoTime() > end) fail("Timed out awaiting session state"); Thread.sleep(5) }
    }
    private class Fake(private val verifyWork: () -> Unit = {}) : SessionConnection {
        override val deviceId = "a".repeat(32)
        override val deviceName = "test-PC"
        override val sessionId = java.util.UUID.randomUUID().toString()
        @Volatile var tcpPreferred = false
        override val usingTcp get() = tcpPreferred
        override fun preferTcp() { tcpPreferred = true }
        @Volatile var heartbeatFailure: Exception? = null
        val closes = AtomicInteger()
        val sent = AtomicInteger()
        val starts = AtomicInteger()
        override fun verify() = verifyWork()
        override fun start() { starts.incrementAndGet() }
        override fun stop() { }
        override fun heartbeat() { heartbeatFailure?.let { throw it } }
        override fun sendAudio(payload: ByteArray, timestampUs: Int, discontinuity: Boolean) { sent.incrementAndGet() }
        override fun close() { closes.incrementAndGet() }
    }

    @Test fun unrelatedInterfacesKeepHealthyStreamAndLostInterfaceResumesIntent() {
        val connections = java.util.Collections.synchronizedList(mutableListOf<Fake>())
        val session = SessionCoordinator({ _, _, _ -> Fake().also(connections::add) }, {})
        try {
            session.networkChanged(listOf(wifi))
            session.selectDevice(null, listOf("192.168.0.165"))
            session.setSending(true)
            await { session.snapshot.phase == ConnectionPhase.STREAMING }
            val first = connections.first()
            session.sendAudio(ByteArray(960), 0, false)
            assertEquals(1, first.sent.get())
            session.networkChanged(listOf(wifi.copy(generation = 2), ap))
            Thread.sleep(100)
            assertEquals(1, connections.size)
            assertEquals(0, first.closes.get())
            session.networkChanged(listOf(ap))
            await { session.snapshot.phase == ConnectionPhase.STREAMING && session.snapshot.route?.local == ap }
            assertTrue(first.closes.get() > 0)
            session.sendAudio(ByteArray(960), 1, true)
            assertEquals(1, first.sent.get())
            assertEquals(1, connections.last().sent.get())
            session.disconnect()
            session.sendAudio(ByteArray(960), 2, false)
            await { session.snapshot.phase == ConnectionPhase.STOPPED }
            assertEquals(1, connections.last().sent.get())
        } finally { session.close() }
    }

    @Test fun lateOldAttemptCannotReplaceNewSelection() {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val old = Fake {
            entered.countDown()
            while (release.count > 0) {
                try { release.await() } catch (_: InterruptedException) { }
            }
        }
        val newest = Fake()
        val session = SessionCoordinator({ route, _, _ -> if (route.remote.endsWith(".1")) old else newest }, {})
        try {
            session.networkChanged(listOf(wifi))
            session.selectDevice(null, listOf("192.168.0.1"))
            assertTrue(entered.await(2, TimeUnit.SECONDS))
            session.selectDevice(null, listOf("192.168.0.2"))
            await { session.snapshot.phase == ConnectionPhase.READY && session.snapshot.route?.remote == "192.168.0.2" }
            release.countDown()
            Thread.sleep(100)
            assertEquals(newest.sessionId, session.snapshot.sessionId)
            assertTrue(old.closes.get() > 0)
            assertEquals(0, newest.closes.get())
        } finally { release.countDown(); session.close() }
    }

    @Test fun explicitDisconnectCancelsScheduledRecovery() {
        val attempts = AtomicInteger()
        val failed = CountDownLatch(1)
        val session = SessionCoordinator({ _, _, _ ->
            attempts.incrementAndGet()
            Fake { throw java.io.IOException("Control refused") }
        }, { if (it.phase == ConnectionPhase.FAILED) failed.countDown() }, retryJitter = { 0 })
        try {
            session.networkChanged(listOf(wifi))
            session.selectDevice(null, listOf("192.168.0.165"))
            assertTrue(failed.await(2, TimeUnit.SECONDS))
            session.disconnect()
            await { session.snapshot.phase == ConnectionPhase.STOPPED }
            Thread.sleep(1200)
            assertEquals(1, attempts.get())
        } finally { session.close() }
    }
    @Test fun volumeWorkCoalescesToLastIntentAndCannotCrossNewSession() {
        val session=SessionCoordinator({_,_,_->Fake()},{})
        val values=java.util.Collections.synchronizedList(mutableListOf<Int>())
        try {
            session.networkChanged(listOf(wifi)); session.selectDevice(null,listOf("192.168.0.165"))
            await { session.snapshot.phase==ConnectionPhase.READY }
            repeat(100) { value-> session.latestApplication { values.add(value) } }
            await { values.isNotEmpty() }
            assertEquals(listOf(99),values.toList())
            session.latestApplication { values.add(100) }
            session.disconnect()
            await { session.snapshot.phase==ConnectionPhase.STOPPED }
            Thread.sleep(120)
            assertEquals(listOf(99),values.toList())
        } finally { session.close() }
    }

    @Test fun unacknowledgedUdpUsesTcpUntilStopThenRetriesUdp() {
        val connections = java.util.Collections.synchronizedList(mutableListOf<Fake>())
        val session = SessionCoordinator({ _, _, _ -> Fake().also {
            if (connections.isEmpty()) it.heartbeatFailure = UdpUnavailable()
            connections.add(it)
        } }, {}, retryJitter = { 0 })
        try {
            session.networkChanged(listOf(wifi)); session.selectDevice(null,listOf("192.168.0.165")); session.setSending(true)
            await { session.snapshot.phase == ConnectionPhase.STREAMING && session.snapshot.usingTcp }
            assertTrue(connections.last().tcpPreferred)
            session.setSending(false)
            await { session.snapshot.phase == ConnectionPhase.READY }
            session.setSending(true)
            await { session.snapshot.phase == ConnectionPhase.STREAMING && !session.snapshot.usingTcp }
            assertFalse(connections.last().tcpPreferred)
        } finally { session.close() }
    }
}
