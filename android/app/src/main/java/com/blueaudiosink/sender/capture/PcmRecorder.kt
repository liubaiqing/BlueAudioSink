package com.blueaudiosink.sender.capture

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.AudioTimestamp
import android.media.projection.MediaProjection
import android.os.Process
import android.os.SystemClock
import android.util.Log
import com.blueaudiosink.sender.net.PacketCodec
import java.util.concurrent.locks.LockSupport
import kotlin.math.abs

/**
 * 采集链：MediaProjection → AudioPlaybackCaptureConfiguration → AudioRecord。
 *
 * 采集线程按 240 帧（960 字节）读，写入一个按包编号的环形缓冲；
 * 发送线程按 5 ms 节奏发送真实采集包；环空时等待采集，不伪造静音。
 * 真正的环溢出丢包会标记内容不连续，PC 缓冲吸收短暂调度抖动。
 *
 * timestampUs 取该包首帧的采样时刻（单调时钟微秒低 32 位），
 * 由采集线程在读到整包时按「读取完成时刻 − 5 ms」估算后随包携带。
 */
class PcmRecorder(
    private val projection: MediaProjection?,
    private val tier: BufferTier,
    private val onPacket: (buffer: ByteArray, length: Int, timestampUs: Int, discontinuity: Boolean) -> Unit,
    private val onError: (code: Int) -> Unit,
) {

    companion object {
        const val ERROR_BUILD = 1
        const val ERROR_START = 2
        const val ERROR_READ = 3
        const val ERROR_PERMISSION = 4

        private const val PACKET_INTERVAL_US = PacketCodec.PACKET_INTERVAL_US
        private const val PACKET_INTERVAL_NS = PACKET_INTERVAL_US * 1_000L

        /** 预填充最多等待：避免无声源时永远不发第一个包。 */
        private const val PREFILL_TIMEOUT_NS = 300_000_000L

        /** 发送线程严重落后（>200 ms）时重新对齐节拍，避免补发风暴。 */
        private const val RESYNC_THRESHOLD_NS = 200_000_000L
        private const val TAG = "BlueAudioSink"
    }

    private val packetBytes = PacketCodec.AUDIO_PAYLOAD_SIZE
    private val capacity = (tier.prefillPackets * 3).coerceAtLeast(8)

    private val ring = ByteArray(capacity * packetBytes)
    private val ringTimestamps = LongArray(capacity)
    private var writeIndex = 0
    private var readIndex = 0
    private var fill = 0
    private val lock = Object()

    private val outBuffer = ByteArray(packetBytes)
    private var lastSentTsUs = 0L
    private var sourceWaits = 0L
    private var sourceOverflows = 0L
    private var shortReads = 0L
    private var sentPackets = 0L

    @Volatile
    private var overflowGap = false

    /** 用于把「帧号 → 单调时钟」锚定到音频硬件时间戳。 */
    private val audioTimestamp = AudioTimestamp()
    private var framesRead: Long = 0

    private var record: AudioRecord? = null
    private var captureThread: Thread? = null
    private var sendThread: Thread? = null

    @Volatile
    private var running = false

    fun start(): Boolean {
        if (running) return true

        val captureConfig = try {
            AudioPlaybackCaptureConfiguration.Builder(requireNotNull(projection))
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                .build()
        } catch (e: Exception) {
            Log.w(TAG, "AudioPlaybackCaptureConfiguration failed", e)
            onError(ERROR_BUILD)
            return false
        }

        val format = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(PacketCodec.SAMPLE_RATE)
            .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
            .build()

        val minBuffer = AudioRecord.getMinBufferSize(
            PacketCodec.SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_STEREO,
            AudioFormat.ENCODING_PCM_16BIT,
        )
        // AudioRecord 内部缓冲取 2 倍 minBufferSize（至少 4 包），
        // 对外仍按 240 帧切包，不因缓冲大就一次发大包。
        val bufferBytes = if (minBuffer > 0) {
            maxOf(minBuffer * 2, packetBytes * 4)
        } else {
            packetBytes * 4
        }

        val recorder = try {
            AudioRecord.Builder()
                .setAudioFormat(format)
                .setBufferSizeInBytes(bufferBytes)
                .setAudioPlaybackCaptureConfig(requireNotNull(captureConfig))
                .build()
        } catch (e: SecurityException) {
            Log.w(TAG, "AudioRecord permission denied", e)
            onError(ERROR_PERMISSION)
            return false
        } catch (e: Exception) {
            Log.w(TAG, "AudioRecord build failed", e)
            onError(ERROR_BUILD)
            return false
        }

        if (recorder.state != AudioRecord.STATE_INITIALIZED) {
            Log.w(TAG, "AudioRecord not initialized, state=" + recorder.state)
            recorder.release()
            onError(ERROR_BUILD)
            return false
        }
        try {
            recorder.startRecording()
        } catch (e: Exception) {
            Log.w(TAG, "startRecording failed", e)
            recorder.release()
            onError(ERROR_START)
            return false
        }
        if (recorder.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
            recorder.release()
            onError(ERROR_START)
            return false
        }

        record = recorder
        running = true
        captureThread = Thread({ captureLoop(recorder) }, "pcm-capture").apply {
            isDaemon = true
            start()
        }
        sendThread = Thread({ sendLoop() }, "pcm-send").apply {
            isDaemon = true
            start()
        }
        return true
    }

    fun stop() {
        running = false
        captureThread?.interrupt()
        captureThread = null
        sendThread?.interrupt()
        sendThread = null
        record?.let {
            try {
                it.stop()
            } catch (_: Exception) {
            }
            try {
                it.release()
            } catch (_: Exception) {
            }
        }
        record = null
    }

    // ---------------------------------------------------------------- 采集

    private fun captureLoop(recorder: AudioRecord) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        val chunk = ByteArray(packetBytes)
        var accumulated = 0
        while (running) {
            val requested = packetBytes - accumulated
            val read = try {
                recorder.read(chunk, accumulated, requested, AudioRecord.READ_BLOCKING)
            } catch (e: Exception) {
                -1
            }
            if (!running) break
            if (read < 0) {
                onError(ERROR_READ)
                overflowGap = true
                try {
                    Thread.sleep(10)
                } catch (_: InterruptedException) {
                    break
                }
                continue
            }
            if (read == 0) continue
            if (read < requested) shortReads++
            accumulated += read
            framesRead += read.toLong() / (PacketCodec.CHANNEL_COUNT * PacketCodec.BYTES_PER_SAMPLE)
            // A short read is not missing audio. Accumulate the rest without inserting zeros.
            if (accumulated < packetBytes) continue
            accumulated = 0

            // First captured frame, in the same BOOTTIME basis used for packet scheduling.
            val captureTsUs = firstFrameCaptureUs(recorder, framesRead - PacketCodec.FRAMES_PER_PACKET)
            synchronized(lock) {
                if (fill == capacity) {
                    // 环满：丢最旧一包保低延迟，并标记内容不连续
                    readIndex = (readIndex + 1) % capacity
                    fill--
                    sourceOverflows++
                    overflowGap = true
                }
                System.arraycopy(chunk, 0, ring, writeIndex * packetBytes, packetBytes)
                ringTimestamps[writeIndex] = captureTsUs
                writeIndex = (writeIndex + 1) % capacity
                fill++
                lock.notifyAll()
            }
        }
    }

    /**
     * 该包第 0 帧的采样时刻（单调时钟微秒）。
     *
     * 优先用 AudioRecord.getTimestamp 提供的「帧号 → 单调时钟」锚点反推：
     * 首帧时间 = 锚点时间 − (锚点帧号 − 首帧帧号) / 48000。
     * 时间戳不可用（返回 false 或明显不合理）时退回「读到整包时刻 − 5 ms」。
     */
    private fun firstFrameCaptureUs(recorder: AudioRecord, firstFrameIndex: Long): Long {
        val nowUs = SystemClock.elapsedRealtimeNanos() / 1000L
        return try {
            if (recorder.getTimestamp(audioTimestamp, AudioTimestamp.TIMEBASE_BOOTTIME) ==
                AudioRecord.SUCCESS
            ) {
                val deltaFrames = audioTimestamp.framePosition - firstFrameIndex
                val candidateUs = audioTimestamp.nanoTime / 1000L -
                    deltaFrames * 1_000_000L / PacketCodec.SAMPLE_RATE
                if (abs(candidateUs - nowUs) <= 200_000L) candidateUs else nowUs - PACKET_INTERVAL_US
            } else {
                nowUs - PACKET_INTERVAL_US
            }
        } catch (e: Exception) {
            nowUs - PACKET_INTERVAL_US
        }
    }

    /**
     * 时间轴平滑。
     *
     * 以「上一包 + 5 ms」为基准，用硬件首帧时间做逐包最多 1 ms 的缓慢校正：
     * AudioRecord.getTimestamp 的锚点偶发数十毫秒跳变，直接把原始值当时间戳会让
     * PC 端的偏移/抖动估计失真，所以这里只允许平滑跟随。
     * 偏差超过 100 ms 视为锚点异常，本包退回均匀时间轴。
     */
    private fun smoothTimestampUs(hasData: Boolean, packetTsUs: Long): Long {
        if (lastSentTsUs == 0L) {
            return if (hasData && packetTsUs > 0) {
                packetTsUs
            } else {
                SystemClock.elapsedRealtimeNanos() / 1000L - PACKET_INTERVAL_US
            }
        }
        val projected = lastSentTsUs + PACKET_INTERVAL_US
        if (!hasData) return projected
        val deviation = packetTsUs - projected
        if (deviation > 100_000L || deviation < -100_000L) return projected
        return projected + deviation.coerceIn(-1_000L, 1_000L)
    }

    // ---------------------------------------------------------------- 发送

    private fun sendLoop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)

        // 预填充到档位深度，最多等 300 ms
        val prefillDeadline = SystemClock.elapsedRealtimeNanos() + PREFILL_TIMEOUT_NS
        while (running) {
            val enough = synchronized(lock) { fill >= tier.prefillPackets }
            if (enough) break
            if (SystemClock.elapsedRealtimeNanos() >= prefillDeadline) break
            try {
                Thread.sleep(2)
            } catch (_: InterruptedException) {
                return
            }
        }

        var nextNs = SystemClock.elapsedRealtimeNanos()
        while (running) {
            while (running) {
                val wait = nextNs - SystemClock.elapsedRealtimeNanos()
                if (wait <= 0) break
                LockSupport.parkNanos(wait)
            }
            if (!running) break

            var hasData = false
            var packetTsUs = 0L
            synchronized(lock) {
                if (fill > 0) {
                    System.arraycopy(ring, readIndex * packetBytes, outBuffer, 0, packetBytes)
                    packetTsUs = ringTimestamps[readIndex]
                    readIndex = (readIndex + 1) % capacity
                    fill--
                    hasData = true
                }
            }

            if (!hasData) {
                // An empty application ring is scheduling jitter, not missing PCM.
                // Wait for real captured audio instead of inserting zeros into music.
                sourceWaits++
                try {
                    synchronized(lock) { if (running && fill == 0) lock.wait(5) }
                } catch (_: InterruptedException) { break }
                nextNs = SystemClock.elapsedRealtimeNanos()
                continue
            }
            var discontinuity = false
            if (overflowGap) {
                overflowGap = false
                discontinuity = true
            }

            val outTsUs = smoothTimestampUs(hasData, packetTsUs)
            lastSentTsUs = outTsUs

            onPacket(outBuffer, packetBytes, (outTsUs and 0xFFFFFFFFL).toInt(), discontinuity)

            sentPackets++
            if (sentPackets % 1000L == 0L) {
                val queued = synchronized(lock) { fill }
                Log.i(TAG, "Capture.Stats packets=$sentPackets ringPackets=$queued " +
                    "sourceWaits=$sourceWaits sourceOverflows=$sourceOverflows shortReads=$shortReads")
            }
            nextNs += PACKET_INTERVAL_NS
            val nowNs = SystemClock.elapsedRealtimeNanos()
            if (nowNs - nextNs > RESYNC_THRESHOLD_NS) nextNs = nowNs
        }
    }
}
