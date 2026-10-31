package com.blueaudiosink.sender.net

import android.content.Context
import android.net.wifi.WifiManager
import android.os.SystemClock
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit
import java.util.concurrent.Executors

/**
 * 手机侧唯一 UDP 通道：一个 socket 绑定 45678，包办发现/控制/音频。
 *
 * - 收：PC 广播的 OFFER（type=6）、PC 发起的 PING（type=4）
 * - 回：PONG（type=5，payload 原样回填 PING 的 T1，header.timestampUs = 手机 T2）
 * - 发：HELLO / START / STOP / AUDIO（发往目标电脑的音频端口，源端口固定 45678）
 *
 * 线程约定（重要）：Android 禁止主线程做 socket 收发。
 * 因此控制包（HELLO/START/STOP）全部投递到内部单线程 controlExecutor 执行，
 * AUDIO 由采集发送线程调用；START 发出后才打开 AUDIO 闸门，保证顺序。
 */
class UdpSender(context: Context) {

    interface Listener {
        fun onOffer(device: DiscoveredDevice)
        fun onPing(t1Us: Int, receivedAtUs: Int)
        fun onRemoteVolume(percent: Int?, ready: Boolean)
        fun onError(message: String)
    }

    @Volatile
    var listener: Listener? = null

    private val appContext = context.applicationContext
    private val sendLock = Any()

    private val controlExecutor: ScheduledExecutorService =
        Executors.newSingleThreadScheduledExecutor { runnable ->
            Thread(runnable, "udp-control").apply { isDaemon = true }
        }

    @Volatile
    private var socket: DatagramSocket? = null

    @Volatile
    private var targetHost: String? = null

    @Volatile
    private var targetPort: Int = PacketCodec.DEFAULT_AUDIO_PORT

    @Volatile
    private var cachedAddress: InetAddress? = null

    @Volatile
    private var running: Boolean = false

    /** START 已发出后才允许 AUDIO。 */
    @Volatile
    private var audioEnabled: Boolean = false

    @Volatile
    private var closed: Boolean = false

    private var receiveThread: Thread? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    /** AUDIO 包序号，从 0 开始，START 时归零。 */
    private var seq: Int = 0
    private var sendFailures = 0L

    /** 复用缓冲：960 payload + 16 包头，避免每包分配。 */
    private val frameBuffer = ByteArray(PacketCodec.AUDIO_PACKET_SIZE)

    private val remoteVolume = RemoteVolumeClient(
        send = { type, request, payload ->
            synchronized(sendLock) {
                val sock = socket
                val address = resolveTarget()
                if (sock != null && address != null) {
                    val bytes = PacketCodec.build(type, request, 0, payload, payload.size)
                    try { sock.send(DatagramPacket(bytes, bytes.size, address, PacketCodec.CONTROL_PORT)) }
                    catch (e: Exception) { Log.w(TAG, "volume control send failed", e) }
                }
            }
        },
        changed = { percent, ready -> listener?.onRemoteVolume(percent, ready) },
    )

    fun setRemoteVolume(percent: Int) = remoteVolume.set(percent, SystemClock.elapsedRealtime())

