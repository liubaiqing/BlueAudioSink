package com.blueaudiosink.sender.net.bas2

import android.content.Context
import android.os.SystemClock
import com.blueaudiosink.sender.core.Diagnostics
import com.blueaudiosink.sender.core.SendDuration
import com.blueaudiosink.sender.net.DiscoveredDevice
import com.blueaudiosink.sender.net.SenderListener
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.atomic.AtomicLong

internal class Bas2Sender(private val context: Context, private val phoneName: () -> String) {
    @Volatile var listener: SenderListener? = null
    private var environment: NetworkEnvironment? = null
    private var discovery: LanDiscovery? = null
    private var coordinator: SessionCoordinator? = null
    private val duration = SendDuration { SystemClock.elapsedRealtime() }
    private val instance = Diagnostics.runId
    private val intent = AtomicLong()
    @Volatile private var opened = false
    @Volatile private var selectedId: String? = null
    @Volatile private var selectedAddresses = emptyList<String>()
    @Volatile private var currentSession: String? = null
    private var lastError: String? = null
    private var routeEvidence = ""
    private var volumeEpoch = ""
    private var volumeRevision = 0L
    private val diagnosticWorker=java.util.concurrent.Executors.newSingleThreadScheduledExecutor()
    @Volatile private var diagnosticControl: ControlConnection? = null
    init { diagnosticWorker.scheduleWithFixedDelay({
        val current=diagnosticControl
        val batch=Diagnostics.batch()
        if(current!=null && batch!=null && opened) runCatching {
            val ids=current.diagnostics(JSONArray(String(batch.payload,Charsets.UTF_8)))
            if((0 until ids.length()).map { ids.getString(it) }.toSet().containsAll(batch.ids)) Diagnostics.acknowledge(batch)
        }
    },2,2,java.util.concurrent.TimeUnit.SECONDS) }
    fun open(): Boolean {
        if (opened) return true
        return try {
            opened = true
            coordinator = SessionCoordinator({ route, id, phase ->
                val network = environment?.snapshot ?: error("网络信息未就绪")
                AndroidSessionConnection(network.candidate(route), id, instance, phoneName().take(64), phase, ::services)
            }, ::changed, { code, fields ->
                if(code == "Bas2.RouteFailed") routeEvidence = fields["error"].orEmpty()
                Diagnostics.recordContext(code, fields)
            })
            try {
                discovery = LanDiscovery(context, ::offer) { evidence -> Diagnostics.recordContext("Bas2.Discovery", mapOf("evidence" to evidence)) }
            } catch(error: Exception) {
                Diagnostics.recordContext("Bas2.DiscoveryUnavailable",mapOf("error" to error.toString()))
                listener?.onError("自动发现暂不可用，仍可选择已保存电脑或手动输入地址连接")
            }
            environment = NetworkEnvironment(context) { snapshot ->
                discovery?.update(snapshot); coordinator?.networkChanged(snapshot.routes)
                Diagnostics.recordContext("Bas2.Network", mapOf("generation" to snapshot.generation.toString(),
                    "routes" to snapshot.routes.joinToString { "${it.interfaceName}/${it.address}" }, "vpn" to snapshot.vpnHandles.isNotEmpty().toString(), "error" to snapshot.enumerationError.orEmpty()))
            }
            true
        } catch (error: Exception) { close(); listener?.onError("局域网初始化失败：${error.message}"); false }
    }
    fun isOpen() = opened
    fun close() { opened = false; diagnosticControl=null; diagnosticWorker.shutdownNow(); duration.setActive(false); coordinator?.close(); coordinator = null; discovery?.close(); discovery = null; environment?.close(); environment = null }
    fun selectDevice(id: String?, addresses: List<String>) {
        selectedId = id; selectedAddresses = addresses; currentSession = null; intent.incrementAndGet()
        discovery?.target(id,addresses); coordinator?.selectDevice(id,addresses); discovery?.refresh()
    }
    private fun offer(device: DiscoveredDevice, bas2: Boolean) {
        listener?.onOffer(device)
        if ((selectedId != null && selectedId == device.deviceId) || (selectedId == null && device.ip in selectedAddresses)) {
            if (!bas2) { listener?.onError("电脑版协议较旧，请同时更新电脑端和手机端"); return }
            if (selectedId == null) { selectedId = device.deviceId; coordinator?.selectDevice(device.deviceId, (selectedAddresses + device.ip).distinct()) }
            device.deviceId?.let { coordinator?.rememberAddresses(it,listOf(device.ip)) }
        }
    }
    private fun changed(snapshot: ConnectionSnapshot) {
        Diagnostics.connection(mapOf("attemptId" to snapshot.attemptId.orEmpty(), "pathId" to snapshot.route?.key.orEmpty(),
            "sessionId" to snapshot.sessionId.orEmpty(), "streamEpoch" to snapshot.streamEpoch.orEmpty()))
        listener?.onStage(when(snapshot.phase) {
            ConnectionPhase.FINDING -> "查找电脑"
            ConnectionPhase.CONTROL -> "建立连接"
            ConnectionPhase.IDENTITY -> "核对身份"
            ConnectionPhase.VERIFYING -> "验证传输"
            ConnectionPhase.RECOVERING -> "正在恢复"
            ConnectionPhase.FAILED -> "连接失败"
            else -> null
        },snapshot.usingTcp)
        if(snapshot.phase == ConnectionPhase.CONTROL) routeEvidence = ""
        duration.setActive(snapshot.phase == ConnectionPhase.STREAMING)
        Diagnostics.recordContext("Bas2.State", mapOf("phase" to snapshot.phase.name, "attemptId" to snapshot.attemptId.orEmpty(),
            "sessionId" to snapshot.sessionId.orEmpty(), "pathId" to snapshot.route?.key.orEmpty(), "tcp" to snapshot.usingTcp.toString(), "error" to snapshot.error.orEmpty()))
        if (snapshot.phase == ConnectionPhase.READY || snapshot.phase == ConnectionPhase.STREAMING) {
            if (currentSession != snapshot.sessionId) {
                selectedId = snapshot.deviceId
                discovery?.target(selectedId,selectedAddresses)
                currentSession = snapshot.sessionId; intent.incrementAndGet(); volumeEpoch = ""; listener?.onRemoteVolume(null,false)
                val device = DiscoveredDevice(snapshot.deviceName.orEmpty(),snapshot.route!!.remote,45679,SystemClock.elapsedRealtime(),snapshot.deviceId)
                listener?.onResolved(device)
                coordinator?.application { (it as AndroidSessionConnection).let { connection -> services(connection.control) } }
            }
            lastError = null; listener?.onPing(0,0)
        } else {
            diagnosticControl=null; currentSession = null; intent.incrementAndGet(); listener?.onRemoteVolume(null,false)
            val error = if (snapshot.phase == ConnectionPhase.FAILED && snapshot.error != lastError) {
                lastError = snapshot.error
                when {
                    routeEvidence.contains("permitted",true) || routeEvidence.contains("Permission",true) -> "系统拒绝局域网绑定，请检查 VPN 的绕过或局域网设置"
                    routeEvidence.contains("Busy") -> "电脑正在接收另一台手机，请先在电脑设置中选择这台手机"
                    routeEvidence.contains("UpgradeRequired") -> "协议版本不同，请同时更新电脑端和手机端"
                    else -> "当前路径无法连接电脑，请刷新设备，或检查局域网是否隔离"
                }
            } else null
            listener?.onResolving(snapshot.phase != ConnectionPhase.STOPPED,error)
        }
    }
    private fun services(control: ControlConnection) {
        val state = control.application("volumeGet",JSONObject(),"volumeState")
        applyVolume(state)
        listener?.onPing(0,0)
        listener?.onAudioReceipt(control.receiptSent,control.receiptReceived)
        diagnosticControl=control
    }

