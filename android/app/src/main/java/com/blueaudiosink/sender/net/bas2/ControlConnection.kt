package com.blueaudiosink.sender.net.bas2

import android.net.Network
import org.json.JSONObject
import java.io.Closeable
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.security.SecureRandom
import java.util.UUID

internal data class RouteCandidate(
    val interfaceName: String,
    val network: Network?,
    val localAddress: Inet4Address,
    val remoteAddress: Inet4Address,
    val controlPort: Int,
    val networkGeneration: Long,
)

/** Blocking control operations belong to the serial session worker, never the UI or PCM thread. */
internal class ControlConnection(private val route: RouteCandidate) : Closeable {
    private val socket: Socket = route.network?.socketFactory?.createSocket() ?: Socket()
    private var mediaSocket: Socket? = null
    private var udp: DatagramSocket? = null
    private var mediaWriter: TcpMediaWriter? = null
    private var requestId = 0
    private var sessionBytes = ByteArray(0)
    private var epoch = 0L
    private var sequence = 0
    private val mediaLock = Any()
    private val resourceLock = Any()
    private var diagnosticSocket: Socket? = null
    @Volatile private var closed = false
    @Volatile private var sending = false
    @Volatile private var mediaFailure: java.io.IOException? = null
    @Volatile private var firstSentNs = 0L
    @Volatile private var lastSentNs = 0L
    private var heartbeatRtt = -1.0
    var receiptSent = 0L; private set
    var receiptReceived = 0L; private set
    var sessionId: String = ""; private set
    var deviceId: String = ""; private set
    var deviceName: String = ""; private set
    var audioPort: Int = 0; private set
    var usingTcp: Boolean = false; private set
    var attemptId = ""
    var pathId = ""
    val streamEpoch: String get() = java.lang.Long.toUnsignedString(epoch,16)
    val localAddress: InetAddress get() = socket.localAddress

    fun connect(expectedDeviceId: String?, instanceId: String, name: String, identityPhase: () -> Unit = {}) {
        try {
            socket.tcpNoDelay = true
            socket.soTimeout = 2000
            socket.bind(InetSocketAddress(route.localAddress, 0))
            socket.connect(InetSocketAddress(route.remoteAddress, route.controlPort), 2000)
            identityPhase()
            val welcome = request("hello", JSONObject().put("protocol", 2).put("expectedDeviceId", expectedDeviceId.orEmpty())
                .put("instanceId", instanceId).put("appRunId", instanceId).put("attemptId",attemptId).put("pathId",pathId).put("name", name), "welcome")
            val body = welcome.getJSONObject("body")
            require(body.getInt("protocol") == 2)
            deviceId = body.getString("deviceId")
            require(deviceId.matches(Regex("[0-9a-f]{32}")) && (expectedDeviceId == null || expectedDeviceId == deviceId))
            deviceName = body.getString("name")
            audioPort = body.getInt("audioPort")
            require(audioPort in 1..65535)
        } catch (error: Exception) { close(); throw error }
    }

    fun verifyTransport(forceTcp: Boolean = false): Boolean {
        check(!closed && sessionBytes.size == 16)
        val challenge = SecureRandom().generateSeed(16)
        val candidate = DatagramSocket(null)
        var verified = false
        try {
            synchronized(resourceLock) {
                check(!closed)
                udp = candidate // cancellation must also close sockets still being probed
            }
            route.network?.bindSocket(candidate)
            candidate.bind(InetSocketAddress(socket.localAddress, 0))
            candidate.connect(route.remoteAddress, audioPort)
            candidate.soTimeout = 500
            val probe = WireCodec.encodeMedia(MediaFrame(2, 0, sessionBytes, 0, 0, 0, challenge))
            val deadline = System.nanoTime() + 1_500_000_000L
            while (!forceTcp && System.nanoTime() < deadline) {
                candidate.send(DatagramPacket(probe, probe.size))
                val response = DatagramPacket(ByteArray(1201), 1201)
                try {
                    candidate.receive(response)
                    val frame = WireCodec.decodeMedia(response.data.copyOf(response.length))
                    if (frame.type == 3 && frame.sessionId.contentEquals(sessionBytes) && frame.payload.contentEquals(challenge)) {
                        request("pathConfirmed", JSONObject().put("challenge", hex(challenge)), "pathReady")
                        check(!closed)
                        verified = true
                        usingTcp = false
                        return true
                    }
                } catch (_: java.net.SocketTimeoutException) { }
                catch (_: IllegalArgumentException) { }
            }
        } catch (_: java.net.SocketException) {
            // A failed UDP path is eligible for independently verified TCP fallback.
        } finally {
            if (!verified) {
                candidate.close()
                synchronized(resourceLock) { if (udp === candidate) udp = null }
            }
        }

        check(!closed)
        val data = route.network?.socketFactory?.createSocket() ?: Socket()
        try {
            synchronized(resourceLock) { check(!closed); mediaSocket = data }
            data.tcpNoDelay = true
            data.soTimeout = 2000
            data.bind(InetSocketAddress(socket.localAddress, 0))
            data.connect(InetSocketAddress(route.remoteAddress, audioPort), 2000)
            val hello = JSONObject().put("type", "mediaHello").put("requestId", 1)
                .put("sessionId", sessionId).put("body", JSONObject())
            data.getOutputStream().write(WireCodec.encodeControlPayload(hello.toString().toByteArray(Charsets.UTF_8)))
            val response = JSONObject(String(WireCodec.readControlPayload(data.getInputStream()), Charsets.UTF_8))
            require(response.getString("type") == "mediaReady" && response.getString("sessionId") == sessionId && response.getInt("requestId") == 1)
            synchronized(resourceLock) {
                check(!closed)
                mediaWriter = TcpMediaWriter(data) {
                    sending = false
                    mediaFailure = java.io.IOException("BAS2 TCP media stalled or queue exhausted")
                }
            }
            usingTcp = true
            return true
        } catch (error: Exception) { data.close(); throw error }
    }

