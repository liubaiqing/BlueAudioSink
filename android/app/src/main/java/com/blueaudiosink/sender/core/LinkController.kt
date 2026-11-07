package com.blueaudiosink.sender.core

import android.content.Context
import android.media.AudioManager
import android.os.SystemClock
import android.util.Log
import com.blueaudiosink.sender.capture.BufferTier
import com.blueaudiosink.sender.net.DiscoveredDevice
import com.blueaudiosink.sender.net.DiscoveryClient
import com.blueaudiosink.sender.net.PacketCodec
import com.blueaudiosink.sender.net.SenderListener
import com.blueaudiosink.sender.net.bas2.Bas2Sender
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.getAndUpdate
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

/** Application callbacks for verified BAS2 control, capture intent and lifecycle-owned discovery. */
object LinkController : SenderListener {

    private const val HEARTBEAT_FRESH_MS = 5_000L


    private val _state = MutableStateFlow(SenderUiState())
    val state: StateFlow<SenderUiState> = _state.asStateFlow()
    private val uiScope = CoroutineScope(SupervisorJob()+Dispatchers.Default)
    val volumeState = state.map { SenderUiState(phase=it.phase,pcConnected=it.pcConnected,remoteVolume=it.remoteVolume,
        remoteVolumeReady=it.remoteVolumeReady,targetDeviceId=it.targetDeviceId,connectionStage=it.connectionStage,volumeConflictToken=it.volumeConflictToken) }
        .distinctUntilChanged().stateIn(uiScope,SharingStarted.WhileSubscribed(0),SenderUiState())
    val notificationState = state.map { Triple(it.phase,it.pcConnected,it.connectionStage) }.distinctUntilChanged()
    private val discoveryCommands = Executors.newSingleThreadScheduledExecutor()
    private val visibility = AtomicLong()
    @Volatile private var discoveryWanted = false
    private val refreshGeneration = AtomicLong()
    private val selectionGeneration = AtomicLong()
    @Volatile var tierChanged: ((BufferTier)->Unit)? = null

    @Volatile
    private var appContext: Context? = null

    @Volatile
    private var deviceName: String = "Android 手机"

    @Volatile private var sender: Bas2Sender? = null
    private var discovery: DiscoveryClient? = null
    private var discoveryActive = false

    /** 已连接设备的本地记忆（SharedPreferences + JSON）。 */
    @Volatile
    private var savedStore: SavedDeviceStore? = null

    /** 本次连接已写入记忆的 IP，避免每个 BAS2 心跳 都重复落盘。 */
    @Volatile
    private var savedForIp: String? = null

    @Volatile
    private var streaming: Boolean = false

    private var watchdog: Thread? = null

