package com.blueaudiosink.sender.core

import android.content.Context
import android.os.SystemClock
import com.blueaudiosink.sender.capture.BufferTier
import com.blueaudiosink.sender.net.DiscoveredDevice
import com.blueaudiosink.sender.net.DiscoveryClient
import com.blueaudiosink.sender.net.PacketCodec
import com.blueaudiosink.sender.net.UdpSender
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/**
 * 应用级连接状态与 UDP 通道的唯一入口。
 *
 * 界面（Activity）与采集服务（Service）同进程，都通过本对象协作：
 * - Activity：发现、选择目标、开始/停止、改档位
 * - Service：拿到 MediaProjection 后调用 [startStreaming]，并把每包 PCM 交给 [sendAudio]
 *
 * 协议裁决：PING 由 PC 发起，手机只回 PONG。
 * 因此「已连接」的判据是「最近 5 秒收到过 PC 的 PING」；
 * 丢包率是控制通道（PING）丢失率；RTT 由 PC 计算并显示在 PC 界面（音量状态不携带 RTT）。
 */
object LinkController : UdpSender.Listener {

    /** PC 每 2 秒一次 PING；间隔超过该值即认定中间丢了一个。 */
    private const val PING_PERIOD_MS = 2_000L
    private const val PING_GAP_TOLERANCE_MS = 500L
    private const val PING_FRESH_MS = 5_000L

    /** 状态里的包计数每 N 包刷新一次，避免每包都触发重组。 */
    private const val PACKET_UI_STEP = 20L

    private val _state = MutableStateFlow(SenderUiState())
    val state: StateFlow<SenderUiState> = _state.asStateFlow()

    @Volatile
    private var appContext: Context? = null

    @Volatile
    private var deviceName: String = "Android 手机"

    private var sender: UdpSender? = null
    private var discovery: DiscoveryClient? = null
    private var discoveryActive = false

    /** 已连接设备的本地记忆（SharedPreferences + JSON）。 */
    @Volatile
    private var savedStore: SavedDeviceStore? = null

    /** 本次连接已写入记忆的 IP，避免每个 PING 都重复落盘。 */
    @Volatile
    private var savedForIp: String? = null

    @Volatile
    private var streaming: Boolean = false

    private var watchdog: Thread? = null

    private var lastPingAtMs: Long = 0
    private var pingReceived: Int = 0
    private var pingMissed: Int = 0
    private var pendingPackets: Long = 0

    // ---------------------------------------------------------------- 生命周期

    fun attach(context: Context) {
        val app = context.applicationContext
        appContext = app
        if (savedStore == null) {
            synchronized(this) {
                if (savedStore == null) {
                    val store = SavedDeviceStore(app)
                    savedStore = store
                    val saved = store.load()
                    _state.update { it.copy(savedDevices = saved) }
                }
            }
        }
    }

    fun setDeviceName(name: String) {
        if (name.isNotBlank()) deviceName = name
    }

    // ---------------------------------------------------------------- 发现

    fun startDiscovery(context: Context) {
        attach(context)
        if (discoveryActive) return
        if (!ensureSocket()) return
        discoveryActive = true
        discovery = DiscoveryClient { devices ->
            _state.update { it.copy(devices = devices) }
        }.also { it.start() }
    }

    fun stopDiscovery() {
        discoveryActive = false
        discovery?.stop()
        discovery = null
        _state.update { it.copy(devices = emptyList()) }
        releaseSocketIfIdle()
    }

    fun restartDiscovery(context: Context) {
        stopDiscovery()
        startDiscovery(context)
    }

    // ---------------------------------------------------------------- 连接

    /** 选择/输入目标电脑。HELLO 让 PC 先认识我们的源地址，从而开始发 PING。 */
    fun connect(ip: String, port: Int = PacketCodec.DEFAULT_AUDIO_PORT): Boolean {
        val host = ip.trim()
        if (host.isEmpty()) {
            postMessage("请输入电脑 IP")
            return false
        }
        if (!ensureSocket()) {
            postMessage("网络初始化失败：无法监听 45678 端口")
            return false
        }
        val udp = sender ?: return false
        val audioPort = if (port in 1..65_535) port else PacketCodec.DEFAULT_AUDIO_PORT
        if (!udp.setTarget(host, audioPort)) return false

        lastPingAtMs = 0
        pingReceived = 0
        pingMissed = 0
        // 新目标：首个 PING 到达时重新写入记忆（刷新「最近使用」时间）
        savedForIp = null

        val knownName = _state.value.devices.firstOrNull { it.ip == host }?.name
            ?: _state.value.savedDevices.firstOrNull { it.ip == host }?.name
        _state.update {
            it.copy(
                pcConnected = false,
                targetIp = host,
                targetPort = audioPort,
                targetName = knownName ?: host,
                rttMs = -1,
                lossPermille = 0,
                phase = LinkPhase.IDLE,
            )
        }
        udp.sendHello(deviceName)
        return true
    }

