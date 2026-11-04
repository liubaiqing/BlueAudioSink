package com.blueaudiosink.sender.net

import java.security.SecureRandom

/** Per-attempt nonce isolates late replies; OFFERs are hints, never connection authority. */
class IdentityResolver(
    private val send: (ByteArray, String?) -> Unit,
    private val verified: (DiscoveredDevice) -> Unit,
    private val status: (Boolean, String?) -> Unit,
) {
    private var nonce = ByteArray(16)
    private var expected: String? = null
    private val hosts = linkedSetOf<String>()
    private var wanted = false
    private var deadline = 0L
    private var nextQuery = 0L
    var waiting = false; private set
    var ready = false; private set
    fun select(id: String?, addresses: List<String>, now: Long) {
        expected = id?.takeIf(::validId); hosts.clear(); hosts.addAll(addresses.take(8)); wanted = true
        restart(now)
    }
    fun restart(now: Long) {
        if (!wanted) return
        nonce = ByteArray(16).also { SecureRandom().nextBytes(it) }
        ready = false; waiting = true; deadline = now + 10_000; nextQuery = now
        status(true, null)
    }
    fun fail(message: String) { ready=false;waiting=false;status(false,message) }
    fun clear() { wanted = false; waiting = false; ready = false; hosts.clear(); nonce = ByteArray(16); status(false,null) }
    fun hint(device: DiscoveredDevice) {
        if(wanted && !ready && !waiting && expected!=null && device.deviceId==expected) restart(device.lastSeenMs)
        if (waiting && expected != null && device.deviceId == expected && hosts.size < 16) {
            if (hosts.add(device.ip)) nextQuery = 0
        }
    }
    fun tick(now: Long) {
        if (!waiting) return
        if (now >= deadline) { waiting = false; status(false,"未找到这台电脑，请确认电脑已启用 Wi-Fi、处于同一网络且已更新双端"); return }
        if (now < nextQuery) return
        nextQuery = now + 1_000
        val payload = ByteArray(48); nonce.copyInto(payload)
        expected?.toByteArray(Charsets.US_ASCII)?.copyInto(payload,16)
        // Known identities can be asked for by broadcast. Initial pairing only contacts the chosen IP.
        if (expected != null) send(payload,null)
        hosts.toList().forEach { send(payload,it) }
    }
    fun reply(ip: String, sourcePort: Int, payload: ByteArray, now: Long) {
        if (!waiting || now >= deadline || sourcePort != PacketCodec.CONTROL_PORT || payload.size !in 52..115) return
        if (!payload.copyOfRange(0,16).contentEquals(nonce)) return
        val id = String(payload,16,32,Charsets.US_ASCII)
        if (!validId(id) || (expected != null && id != expected) || (expected == null && ip !in hosts)) return
        val port = PacketCodec.getIntLE(payload,48)
        if (port !in 1..65535) return
        val name = String(payload,52,payload.size-52,Charsets.UTF_8).trim().ifBlank { ip }
        expected = id; ready = true; waiting = false; hosts.add(ip)
        status(false,null); verified(DiscoveredDevice(name,ip,port,now,id))
    }
    companion object { fun validId(id: String) = id.length == 32 && id.all { it in '0'..'9' || it in 'a'..'f' } }
}
