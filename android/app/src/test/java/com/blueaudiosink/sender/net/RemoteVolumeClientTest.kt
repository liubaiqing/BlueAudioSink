package com.blueaudiosink.sender.net

import org.junit.Assert.*
import org.junit.Test

class RemoteVolumeClientTest {
    private data class Sent(val type: Int, val seq: Int, val data: ByteArray)
    private class Rig {
        val sent = mutableListOf<Sent>()
        var value: Int? = null
        var ready = false
        val client = RemoteVolumeClient({ type, seq, data -> sent += Sent(type, seq, data) },
            { value, ready -> this.value = value; this.ready = ready })
        fun connect() { client.reset(true); client.tick(0) }
        fun state(value: Int, revision: Int = 2, ack: Int = 0, now: Long = 10, token: Int = PacketCodec.getIntLE(sent.first().data, 0)) {
            val data = ByteArray(14)
            PacketCodec.putIntLE(data, 0, token); PacketCodec.putIntLE(data, 4, 42)
            PacketCodec.putIntLE(data, 8, revision); data[12] = value.toByte(); data[13] = (if (ack != 0) 2 else 0).toByte()
            client.receive(data, ack, now)
        }
        fun sets() = sent.filter { it.type == 9 }
    }
    @Test fun firstConnectReadsWithoutRestoringOldVolume() {
        val r = Rig(); r.connect(); r.client.set(90, 1); r.client.tick(100)
        assertTrue(r.sets().isEmpty()); assertFalse(r.ready)
        r.state(25); assertEquals(25, r.value); assertTrue(r.ready)
    }
    @Test fun dragCoalescesAndEventuallySendsFinalValue() {
        val r = Rig(); r.connect(); r.state(25)
        r.client.set(30, 20); r.client.tick(100)
        r.client.set(40, 110); r.client.set(41, 120); r.client.tick(150)
        assertEquals(1, r.sets().size)
        r.state(30, 3, r.sets().first().seq, 160); r.client.tick(200)
        assertEquals(2, r.sets().size); assertEquals(41, r.sets().last().data[12].toInt())
        r.state(41, 4, r.sets().last().seq, 210); assertEquals(41, r.value)
    }
    @Test fun lostAckRetriesSameRequestWithoutNewRevision() {
        val r = Rig(); r.connect(); r.state(25); r.client.set(30, 20); r.client.tick(100); r.client.tick(400)
        assertEquals(2, r.sets().size); assertEquals(r.sets()[0].seq, r.sets()[1].seq)
        assertArrayEquals(r.sets()[0].data, r.sets()[1].data)
    }
    @Test fun pcMuteCancelsInFlightAndUnsentDrag() {
        val r = Rig(); r.connect(); r.state(25); r.client.set(80, 20); r.client.tick(100); r.client.set(90, 110)
        r.state(0, 3, now = 120); r.client.tick(500)
        assertEquals(0, r.value); assertEquals(1, r.sets().size)
        r.state(25, 2, now = 510); assertEquals(0, r.value)
    }
    @Test fun unsolicitedMuteMatchingPendingMuteCannotConfirmQueuedRaise() {
        val r = Rig(); r.connect(); r.state(25); r.client.set(0, 20); r.client.tick(100); r.client.set(90, 110)
        r.state(0, 3, now = 120); r.client.tick(500)
        assertEquals(0, r.value); assertEquals(1, r.sets().size)
    }
    @Test fun reconnectAndStateTimeoutNeverReplaySliderIntent() {
        val r = Rig(); r.connect(); r.state(25); r.client.set(90, 20)
        val oldToken = PacketCodec.getIntLE(r.sent.first().data, 0)
        r.client.reset(true); r.client.tick(100)
        r.state(90, token = oldToken); assertFalse(r.ready); assertTrue(r.sets().isEmpty())
        val token = PacketCodec.getIntLE(r.sent.last().data, 0)
        r.state(0, now = 110, token = token); r.client.set(50, 120); r.client.tick(4000)
        assertFalse(r.ready); assertTrue(r.sets().isEmpty())
    }
}