    fun disconnect() {
        sender?.clearTarget()
        lastPingAtMs = 0
        savedForIp = null
        streaming = false
        _state.update {
            it.copy(
                phase = LinkPhase.IDLE,
                pcConnected = false,
                targetIp = null,
                targetName = null,
                rttMs = -1,
                lossPermille = 0,
                packetsSent = 0,
            )
        }
        releaseSocketIfIdle()
    }

    fun setTier(tier: BufferTier) {
        _state.update { it.copy(tier = tier) }
    }

    /** 从「已保存设备」里删除一台（不影响当前连接）。 */
    fun removeSavedDevice(ip: String) {
        val store = savedStore ?: return
        val host = ip.trim()
        val remaining = store.remove(host)
        // 删的正是当前连接时，本会话内不再自动写回：否则下一个 PING（2 秒内）又把它存回来。
        // 用户重新点一次该设备（connect 会清空 savedForIp）才会再次记住。
        if (host == _state.value.targetIp) savedForIp = host
        _state.update { it.copy(savedDevices = remaining) }
    }

    // ---------------------------------------------------------------- 发送（由前台服务驱动）

    /** 采集已就绪：发 HELLO + START，seq 归零，随后保持 200 包/秒。 */
    fun startStreaming(name: String) {
        if (!ensureSocket()) {
            fail("网络初始化失败：无法监听 45678 端口")
            return
        }
        val udp = sender ?: return
        val current = _state.value
        if (!udp.hasTarget()) {
            val ip = current.targetIp
            if (ip.isNullOrBlank()) {
                fail("尚未连接电脑，无法开始发送")
                return
            }
            if (!udp.setTarget(ip, current.targetPort)) {
                fail("目标地址无效：" + ip)
                return
            }
        }
        streaming = true
        pendingPackets = 0
        pingReceived = 0
        pingMissed = 0
        _state.update {
            it.copy(phase = LinkPhase.STREAMING, pcConnected = pingFresh(), packetsSent = 0, rttMs = -1, lossPermille = 0)
        }
        udp.sendHello(name.ifBlank { deviceName })
        udp.sendStart()
    }

    /** 采集结束：发 STOP（不重传），PC 5 秒超时兜底。 */
    fun stopStreaming() {
        if (streaming) sender?.sendStop()
        streaming = false
        pendingPackets = 0
        _state.update {
            it.copy(phase = if (pingFresh()) LinkPhase.CONNECTED else LinkPhase.IDLE, pcConnected = pingFresh())
        }
        releaseSocketIfIdle()
    }

    /** 采集线程逐包调用；buffer 会被下一次出包覆盖，必须同步发完。 */
    fun sendAudio(buffer: ByteArray, length: Int, timestampUs: Int, discontinuity: Boolean) {
        if (!streaming) return
        sender?.sendAudio(buffer, length, timestampUs, discontinuity)
        pendingPackets++
        if (pendingPackets >= PACKET_UI_STEP) {
            val sent = pendingPackets
            pendingPackets = 0
            _state.update { it.copy(packetsSent = it.packetsSent + sent) }
        }
    }

    // ---------------------------------------------------------------- UdpSender.Listener

    override fun onOffer(device: DiscoveredDevice) {
        discovery?.offer(device)
        if (device.ip == _state.value.targetIp) {
            sender?.setTarget(device.ip, device.port)
            _state.update { it.copy(targetName = device.name, targetPort = device.port) }
            // 已保存的设备拿到真实名称后同步更新（名称比 IP 更好认）；
            // 只在它确实还在记忆列表里时更新，避免刚删掉的设备被一次广播写回来。
            if (_state.value.savedDevices.any { it.ip == device.ip }) {
                rememberTarget(device.ip, device.port, device.name, refresh = true)
            }
        }
    }

