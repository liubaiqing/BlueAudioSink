package com.blueaudiosink.sender.core

import android.content.Context
import android.media.AudioManager
import android.os.SystemClock
import android.util.Log
import com.blueaudiosink.sender.capture.BufferTier
import com.blueaudiosink.sender.net.DiscoveredDevice
import com.blueaudiosink.sender.net.DiscoveryClient
import com.blueaudiosink.sender.net.PacketCodec
import com.blueaudiosink.sender.net.UdpSender
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.getAndUpdate
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
        Diagnostics.attach(app)
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

    private var forgottenId: String? = null
    private var legacyToUpgrade: String? = null
    fun connect(ip: String, port: Int = PacketCodec.DEFAULT_AUDIO_PORT): Boolean {
        val saved=_state.value.savedDevices.firstOrNull { it.key==ip }
        val found=_state.value.devices.firstOrNull { (it.deviceId?.let { id->"id:$id" } ?: "ip:${it.ip}")==ip || it.ip==ip }
        val host=saved?.ip ?: found?.ip ?: ip.removePrefix("ip:").trim()
        if (host.isEmpty() || host.startsWith("id:")) { postMessage("请输入电脑 IPv4 地址");return false }
        if (!ensureSocket()) { postMessage("网络初始化失败：无法监听45678端口");return false }
        val id=saved?.deviceId ?: found?.deviceId
        forgottenId=null
        legacyToUpgrade=saved?.takeIf { it.deviceId==null }?.key
        savedForIp=null;lastPingAtMs=0;pingReceived=0;pingMissed=0
        _state.update { it.copy(targetDeviceId=id,targetName=saved?.name ?: found?.name ?: host,
            targetIp=null,targetPort=port,pcConnected=false,resolving=true,phase=if(streaming) LinkPhase.STREAMING else LinkPhase.IDLE) }
        sender?.selectDevice(id,(listOf(found?.ip,host).filterNotNull()+(saved?.addresses ?: emptyList())).distinct())
        return true
    }

    fun mergeLegacy(keys: Set<String>) {
        val id=_state.value.targetDeviceId ?: return
        val records=savedStore?.mergeLegacy(id,keys) ?: return
        _state.update { it.copy(savedDevices=records) }
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
                targetDeviceId = null, resolving = false,
                targetName = null,
                rttMs = -1,
                lossPermille = 0,
                packetsSent = 0,
                sendingSeconds = sender?.sendingSeconds() ?: it.sendingSeconds,
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
        if (host == (_state.value.targetDeviceId?.let { "id:$it" } ?: "ip:${_state.value.targetIp}")) {
            savedForIp = _state.value.targetIp; legacyToUpgrade=null; forgottenId=_state.value.targetDeviceId
        }
        _state.update { it.copy(savedDevices = remaining) }
    }

    // ---------------------------------------------------------------- 发送（由前台服务驱动）

    /** 采集已就绪：发 HELLO + START，seq 归零，随后保持 200 包/秒。 */
    fun startStreaming(name: String): Boolean {
        if (!ensureSocket()) {
            fail("网络初始化失败：无法监听 45678 端口")
            return false
        }
        val udp = sender ?: return false
        val current = _state.value
        if (!udp.hasTarget() || !current.pcConnected || current.resolving) {
            fail("请先连接并确认电脑身份，再开始发送")
            return false
        }
        udp.resetSendDuration()
        streaming = true
        pendingPackets = 0
        pingReceived = 0
        pingMissed = 0
        _state.update {
            it.copy(phase = LinkPhase.STREAMING, pcConnected = pingFresh(), packetsSent = 0, sendingSeconds = 0, rttMs = -1, lossPermille = 0)
        }
        udp.sendHello(name.ifBlank { deviceName })
        udp.sendStart()
        Diagnostics.record("Capture.Start")
        return true
    }

    /** 采集结束：发 STOP（不重传），PC 5 秒超时兜底。 */
    fun stopStreaming() {
        if (streaming) Diagnostics.record("Capture.Stop")
        if (streaming) sender?.sendStop()
        streaming = false
        pendingPackets = 0
        _state.update {
            it.copy(phase = if (pingFresh()) LinkPhase.CONNECTED else LinkPhase.IDLE, pcConnected = pingFresh(),
                sendingSeconds = sender?.sendingSeconds() ?: it.sendingSeconds)
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

    override fun onOffer(device: DiscoveredDevice) { discovery?.offer(device) }
    override fun onResolving(active: Boolean, error: String?) {
        if (active) Diagnostics.record("Wifi.Resolve")
        if (error != null) Diagnostics.record("Wifi.ResolveFailed")
        if(active) { lastPingAtMs=0; savedForIp=null }
        _state.update { it.copy(resolving=active,pcConnected=false,
            phase=if(streaming) LinkPhase.STREAMING else LinkPhase.IDLE) }
        if(error!=null) postMessage(error)
    }
    override fun onResolved(device: DiscoveredDevice) {
        Diagnostics.record("Wifi.IdentityConfirmed", device.port.toLong())
        _state.update { it.copy(targetIp=device.ip,targetPort=device.port,targetName=device.name,
            targetDeviceId=device.deviceId,resolving=false,pcConnected=false) }
        sender?.sendHello(deviceName)
        if(streaming) sender?.sendStart()
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
        val previous = _state.getAndUpdate {
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
        if (hasTarget && !previous.pcConnected && !previous.resolving) {
            Diagnostics.record("Wifi.Connected")
            runCatching {
                appContext?.getSystemService(AudioManager::class.java)
                    ?.setStreamVolume(AudioManager.STREAM_MUSIC, 0, 0)
            }.onFailure { Log.w("BasLink", "Could not mute phone media on Wi-Fi connection", it) }
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
     * 本次确认的地址只写一次；身份由当前握手提供，OFFER不直接落盘。
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
        val id=_state.value.targetDeviceId ?: return
        if(id==forgottenId) return
        val saved = store.remember(label, host, port,id,legacyToUpgrade)
        legacyToUpgrade=null
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
        if (streaming || discoveryActive || _state.value.targetDeviceId!=null || _state.value.resolving) return
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
                    Diagnostics.record("Wifi.HeartbeatTimeout")
                    lastPingAtMs=0
                    sender?.suspendIdentity()
                }
                if (streaming) sender?.sendingSeconds()?.let { seconds ->
                    _state.update { if (streaming) it.copy(sendingSeconds = seconds) else it }
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