    /** 绑定 45678 并开始收包。重复调用安全。 */
    fun open(): Boolean {
        if (socket != null) return true
        val sock = try {
            DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                soTimeout = 500
                bind(InetSocketAddress(PacketCodec.CONTROL_PORT))
            }
        } catch (e: Exception) {
            Log.w(TAG, "bind 45678 failed", e)
            listener?.onError("端口 45678 被占用")
            return false
        }
        socket = sock
        controlExecutor.scheduleAtFixedRate({ remoteVolume.tick(SystemClock.elapsedRealtime()) }, 0, 100, TimeUnit.MILLISECONDS)
        running = true
        acquireMulticastLock()
        receiveThread = Thread({ receiveLoop() }, "udp-recv").apply {
            isDaemon = true
            start()
        }
        return true
    }

    fun isOpen(): Boolean = socket != null

    /** 关闭 socket；先让已排队的 STOP 发完（同一 executor 保证顺序）。 */
    fun close() {
        if (closed) return
        remoteVolume.reset(false)
        closed = true
        running = false
        audioEnabled = false
        controlExecutor.execute {
            synchronized(sendLock) {
                try {
                    socket?.close()
                } catch (_: Exception) {
                }
                socket = null
            }
            releaseMulticastLock()
        }
        controlExecutor.shutdown()
    }

    /** 设定目标电脑（来自发现列表或手动输入）。不做 DNS，解析推迟到发送线程。 */
    fun setTarget(host: String, port: Int): Boolean {
        val trimmed = host.trim()
        if (trimmed.isEmpty()) return false
        val changedTarget = targetHost != trimmed
        targetHost = trimmed
        targetPort = if (port in 1..65_535) port else PacketCodec.DEFAULT_AUDIO_PORT
        cachedAddress = null
        if (changedTarget) remoteVolume.reset(true)
        return true
    }

    fun clearTarget() {
        remoteVolume.reset(false)
        targetHost = null
        targetPort = PacketCodec.DEFAULT_AUDIO_PORT
        cachedAddress = null
    }

    fun hasTarget(): Boolean = targetHost != null

    /** START：重置 seq 基线为 0，不重传；发送完成后才开启 AUDIO。 */
    fun sendStart(onSent: (() -> Unit)? = null) {
        controlExecutor.execute {
            synchronized(sendLock) {
                seq = 0
                sendControlLocked(PacketCodec.TYPE_START)
            }
            audioEnabled = true
            onSent?.invoke()
        }
    }

    fun sendStop() {
        audioEnabled = false
        controlExecutor.execute {
            synchronized(sendLock) { sendControlLocked(PacketCodec.TYPE_STOP) }
        }
    }

    fun sendHello(deviceName: String) {
        val bytes = deviceName.toByteArray(Charsets.UTF_8)
        controlExecutor.execute {
            synchronized(sendLock) {
                sendLocked(PacketCodec.TYPE_HELLO, 0, bytes, bytes.size)
            }
        }
    }

    /** 发送一个 AUDIO 包（自动递增 seq）。payload 必须恰好 960 字节。 */
    fun sendAudio(payload: ByteArray, length: Int, timestampUs: Int, discontinuity: Boolean) {
        if (!audioEnabled) return
        synchronized(sendLock) {
            val sock = socket ?: return
            val address = resolveTarget() ?: return
            val flags = if (discontinuity) PacketCodec.FLAG_DISCONTINUITY else 0
            val packetSeq = seq++ // Consume source time even if send fails.
            PacketCodec.writeHeader(frameBuffer, PacketCodec.TYPE_AUDIO, flags, packetSeq, timestampUs)
            System.arraycopy(payload, 0, frameBuffer, PacketCodec.HEADER_SIZE, length)
            try {
                sock.send(DatagramPacket(frameBuffer, PacketCodec.HEADER_SIZE + length, address, targetPort))
            } catch (e: Exception) {
                sendFailures++
                Log.w(TAG, "send audio failed count=$sendFailures", e)
                listener?.onError("发送失败：" + (e.message ?: e.javaClass.simpleName))
                return
            }
        }
    }

    private fun resolveTarget(): InetAddress? {
        val cached = cachedAddress
        if (cached != null) return cached
        val host = targetHost ?: return null
        return try {
            InetAddress.getByName(host).also { cachedAddress = it }
        } catch (e: Exception) {
            Log.w(TAG, "resolve target failed: " + host, e)
            listener?.onError("无法解析地址 " + host + "：" + (e.message ?: e.javaClass.simpleName))
            null
        }
    }

    private fun receiveLoop() {
        val buffer = ByteArray(2048)
        while (running) {
            val sock = socket ?: break
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                sock.receive(packet)
            } catch (_: SocketTimeoutException) {
                continue
            } catch (e: Exception) {
                if (running) Log.w(TAG, "receive failed", e)
                break
            }
            val length = packet.length
            if (!PacketCodec.isValid(buffer, length)) continue
            when (PacketCodec.typeOf(buffer, length)) {
                PacketCodec.TYPE_OFFER -> handleOffer(packet, buffer)
                PacketCodec.TYPE_PING -> if (isSelectedPc(packet)) handlePing(packet, buffer)
                PacketCodec.TYPE_VOLUME_STATE -> if (isSelectedPc(packet) && length == 30) {
                    remoteVolume.receive(buffer.copyOfRange(16, 30), PacketCodec.seqOf(buffer, length), SystemClock.elapsedRealtime())
                }
                else -> Unit
            }
        }
    }

    private fun isSelectedPc(packet: DatagramPacket): Boolean =
        packet.port == PacketCodec.CONTROL_PORT && packet.address?.hostAddress == targetHost

    private fun handleOffer(packet: DatagramPacket, buffer: ByteArray) {
        val length = packet.length
        val nameStart = PacketCodec.HEADER_SIZE
        val zero = PacketCodec.findZero(buffer, nameStart, length)
        val portOffset = if (zero >= 0) zero + 1 else -1
        val port = if (portOffset > 0 && portOffset + 4 <= length) {
            PacketCodec.getIntLE(buffer, portOffset)
        } else {
            PacketCodec.DEFAULT_AUDIO_PORT
        }
        val nameEnd = if (zero >= 0) zero else length
        val name = String(buffer, nameStart, nameEnd - nameStart, Charsets.UTF_8).trim()
        val ip = packet.address?.hostAddress ?: return
        val device = DiscoveredDevice(
            name = name.ifEmpty { ip },
            ip = ip,
            port = if (port in 1..65_535) port else PacketCodec.DEFAULT_AUDIO_PORT,
            lastSeenMs = SystemClock.elapsedRealtime(),
        )
        listener?.onOffer(device)
    }

    /** PC 发起的探测：payload 空，header.timestampUs = T1（PC 发送时刻）。 */
    private fun handlePing(packet: DatagramPacket, buffer: ByteArray) {
        val t1 = PacketCodec.timestampOf(buffer, packet.length)
        val receiveUs = (SystemClock.elapsedRealtimeNanos() / 1000L).toInt()
        val pong = PacketCodec.build(
            type = PacketCodec.TYPE_PONG,
            seq = 0,
            timestampUs = receiveUs,
            payload = intToBytes(t1),
            payloadLength = 4,
        )
        try {
            socket?.send(DatagramPacket(pong, pong.size, packet.address, packet.port))
        } catch (e: Exception) {
            Log.w(TAG, "send PONG failed", e)
            listener?.onError("回送 PONG 失败：" + (e.message ?: e.javaClass.simpleName))
        }
        listener?.onPing(t1, receiveUs)
    }

    private fun intToBytes(value: Int): ByteArray {
        val out = ByteArray(4)
        PacketCodec.putIntLE(out, 0, value)
        return out
    }

    private fun sendControlLocked(type: Int) {
        sendLocked(type, (SystemClock.elapsedRealtimeNanos() / 1000L).toInt(), null, 0)
    }

    private fun sendLocked(type: Int, timestampUs: Int, payload: ByteArray?, payloadLength: Int) {
        val sock = socket ?: return
        val address = resolveTarget() ?: return
        val packetBytes = PacketCodec.build(type, 0, timestampUs, payload, payloadLength)
        try {
            sock.send(DatagramPacket(packetBytes, packetBytes.size, address, targetPort))
        } catch (e: Exception) {
            Log.w(TAG, "send control " + type + " failed", e)
            listener?.onError("发送失败：" + (e.message ?: e.javaClass.simpleName))
        }
    }

    private fun acquireMulticastLock() {
        try {
            val wifi = appContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return
            multicastLock = wifi.createMulticastLock("blueaudiosink-udp").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {
            // 没有 Wi-Fi 时忽略，仍尝试直接收广播
        }
    }

    private fun releaseMulticastLock() {
        try {
            multicastLock?.release()
        } catch (_: Exception) {
        }
        multicastLock = null
    }

    private companion object {
        const val TAG = "BlueAudioSink"
    }
}