    override fun onPing(t1Us: Int, receivedAtUs: Int) {
        val now = SystemClock.elapsedRealtime()
        if (lastPingAtMs > 0) {
            val gap = now - lastPingAtMs
            if (gap > PING_PERIOD_MS + PING_GAP_TOLERANCE_MS) {
                val missed = ((gap / PING_PERIOD_MS) - 1).toInt()
                if (missed > 0) pingMissed += missed
            }
        }
        lastPingAtMs = now
        pingReceived++
        val hasTarget = _state.value.targetIp != null
        // 收到首包 = 连接成功：把当前目标写入本地记忆（之后从列表直接点连）
        if (hasTarget) {
            val current = _state.value
            rememberTarget(current.targetIp.orEmpty(), current.targetPort, current.targetName)
        }
        _state.update {
            it.copy(
                phase = when {
                    streaming -> LinkPhase.STREAMING
                    hasTarget -> LinkPhase.CONNECTED
                    else -> LinkPhase.IDLE
                },
                pcConnected = hasTarget,
                lossPermille = lossPermille(),
            )
        }
    }

    fun setRemoteVolume(percent: Int) { sender?.setRemoteVolume(percent) }

    override fun onRemoteVolume(percent: Int?, ready: Boolean) {
        _state.update { it.copy(remoteVolume = percent, remoteVolumeReady = ready) }
    }

    override fun onError(message: String) {
        postMessage(message)
    }

    // ---------------------------------------------------------------- 提示

    fun fail(message: String) {
        streaming = false
        _state.update {
            it.copy(
                phase = LinkPhase.IDLE,
                message = message,
                messageToken = it.messageToken + 1,
            )
        }
    }

    fun postMessage(message: String) {
        _state.update {
            it.copy(message = message, messageToken = it.messageToken + 1)
        }
    }

    fun clearMessage() {
        _state.update { it.copy(message = null) }
    }

    // ---------------------------------------------------------------- 内部

    /**
     * 把当前目标写入本地记忆。
     *
     * 触发时机：首次收到 PC 的 PING（= 连接成功）。
     * 同一 IP 只写一次；[refresh] 用于发现到同名设备时更新名称。
     */
    private fun rememberTarget(ip: String, port: Int, name: String?, refresh: Boolean = false) {
        val host = ip.trim()
        if (host.isEmpty()) return
        val store = savedStore ?: return
        val label = name?.trim().orEmpty().ifBlank { host }
        if (savedForIp == host) {
            if (!refresh) return
            val existing = _state.value.savedDevices.firstOrNull { it.ip == host }
            if (existing != null && existing.name == label && existing.port == port) return
        }
        savedForIp = host
        val saved = store.remember(label, host, port)
        _state.update { it.copy(savedDevices = saved) }
    }

    @Synchronized
    private fun ensureSocket(): Boolean {
        val context = appContext ?: return false
        val existing = sender
        if (existing != null) {
            if (!existing.isOpen() && !existing.open()) return false
            return true
        }
        val udp = UdpSender(context).also { it.listener = this }
        if (!udp.open()) return false
        sender = udp
        startWatchdog()
        return true
    }

    private fun releaseSocketIfIdle() {
        if (streaming || discoveryActive) return
        sender?.close()
        _state.update { it.copy(pcConnected = false, phase = LinkPhase.IDLE) }
        sender = null
        stopWatchdog()
    }

    /** 每秒检查一次 PC 的心跳是否还在，避免界面长期停留在「已连接」。 */
    private fun startWatchdog() {
        if (watchdog != null) return
        watchdog = Thread({
            while (sender != null) {
                try {
                    Thread.sleep(1_000)
                } catch (_: InterruptedException) {
                    break
                }
                if (lastPingAtMs > 0 && !pingFresh()) {
                    _state.update { it.copy(pcConnected = false, phase = if (streaming) LinkPhase.STREAMING else LinkPhase.IDLE) }
                }
            }
        }, "link-watchdog").apply {
            isDaemon = true
            start()
        }
    }

    private fun stopWatchdog() {
        watchdog?.interrupt()
        watchdog = null
    }

    private fun pingFresh(): Boolean =
        lastPingAtMs > 0 && SystemClock.elapsedRealtime() - lastPingAtMs < PING_FRESH_MS

    private fun lossPermille(): Int {
        val total = pingReceived + pingMissed
        return if (total <= 0) 0 else pingMissed * 1000 / total
    }
}