    @Volatile private var lastPingAtMs: Long = 0
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
        discoveryWanted=true
        val generation=visibility.incrementAndGet()
        discoveryCommands.execute { if(discoveryWanted && visibility.get()==generation) startDiscoveryNow(context.applicationContext) }
    }
    private fun startDiscoveryNow(context: Context) {
        attach(context)
        if (discoveryActive) return
        if (!ensureSocket()) return
        discoveryActive = true
        discovery = (discovery ?: DiscoveryClient { devices ->
            _state.update { it.copy(devices = devices) }
        }).also { it.start() }
    }

    fun stopDiscovery() {
        discoveryWanted=false
        val generation=visibility.incrementAndGet()
        discoveryCommands.execute { if(!discoveryWanted && visibility.get()==generation) stopDiscoveryNow() }
    }
    private fun stopDiscoveryNow() {
        discoveryActive = false
        discovery?.stop()
        releaseSocketIfIdle()
    }

    fun restartDiscovery(context: Context) {
        val refresh=refreshGeneration.incrementAndGet()
        _state.update { it.copy(discovering=true) }
        discoveryCommands.execute {
            if(discoveryWanted && !discoveryActive) startDiscoveryNow(context.applicationContext)
            sender?.refreshDiscovery()
        }
        discoveryCommands.schedule({ if(refreshGeneration.get()==refresh) _state.update { it.copy(discovering=false) } },1500,TimeUnit.MILLISECONDS)
    }

    // ---------------------------------------------------------------- 连接

    private var forgottenId: String? = null
    private var legacyToUpgrade: String? = null
    fun connect(ip: String, port: Int = PacketCodec.DEFAULT_AUDIO_PORT): Boolean {
        val generation=selectionGeneration.incrementAndGet()
        val saved=_state.value.savedDevices.firstOrNull { it.key==ip }
        val found=_state.value.devices.firstOrNull { (it.deviceId?.let { id->"id:$id" } ?: "ip:${it.ip}")==ip || it.ip==ip }
        val host=saved?.ip ?: found?.ip ?: ip.removePrefix("ip:").trim()
        if (host.isEmpty() || host.startsWith("id:")) { postMessage("请输入电脑 IPv4 地址");return false }
        val id=saved?.deviceId ?: found?.deviceId
        forgottenId=null
        legacyToUpgrade=saved?.takeIf { it.deviceId==null }?.key
        savedForIp=null;lastPingAtMs=0
        _state.update { it.copy(targetDeviceId=id,targetName=saved?.name ?: found?.name ?: host,
            targetIp=null,targetPort=port,pcConnected=false,resolving=true,phase=if(streaming) LinkPhase.STREAMING else LinkPhase.IDLE) }
        discoveryCommands.execute {
            if(selectionGeneration.get()!=generation) return@execute
            if (!ensureSocket()) { onResolving(false,"无法建立局域网通道"); return@execute }
            if(selectionGeneration.get()!=generation) { releaseSocketIfIdle(); return@execute }
            sender?.selectDevice(id,(listOf(found?.ip,host).filterNotNull()+(saved?.addresses ?: emptyList())).distinct())
        }
        return true
    }

    fun mergeLegacy(keys: Set<String>) {
        val id=_state.value.targetDeviceId ?: return
        val records=savedStore?.mergeLegacy(id,keys) ?: return
        _state.update { it.copy(savedDevices=records) }
    }

    fun disconnect() {
        selectionGeneration.incrementAndGet()
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
        val update=tierChanged
        _state.update { it.copy(tier=tier,tierPending=update!=null) }
        update?.invoke(tier)
    }
    fun tierApplied(tier: BufferTier) { Diagnostics.recordAsync("Capture.TierApplied",tier.prefillPackets.toLong(),0,0); _state.update { if(it.tier==tier) it.copy(tierPending=false) else it } }

    /** 从「已保存设备」里删除一台（不影响当前连接）。 */
    fun removeSavedDevice(ip: String) {
        val store = savedStore ?: return
        val host = ip.trim()
        val remaining = store.remove(host)
        // 删的正是当前连接时，本会话内不再自动写回：否则下一个 BAS2 心跳（2 秒内）又把它存回来。
        // 用户重新点一次该设备（connect 会清空 savedForIp）才会再次记住。
        if (host == (_state.value.targetDeviceId?.let { "id:$it" } ?: "ip:${_state.value.targetIp}")) {
            savedForIp = _state.value.targetIp; legacyToUpgrade=null; forgottenId=_state.value.targetDeviceId
        }
        _state.update { it.copy(savedDevices = remaining) }
    }

    // ---------------------------------------------------------------- 发送（由前台服务驱动）

    /** 采集已就绪：发送 BAS2 START，seq 归零，随后保持 200 包/秒。 */
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
        _state.update {
            it.copy(phase = LinkPhase.STREAMING, pcConnected = pingFresh(), packetsSent = 0, sendingSeconds = 0, rttMs = -1, lossPermille = 0)
        }
        udp.sendHello(name.ifBlank { deviceName })
        udp.sendStart()
        Diagnostics.record("Capture.Start")
        return true
    }

    /** 采集结束：发送 BAS2 STOP 并等待控制确认。 */
    fun stopStreaming() {
        if (streaming) Diagnostics.record("Capture.Stop")
        if (streaming) sender?.sendStop()
        streaming = false
        pendingPackets = 0
        _state.update {
            it.copy(tierPending=false, phase = if (pingFresh()) LinkPhase.CONNECTED else LinkPhase.IDLE, pcConnected = pingFresh(),
                sendingSeconds = sender?.sendingSeconds() ?: it.sendingSeconds)
        }
        releaseSocketIfIdle()
    }

    /** 采集线程逐包调用；buffer 会被下一次出包覆盖，必须同步发完。 */
    fun sendAudio(buffer: ByteArray, length: Int, timestampUs: Int, discontinuity: Boolean) {
        if (!streaming) return
        sender?.sendAudio(buffer, length, timestampUs, discontinuity)
        pendingPackets++ // internal diagnostics only; no per-packet page invalidation
    }

    // ---------------------------------------------------------------- SenderListener

    override fun onOffer(device: DiscoveredDevice) { discovery?.offer(device) }
    override fun onStage(stage: String?, compatible: Boolean) { _state.update { it.copy(connectionStage=stage,compatibleTransport=compatible) } }
    override fun onAudioReceipt(sent: Long, received: Long) { if(sent > 0) _state.update { it.copy(lossPermille=(((sent-received).coerceAtLeast(0)*1000)/sent).toInt().coerceIn(0,1000)) } }
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
        lastPingAtMs = now
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
    override fun onVolumeConflict() { _state.update { it.copy(volumeConflictToken=it.volumeConflictToken+1) } }

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
     * 触发时机：首次收到 PC 的 BAS2 心跳（= 连接成功）。
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
        val udp = Bas2Sender(context) { deviceName }.also { it.listener = this }
        if (!udp.open()) return false
        sender = udp
        startWatchdog()
        return true
    }

    private fun releaseSocketIfIdle() {
        discoveryCommands.execute { releaseSocketIfIdleNow() }
    }

    @Synchronized
    private fun releaseSocketIfIdleNow() {
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
        lastPingAtMs > 0 && SystemClock.elapsedRealtime() - lastPingAtMs < HEARTBEAT_FRESH_MS


}