    private fun applyVolume(body: JSONObject) {
        volumeEpoch = body.getString("epoch"); volumeRevision = body.getLong("revision")
        listener?.onRemoteVolume(body.getInt("percent"),true)
    }
    fun setRemoteVolume(percent: Int) {
        val version = intent.incrementAndGet()
        coordinator?.latestApplication { connection ->
            if (version != intent.get() || volumeEpoch.isEmpty()) return@latestApplication
            val body = (connection as AndroidSessionConnection).control.application("volumeSet",JSONObject().put("epoch",volumeEpoch)
                .put("revision",volumeRevision).put("percent",percent.coerceIn(0,100)),"volumeState")
            if (!body.optBoolean("accepted")) { intent.incrementAndGet(); listener?.onVolumeConflict() }
            applyVolume(body)
        }
    }
    fun refreshDiscovery() { discovery?.refresh() }
    fun clearTarget() { selectedId = null; selectedAddresses = emptyList(); discovery?.target(null,emptyList()); coordinator?.disconnect() }
    fun suspendIdentity() = coordinator?.reconnect()
    fun hasTarget() = currentSession != null
    fun sendHello(name: String) { /* name is read at hello from the current application preference */ }
    fun sendStart() = coordinator?.setSending(true)
    fun sendStop() = coordinator?.setSending(false)
    fun resetSendDuration() = duration.reset()
    fun sendingSeconds() = duration.milliseconds()/1000
    fun sendAudio(payload: ByteArray, length: Int, timestampUs: Int, discontinuity: Boolean) {
        if (length == 960) coordinator?.sendAudio(if (payload.size == length) payload else payload.copyOf(length),timestampUs,discontinuity)
    }
}