    fun start() {
        val response = request("start", JSONObject(), "startAck")
        val nextEpoch = java.lang.Long.parseUnsignedLong(response.getJSONObject("body").getString("streamEpoch"), 16)
        require(nextEpoch != 0L)
        synchronized(mediaLock) { epoch = nextEpoch; sequence = 0; sending = true }
        firstSentNs = 0; lastSentNs = 0
    }

    fun stop() {
        synchronized(mediaLock) { sending = false; epoch = 0 }
        mediaWriter?.clear()
        request("stop", JSONObject(), "stopAck")
    }

    fun heartbeat() {
        mediaFailure?.let { throw it }
        socket.soTimeout = 4000
        val body = JSONObject(); if (heartbeatRtt >= 0) body.put("rttMs",heartbeatRtt)
        val sent = System.nanoTime()
        receiptSent = sequence.toLong() and 0xffffffffL
        val reply = try { request("heartbeat", body, "heartbeatAck").getJSONObject("body") } finally { socket.soTimeout = 2000 }
        heartbeatRtt = (System.nanoTime()-sent)/1_000_000.0
        receiptReceived = reply.optLong("audioPackets",0)
        val now = System.nanoTime()
        if (!usingTcp && sending && firstSentNs != 0L && now-firstSentNs > 1_000_000_000 && now-lastSentNs < 1_000_000_000 &&
            (reply.optLong("audioAgeMs",-1) < 0 || reply.optLong("audioAgeMs") > 1000)) {
            val deadline = now + 2_000_000_000
            val challenge = SecureRandom().generateSeed(16)
            val probe = WireCodec.encodeMedia(MediaFrame(2,0,sessionBytes,0,0,0,challenge))
            while (System.nanoTime() < deadline) {
                runCatching { udp?.send(DatagramPacket(probe,probe.size)) }
                Thread.sleep(200)
                val state = request("heartbeat",JSONObject(),"heartbeatAck").getJSONObject("body")
                if (state.optLong("audioAgeMs",-1) in 0..999) return
            }
            throw UdpUnavailable()
        }
    }

    fun sendAudio(payload: ByteArray, timestampUs: Int, discontinuity: Boolean) {
        synchronized(mediaLock) {
            mediaFailure?.let { throw it }
            if (!sending || closed) return
            val now = System.nanoTime(); if (firstSentNs == 0L) firstSentNs = now; lastSentNs = now
            val packet = WireCodec.encodeMedia(MediaFrame(1, if (discontinuity) 1 else 0,
                sessionBytes, epoch, sequence++, timestampUs, payload))
            if (usingTcp) mediaWriter?.offer(packet)
            else udp?.send(DatagramPacket(packet, packet.size))
        }
    }

    private fun request(type: String, body: JSONObject, expectedReply: String): JSONObject {
        check(!closed)
        requestId++
        if (requestId == 0) requestId++
        val envelope = JSONObject().put("type", type).put("requestId", requestId.toLong() and 0xffffffffL)
            .put("sessionId", sessionId).put("body", body)
        socket.getOutputStream().write(WireCodec.encodeControlPayload(envelope.toString().toByteArray(Charsets.UTF_8)))
        val response = JSONObject(String(WireCodec.readControlPayload(socket.getInputStream()), Charsets.UTF_8))
        require(response.getLong("requestId") == requestId.toLong().and(0xffffffffL))
        val responseSession = response.getString("sessionId")
        if (sessionId.isEmpty()) {
            require(responseSession.matches(Regex("[0-9a-f]{32}")))
            sessionId = responseSession
            sessionBytes = ByteArray(16) { responseSession.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
        } else require(responseSession == sessionId)
        if (response.getString("type") == "error") throw java.io.IOException("BAS2: " + response.getJSONObject("body").getString("code"))
        require(response.getString("type") == expectedReply)
        return response
    }
    fun application(type: String, body: JSONObject, expected: String): JSONObject = request(type, body, expected).getJSONObject("body")
    fun diagnostics(events: org.json.JSONArray): org.json.JSONArray {
        check(!closed)
        val data=route.network?.socketFactory?.createSocket() ?: Socket()
        try {
            synchronized(resourceLock) { check(!closed); diagnosticSocket=data }
            data.soTimeout=2000; data.tcpNoDelay=true
            data.bind(InetSocketAddress(socket.localAddress,0)); data.connect(InetSocketAddress(route.remoteAddress,route.controlPort),2000)
            val json=JSONObject().put("type","diagnostics").put("requestId",1).put("sessionId",sessionId).put("body",JSONObject().put("events",events))
            data.getOutputStream().write(WireCodec.encodeControlPayload(json.toString().toByteArray(Charsets.UTF_8)))
            val response=JSONObject(String(WireCodec.readControlPayload(data.getInputStream()),Charsets.UTF_8))
            require(response.getString("sessionId")==sessionId && response.getInt("requestId")==1 && response.getString("type")=="diagnosticsAck")
            return response.getJSONObject("body").getJSONArray("ids")
        } finally { data.close(); synchronized(resourceLock) { if(diagnosticSocket===data) diagnosticSocket=null } }
    }

    override fun close() {
        synchronized(resourceLock) {
            closed = true
            sending = false
            runCatching { socket.close() }
            runCatching { udp?.close() }
            mediaWriter?.close()
            runCatching { mediaSocket?.close() }
            runCatching { diagnosticSocket?.close() }
        }
    }

    companion object {
        fun instanceId(): String = UUID.randomUUID().toString().replace("-", "")
        private fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it.toInt() and 255) }
    }
}
