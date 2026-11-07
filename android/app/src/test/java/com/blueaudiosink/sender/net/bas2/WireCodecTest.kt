package com.blueaudiosink.sender.net.bas2

import java.io.ByteArrayInputStream
import java.io.EOFException
import org.junit.Assert.*
import org.junit.Test

class WireCodecTest {
    private val id = ByteArray(16) { (it + 1).toByte() }
    private val frame = MediaFrame(1, 1, id, -1L, -1, 0x87654321.toInt(), ByteArray(960))

    @Test fun matchesWindowsHeaderVectorAndUnsignedBitPatterns() {
        val bytes = WireCodec.encodeMedia(frame)
        assertEquals(1000, bytes.size)
        assertEquals("42415332010128000102030405060708090a0b0c0d0e0f10ffffffffffffffffffffffff21436587",
            bytes.take(40).joinToString("") { "%02x".format(it.toInt() and 255) })
        val decoded = WireCodec.decodeMedia(bytes)
        assertEquals(-1L, decoded.streamEpoch)
        assertEquals(-1, decoded.sequence)
        assertArrayEquals(id, decoded.sessionId)
    }

    @Test fun rejectsForeignLengthsFlagsAndMissingSession() {
        val encoded = WireCodec.encodeMedia(frame)
        assertThrows(IllegalArgumentException::class.java) { WireCodec.decodeMedia(encoded.copyOf(999)) }
        assertThrows(IllegalArgumentException::class.java) {
            WireCodec.decodeMedia(encoded.copyOf().also { it[5] = 2 })
        }
        assertThrows(IllegalArgumentException::class.java) { WireCodec.encodeMedia(frame.copy(streamEpoch = 0)) }
        assertThrows(IllegalArgumentException::class.java) { WireCodec.encodeMedia(frame.copy(sessionId = ByteArray(16))) }
    }

    @Test fun probesUseSeparateShape() {
        val probe = frame.copy(type = 2, flags = 0, streamEpoch = 0, payload = id)
        assertArrayEquals(id, WireCodec.decodeMedia(WireCodec.encodeMedia(probe)).payload)
    }

    @Test fun readsFragmentedAndCoalescedControlFrames() {
        val json = """{"type":"hello","protocol":2}""".toByteArray()
        val encoded = WireCodec.encodeControlPayload(json)
        val input = object : ByteArrayInputStream(encoded + encoded) {
            override fun read(bytes: ByteArray, offset: Int, length: Int) =
                super.read(bytes, offset, minOf(length, 1))
        }
        assertArrayEquals(json, WireCodec.readControlPayload(input))
        assertArrayEquals(json, WireCodec.readControlPayload(input))
        assertThrows(EOFException::class.java) {
            WireCodec.readControlPayload(ByteArrayInputStream(encoded.copyOf(encoded.size - 1)))
        }
        assertThrows(IllegalArgumentException::class.java) {
            WireCodec.readControlPayload(ByteArrayInputStream(byteArrayOf(-1, -1, -1, 127)))
        }
    }
}
