package com.blueaudiosink.sender.net

/**
 * 传输协议 v1 编解码（唯一依据：docs/02-protocol.md）。
 *
 * 统一包头 16 字节，全部小端：
 * | 偏移 | 长度 | 字段        | 类型 |
 * | 0    | 4    | magic "BAS1"| bytes|
 * | 4    | 1    | type        | u8   |
 * | 5    | 1    | flags       | u8   |
 * | 6    | 2    | reserved    | u16  |
 * | 8    | 4    | seq         | u32  |
 * | 12   | 4    | timestampUs | u32  |
 *
 * 音频包：5 ms / 240 帧 / 960 字节 payload，总长 976 字节（IP 层 1004，不分片）。
 */
object PacketCodec {

    const val HEADER_SIZE = 16

    // ---- type ----
    const val TYPE_AUDIO = 1
    const val TYPE_START = 2
    const val TYPE_STOP = 3
    const val TYPE_PING = 4
    const val TYPE_PONG = 5
    const val TYPE_OFFER = 6
    const val TYPE_HELLO = 7
    const val TYPE_VOLUME_GET = 8
    const val TYPE_VOLUME_SET = 9
    const val TYPE_VOLUME_STATE = 10

    // ---- flags ----
    /** 仅手机置位，表示「内容不连续」（补静音/丢帧），与网络丢包无关。 */
    const val FLAG_DISCONTINUITY = 0x01

    // ---- 端口 ----
    /** 手机唯一 UDP socket 的本地端口：发现、控制、音频同源。 */
    const val CONTROL_PORT = 45_678
    /** 音频默认目的端口（OFFER 未给出时使用）。 */
    const val DEFAULT_AUDIO_PORT = 45_679

    // ---- 音频格式（协议第 3 节）----
    const val SAMPLE_RATE = 48_000
    const val CHANNEL_COUNT = 2
    const val BYTES_PER_SAMPLE = 2
    const val FRAMES_PER_PACKET = 240
    const val AUDIO_PAYLOAD_SIZE = FRAMES_PER_PACKET * CHANNEL_COUNT * BYTES_PER_SAMPLE // 960
    const val AUDIO_PACKET_SIZE = HEADER_SIZE + AUDIO_PAYLOAD_SIZE // 976
    const val PACKETS_PER_SECOND = 200
    const val PACKET_INTERVAL_US = 5_000L

    private const val MAGIC_0 = 0x42 // 'B'
    private const val MAGIC_1 = 0x41 // 'A'
    private const val MAGIC_2 = 0x53 // 'S'
    private const val MAGIC_3 = 0x31 // '1'

    /** 写入 16 字节包头到 dst 的 0..15。 */
    fun writeHeader(dst: ByteArray, type: Int, flags: Int, seq: Int, timestampUs: Int) {
        dst[0] = MAGIC_0.toByte()
        dst[1] = MAGIC_1.toByte()
        dst[2] = MAGIC_2.toByte()
        dst[3] = MAGIC_3.toByte()
        dst[4] = type.toByte()
        dst[5] = flags.toByte()
        dst[6] = 0
        dst[7] = 0
        putIntLE(dst, 8, seq)
        putIntLE(dst, 12, timestampUs)
    }

    /** 生成一个完整控制包（payload 可为 null）。 */
    fun build(type: Int, seq: Int, timestampUs: Int, payload: ByteArray?, payloadLength: Int = 0): ByteArray {
        val len = payloadLength.coerceAtLeast(0)
        val out = ByteArray(HEADER_SIZE + len)
        writeHeader(out, type, 0, seq, timestampUs)
        if (payload != null && len > 0) {
            System.arraycopy(payload, 0, out, HEADER_SIZE, len)
        }
        return out
    }

    fun isValid(buffer: ByteArray, length: Int): Boolean =
        length >= HEADER_SIZE &&
            buffer[0] == MAGIC_0.toByte() &&
            buffer[1] == MAGIC_1.toByte() &&
            buffer[2] == MAGIC_2.toByte() &&
            buffer[3] == MAGIC_3.toByte()

    fun typeOf(buffer: ByteArray, length: Int): Int =
        if (isValid(buffer, length)) buffer[4].toInt() and 0xFF else -1

    fun flagsOf(buffer: ByteArray, length: Int): Int =
        if (isValid(buffer, length)) buffer[5].toInt() and 0xFF else 0

    fun timestampOf(buffer: ByteArray, length: Int): Int =
        if (length >= 16) getIntLE(buffer, 12) else 0

    fun seqOf(buffer: ByteArray, length: Int): Int =
        if (length >= 12) getIntLE(buffer, 8) else 0

    fun findZero(buffer: ByteArray, from: Int, to: Int): Int {
        var i = from
        while (i < to) {
            if (buffer[i] == 0.toByte()) return i
            i++
        }
        return -1
    }

    fun putIntLE(dst: ByteArray, offset: Int, value: Int) {
        dst[offset] = (value and 0xFF).toByte()
        dst[offset + 1] = ((value ushr 8) and 0xFF).toByte()
        dst[offset + 2] = ((value ushr 16) and 0xFF).toByte()
        dst[offset + 3] = ((value ushr 24) and 0xFF).toByte()
    }

    fun getIntLE(src: ByteArray, offset: Int): Int =
        (src[offset].toInt() and 0xFF) or
            ((src[offset + 1].toInt() and 0xFF) shl 8) or
            ((src[offset + 2].toInt() and 0xFF) shl 16) or
            ((src[offset + 3].toInt() and 0xFF) shl 24)
}
