package com.blueaudiosink.sender.net.bas2

import java.io.DataInputStream
import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

internal data class MediaFrame(
    val type: Int, val flags: Int, val sessionId: ByteArray, val streamEpoch: Long,
    val sequence: Int, val timestampUs: Int, val payload: ByteArray,
)

internal object WireCodec {
    const val HEADER_SIZE = 40
    const val MAX_CONTROL_BYTES = 16384

    fun encodeMedia(frame: MediaFrame): ByteArray {
        validate(frame)
        return ByteBuffer.allocate(HEADER_SIZE + frame.payload.size).order(ByteOrder.LITTLE_ENDIAN)
            .putInt(0x32534142).put(frame.type.toByte()).put(frame.flags.toByte()).putShort(HEADER_SIZE.toShort())
            .put(frame.sessionId).putLong(frame.streamEpoch).putInt(frame.sequence).putInt(frame.timestampUs)
            .put(frame.payload).array()
    }

    fun decodeMedia(bytes: ByteArray): MediaFrame {
        require(bytes.size in HEADER_SIZE..1000) { "Invalid BAS2 media size" }
        val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        require(buffer.int == 0x32534142) { "Invalid BAS2 magic" }
        val type = buffer.get().toInt() and 255
        val flags = buffer.get().toInt() and 255
        require(buffer.short.toInt() == HEADER_SIZE) { "Invalid BAS2 header size" }
        val session = ByteArray(16).also { buffer.get(it) }
        val epoch = buffer.long
        val sequence = buffer.int
        val timestamp = buffer.int
        val payload = ByteArray(buffer.remaining()).also { buffer.get(it) }
        return MediaFrame(type, flags, session, epoch, sequence, timestamp, payload).also(::validate)
    }

    private fun validate(frame: MediaFrame) {
        require(frame.sessionId.size == 16 && frame.sessionId.any { it != 0.toByte() }) { "Invalid BAS2 session" }
        val audio = frame.type == 1 && frame.streamEpoch != 0L && frame.payload.size == 960 && frame.flags in 0..1
        val probe = frame.type in 2..3 && frame.streamEpoch == 0L && frame.flags == 0 && frame.payload.size == 16
        require(audio || probe) { "Invalid BAS2 media fields" }
    }

    // JSON schema validation is performed by the session parser after bounded framing.
    fun encodeControlPayload(json: ByteArray): ByteArray {
        require(json.size in 1..MAX_CONTROL_BYTES) { "Invalid BAS2 control size" }
        return ByteBuffer.allocate(4 + json.size).order(ByteOrder.LITTLE_ENDIAN).putInt(json.size).put(json).array()
    }

    fun readControlPayload(input: InputStream): ByteArray {
        val reader = DataInputStream(input)
        val length = Integer.reverseBytes(reader.readInt())
        require(length in 1..MAX_CONTROL_BYTES) { "Invalid BAS2 control size" }
        return ByteArray(length).also { reader.readFully(it) }
    }
}
