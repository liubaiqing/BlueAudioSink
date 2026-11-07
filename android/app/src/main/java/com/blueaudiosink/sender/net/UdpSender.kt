package com.blueaudiosink.sender.net

import com.blueaudiosink.sender.core.Diagnostics

import android.content.Context
import android.net.wifi.WifiManager
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkRequest
import android.net.NetworkCapabilities
import android.net.LinkProperties
import android.os.SystemClock
import com.blueaudiosink.sender.core.SendDuration
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

    interface Listener : SenderListener

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

    private val sendDuration = SendDuration { SystemClock.elapsedRealtime() }
    fun resetSendDuration() = sendDuration.reset()
    fun sendingSeconds(): Long = sendDuration.milliseconds() / 1000

    /** START 已发出后才允许 AUDIO。 */
    @Volatile
    private var audioEnabled: Boolean = false
        set(value) {
            sendDuration.setActive(value)
            field = value
        }

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

    private var identityVerified = false
    private var generation = 0L
    private var pingDeadline = 0L
    private var nextHello = 0L
    private var helloName = "Android 手机"
    private var networkSignature = DiscoveryTargets.signature()
    private var nextNetworkCheck = 0L
    private var nextOfferDiagnostic = 0L
    private var nextSendDiagnostic = 0L
    private val connectivity=appContext.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
    private var boundNetwork: Network? = null
    private var physicalBindingBlocked = false
    private var vpnSignature = ""
    private var routeErrorReported = false
    private val networkCallback=object: ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = changed()
        override fun onLost(network: Network) = changed()
        override fun onLinkPropertiesChanged(network: Network, properties: LinkProperties) = changed()
        private fun changed() { if(!closed) runCatching {
            controlExecutor.execute { synchronized(sendLock) { if(!closed) {
                checkNetwork()
            } } }
        } }
    }
    private val resolver = IdentityResolver(
        send = { payload, host ->
            val destinations = if (host == null) DiscoveryTargets.addresses() else {
                val initial = payload.copyOfRange(16,48).all { it == 0.toByte() }
                if (initial || DiscoveryTargets.isLocal(host)) setOf(host) else emptySet()
            }
            val bytes = PacketCodec.build(PacketCodec.TYPE_IDENTITY_QUERY,0,0,payload,payload.size)
            destinations.forEach { destination -> try {
                val address = InetAddress.getByName(destination)
                socket?.send(DatagramPacket(bytes,bytes.size,address,PacketCodec.CONTROL_PORT))
            } catch (_: Exception) {} }
        },
        verified = { device ->
            setTarget(device.ip,device.port); identityVerified = true
            pingDeadline=SystemClock.elapsedRealtime()+10000;nextHello=0
            listener?.onResolved(device)
        },
        status = { active, error ->
            val message = if (error != null && physicalBindingBlocked)
                "VPN 禁止局域网直连，请将蓝音频加入 VPN 绕过列表或开启局域网放行" else error
            listener?.onResolving(active, message)
        },
    )
    fun selectDevice(id: String?, addresses: List<String>) = synchronized(sendLock) {
        if (audioEnabled && identityVerified) {
            val address=cachedAddress;val port=targetPort;val oldSocket=socket
            if(address!=null) controlExecutor.execute { runCatching {
                val stop=PacketCodec.build(PacketCodec.TYPE_STOP,0,0,null,0)
                oldSocket?.send(DatagramPacket(stop,stop.size,address,port))
            } }
        }
        invalidateIdentity()
        resolver.select(id,addresses,SystemClock.elapsedRealtime())
    }
    fun suspendIdentity() = synchronized(sendLock) {
        invalidateIdentity(); resolver.restart(SystemClock.elapsedRealtime())
    }
    private fun invalidateIdentity() {
        diagnosticsBatch = null
        generation++; identityVerified=false; audioEnabled=false
        targetHost=null; cachedAddress=null; pingDeadline=0; remoteVolume.reset(false)
    }
    private fun checkNetwork(force: Boolean = false) {
        val network = LanNetwork.select(connectivity, boundNetwork)
        val currentVpn = LanNetwork.vpnSignature(connectivity)
        val signature = DiscoveryTargets.signature()
        val routeChanged = network != boundNetwork || currentVpn != vpnSignature || signature != networkSignature
        if (routeChanged && !replaceRoute(network)) return
        vpnSignature = currentVpn
        if (force || routeChanged || signature != networkSignature) {
            Diagnostics.record("Wifi.NetworkChanged", DiscoveryTargets.lans().size.toLong())
            networkSignature=signature; invalidateIdentity(); resolver.restart(SystemClock.elapsedRealtime())
        }
    }

    private fun createSocket(network: Network?): DatagramSocket {
        val candidate = DatagramSocket(null)
        try {
            candidate.reuseAddress = true
            candidate.broadcast = true
            candidate.soTimeout = 500
            if (network != null) {
                try {
                    network.bindSocket(candidate)
                    physicalBindingBlocked = false
                } catch (denied: java.net.SocketException) {
                    candidate.close()
                    physicalBindingBlocked = true
                    Diagnostics.record("Wifi.VpnRouteRestricted")
                    Log.w(TAG, "VPN or system policy denied physical LAN binding; retaining system routing", denied)
                    return createSocket(null)
                } catch (denied: SecurityException) {
                    candidate.close()
                    physicalBindingBlocked = true
                    Diagnostics.record("Wifi.VpnRouteRestricted")
                    return createSocket(null)
                }
            }
            candidate.bind(InetSocketAddress(PacketCodec.CONTROL_PORT))
            return candidate
        } catch (error: Exception) {
            candidate.close()
            throw error
        }
    }

    private fun replaceRoute(network: Network?): Boolean {
        try {
            val replacement = createSocket(network)
            val previous = socket
            socket = replacement
            boundNetwork = network
            previous?.close()
            routeErrorReported = false
            Diagnostics.record("Wifi.LanRoute", if (network == null || physicalBindingBlocked) 0 else 1)
            return true
        } catch (error: Exception) {
            invalidateIdentity()
            if (!routeErrorReported) {
                Diagnostics.record("Wifi.LanRouteFailed")
                listener?.onError("局域网通道不可用，请检查 VPN 是否允许局域网或将蓝音频加入绕过列表")
                routeErrorReported = true
            }
            Log.w(TAG, "Could not bind LAN route", error)
            return false
        }
    }
    private fun controlTick() = synchronized(sendLock) {
        if (closed) return@synchronized
        try {
            val now=SystemClock.elapsedRealtime()
            if(now>=nextNetworkCheck) { nextNetworkCheck=now+1000; checkNetwork() }
            resolver.tick(now)
            if(identityVerified && pingDeadline>0) {
                if(now>=pingDeadline) {
                    Diagnostics.record("Wifi.FirstPingTimeout")
                    invalidateIdentity()
                    resolver.fail(if (physicalBindingBlocked)
                        "VPN 禁止局域网直连，请将蓝音频加入 VPN 绕过列表或开启局域网放行"
                        else "电脑身份已确认，但连接无回应，请检查电脑防火墙放行")
                    return@synchronized
                }
                if(now>=nextHello) { nextHello=now+1000;val name=helloName.toByteArray(Charsets.UTF_8);sendLocked(PacketCodec.TYPE_HELLO,0,name,name.size) }
            }
            if(identityVerified) remoteVolume.tick(now)
        } catch(e: Exception) { Log.w(TAG,"control tick failed",e) }
    }
    fun setRemoteVolume(percent: Int) = synchronized(sendLock) {
        if(identityVerified) remoteVolume.set(percent, SystemClock.elapsedRealtime())
    }

    /** 绑定 45678 并开始收包。重复调用安全。 */
    fun open(): Boolean {
        if (socket != null) return true
        val sock = try {
            val network = LanNetwork.select(connectivity, null)
            createSocket(network).also {
                boundNetwork = network
                vpnSignature = LanNetwork.vpnSignature(connectivity)
            }
        } catch (e: Exception) {
            Log.w(TAG, "bind 45678 failed", e)
            Diagnostics.record("Wifi.BindFailed", PacketCodec.CONTROL_PORT.toLong())
            listener?.onError("无法建立局域网通道，请检查端口占用或 VPN 的局域网放行设置")
            return false
        }
        socket = sock
        Diagnostics.record("Wifi.Bound", PacketCodec.CONTROL_PORT.toLong())
        Diagnostics.record("Wifi.LanRoute", if (boundNetwork == null || physicalBindingBlocked) 0 else 1)
        controlExecutor.scheduleAtFixedRate({ controlTick() }, 0, 100, TimeUnit.MILLISECONDS)
        running = true
        acquireMulticastLock()
        runCatching {
            connectivity?.registerNetworkCallback(
                NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
                    .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                    .addTransportType(NetworkCapabilities.TRANSPORT_ETHERNET).build(),
                networkCallback,
            )
        }
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
        synchronized(sendLock) { invalidateIdentity(); resolver.clear() }
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
        runCatching { connectivity?.unregisterNetworkCallback(networkCallback) }
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

    fun clearTarget() = synchronized(sendLock) {
        invalidateIdentity(); resolver.clear()
        remoteVolume.reset(false)
        targetHost = null
        targetPort = PacketCodec.DEFAULT_AUDIO_PORT
        cachedAddress = null
    }

    fun hasTarget(): Boolean = targetHost != null

    /** START：重置 seq 基线为 0，不重传；发送完成后才开启 AUDIO。 */
    fun sendStart(onSent: (() -> Unit)? = null) {
        val requestedGeneration = synchronized(sendLock) { generation }
        controlExecutor.execute {
            synchronized(sendLock) {
                if (!identityVerified || requestedGeneration != generation) return@execute
                seq = 0
                sendControlLocked(PacketCodec.TYPE_START)
                audioEnabled = true
            }
            onSent?.invoke()
        }
    }

    fun sendStop() {
        val requestedGeneration=synchronized(sendLock) { generation }
        audioEnabled = false
        controlExecutor.execute {
            synchronized(sendLock) { if(requestedGeneration==generation) sendControlLocked(PacketCodec.TYPE_STOP) }
        }
    }

    fun sendHello(deviceName: String) {
        helloName=deviceName
        val bytes = deviceName.toByteArray(Charsets.UTF_8)
        val requestedGeneration=synchronized(sendLock) { generation }
        controlExecutor.execute {
            synchronized(sendLock) {
                if(requestedGeneration==generation) sendLocked(PacketCodec.TYPE_HELLO, 0, bytes, bytes.size)
            }
        }
    }

    /** 发送一个 AUDIO 包（自动递增 seq）。payload 必须恰好 960 字节。 */
    fun sendAudio(payload: ByteArray, length: Int, timestampUs: Int, discontinuity: Boolean) {
        if (!audioEnabled) return
        synchronized(sendLock) {
            if (!identityVerified || !audioEnabled) return
            val sock = socket ?: return
            val address = resolveTarget() ?: return
            val flags = if (discontinuity) PacketCodec.FLAG_DISCONTINUITY else 0
            val packetSeq = seq++ // Consume source time even if send fails.
            val now = SystemClock.elapsedRealtime()
            if (now >= nextSendDiagnostic) {
                nextSendDiagnostic = now + 10_000
                Diagnostics.recordAsync("Wifi.SendStats", packetSeq.toLong(), sendFailures, targetPort.toLong())
            }
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
                if (running && sock !== socket) continue
                if (running) Log.w(TAG, "receive failed", e)
                break
            }
            if (sock !== socket) continue
            val length = packet.length
            if (!PacketCodec.isValid(buffer, length)) continue
            when (PacketCodec.typeOf(buffer, length)) {
                PacketCodec.TYPE_OFFER -> handleOffer(packet, buffer)
                PacketCodec.TYPE_IDENTITY_REPLY -> synchronized(sendLock) {
                    resolver.reply(packet.address.hostAddress ?: "",packet.port,buffer.copyOfRange(16,length),SystemClock.elapsedRealtime())
                }
                PacketCodec.TYPE_PING -> synchronized(sendLock) { if (isSelectedPc(packet)) handlePing(packet, buffer) }
                PacketCodec.TYPE_DIAGNOSTICS_ACK -> synchronized(sendLock) {
                    val batch = diagnosticsBatch
                    if (batch != null && isSelectedPc(packet) && length == PacketCodec.HEADER_SIZE &&
                        PacketCodec.seqOf(buffer, length) == batch.sequence &&
                        PacketCodec.timestampOf(buffer, length) == diagnosticsPing) {
                        Diagnostics.acknowledge(batch)
                        diagnosticsBatch = null
                    }
                }
                PacketCodec.TYPE_VOLUME_STATE -> synchronized(sendLock) { if (isSelectedPc(packet) && length == 30) {
                    remoteVolume.receive(buffer.copyOfRange(16, 30), PacketCodec.seqOf(buffer, length), SystemClock.elapsedRealtime())
                } }
                else -> Unit
            }
        }
    }

    private fun isSelectedPc(packet: DatagramPacket): Boolean =
        identityVerified && packet.port == PacketCodec.CONTROL_PORT && packet.address?.hostAddress == targetHost

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
            deviceId = if (portOffset > 0 && length == portOffset+36)
                String(buffer,portOffset+4,32,Charsets.US_ASCII).takeIf(IdentityResolver::validId) else null,
        )
        synchronized(sendLock) {
            if (device.lastSeenMs >= nextOfferDiagnostic) {
                nextOfferDiagnostic = device.lastSeenMs + 15_000
                Diagnostics.record("Wifi.OfferReceived", device.port.toLong(), if (device.deviceId == null) 0 else 1)
            }
            resolver.hint(device)
        }
        listener?.onOffer(device)
    }

    /** PC 发起的探测：payload 空，header.timestampUs = T1（PC 发送时刻）。 */
    private fun handlePing(packet: DatagramPacket, buffer: ByteArray) {
        pingDeadline=0
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
        if (PacketCodec.flagsOf(buffer, packet.length) and PacketCodec.FLAG_DIAGNOSTICS != 0) {
            val batch = diagnosticsBatch ?: Diagnostics.batch()?.also { diagnosticsBatch = it } ?: return
            diagnosticsPing = t1
            val payload = batch.payload
            if (payload.size > 1100) return
            val diagnostic = PacketCodec.build(PacketCodec.TYPE_DIAGNOSTICS, batch.sequence, t1, payload, payload.size)
            runCatching { socket?.send(DatagramPacket(diagnostic, diagnostic.size, packet.address, packet.port)) }
        }
    }

    private var diagnosticsBatch: Diagnostics.Batch? = null
    private var diagnosticsPing = 0

    private fun intToBytes(value: Int): ByteArray {
        val out = ByteArray(4)
        PacketCodec.putIntLE(out, 0, value)
        return out
    }

    private fun sendControlLocked(type: Int) {
        sendLocked(type, (SystemClock.elapsedRealtimeNanos() / 1000L).toInt(), null, 0)
    }

    private fun sendLocked(type: Int, timestampUs: Int, payload: ByteArray?, payloadLength: Int) {
        if (!identityVerified) return
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
