package com.blueaudiosink.sender.net.bas2

import android.content.Context
import android.net.wifi.WifiManager
import android.os.SystemClock
import com.blueaudiosink.sender.net.DiscoveredDevice
import com.blueaudiosink.sender.net.PacketCodec
import java.io.Closeable
import java.net.*
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

internal class LanDiscovery(context: Context, private val found: (DiscoveredDevice, Boolean) -> Unit,
    private val evidence: (String) -> Unit) : Closeable {
    private val gate = Any()
    private val worker = Executors.newSingleThreadScheduledExecutor()
    private val sockets = linkedMapOf<String, DatagramSocket>()
    private val queries = linkedMapOf<String, Long>()
    private var snapshot = NetworkSnapshot(0, emptyList(), emptyMap(), emptySet())
    private var targetId: String? = null
    private var remembered = emptyList<String>()
    @Volatile private var closed = false
    private var lock: WifiManager.MulticastLock? = null
    private var listener: MulticastSocket? = null
    init {
        try {
            val socket = MulticastSocket(null); listener = socket
            socket.reuseAddress = true; socket.bind(InetSocketAddress(45678)); socket.soTimeout = 500
            lock = (context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager)
                .createMulticastLock("BAS2-discovery").apply { setReferenceCounted(false); acquire() }
            receive(socket)
            worker.scheduleWithFixedDelay({ runCatching { query() }.onFailure { evidence("Discovery: ${it.message}") } }, 0, 2, TimeUnit.SECONDS)
        } catch(error: Exception) { close(); throw error }
    }
    fun update(network: NetworkSnapshot) = synchronized(gate) {
        snapshot = network
        val keys = network.routes.map { "${it.interfaceName}/${it.address}/${it.networkHandle}" }.toSet()
        sockets.keys.filter { it !in keys }.forEach { sockets.remove(it)?.close() }
        network.routes.take(16).forEach { local ->
            val key = "${local.interfaceName}/${local.address}/${local.networkHandle}"
            if (key !in sockets && !closed) runCatching {
                val socket = MulticastSocket(null)
                try {
                    local.networkHandle?.let { network.networks[it]?.bindSocket(socket) }
                    socket.broadcast = true; socket.timeToLive = 1; socket.soTimeout = 500
                    socket.bind(InetSocketAddress(local.address, 0))
                    socket.networkInterface = NetworkInterface.getByName(local.interfaceName)
                    sockets[key] = socket; receive(socket)
                    runCatching { listener?.joinGroup(InetSocketAddress("224.0.0.167",45678), NetworkInterface.getByName(local.interfaceName)) }
                } catch (error: Exception) { socket.close(); throw error }
            }.onFailure { evidence("${local.interfaceName}: ${it.javaClass.simpleName}: ${it.message}") }
        }
    }
    fun target(id: String?, addresses: List<String>) = synchronized(gate) { targetId = id; remembered = addresses.take(32) }
    fun refresh() { if (!closed) runCatching { worker.execute { query() } } }
    private fun query() = synchronized(gate) {
        if (closed) return@synchronized
        val nonce = UUID.randomUUID().toString().replace("-", "")
        queries[nonce] = SystemClock.elapsedRealtime()
        queries.entries.removeAll { SystemClock.elapsedRealtime() - it.value > 8000 }
        val payload = ByteArray(48)
        for (i in 0..15) payload[i] = nonce.substring(i*2,i*2+2).toInt(16).toByte()
        targetId?.takeIf { it.matches(Regex("[0-9a-f]{32}")) }?.toByteArray(Charsets.US_ASCII)?.copyInto(payload,16)
        val query = PacketCodec.build(PacketCodec.TYPE_IDENTITY_QUERY,0,0,payload,payload.size)
        for (local in snapshot.routes.take(16)) {
            val key = "${local.interfaceName}/${local.address}/${local.networkHandle}"
            val socket = sockets[key] ?: continue
            val ip = RoutePlanner.ipv4(local.address) ?: continue
            val mask = (0xffffffffL shl (32-local.prefix)) and 0xffffffffL
            val broadcast = (ip and mask) or (mask xor 0xffffffffL)
            val destinations = remembered.filter { RoutePlanner.sameSubnet(local,it) }.toMutableSet()
            if (local.prefix < 31) destinations.add((3 downTo 0).joinToString(".") { ((broadcast shr (it*8)) and 255).toString() })
            // Conventional subnet gateway is only a bounded candidate, never a device identity.
            val gateway = (ip and mask) + 1
            destinations.add((3 downTo 0).joinToString(".") { ((gateway shr (it*8)) and 255).toString() })
            destinations.add("224.0.0.167")
            destinations.forEach { address -> runCatching { socket.send(DatagramPacket(query,query.size,InetAddress.getByName(address),45678)) } }
        }
    }
    private fun receive(socket: DatagramSocket) {
        Thread({
            val buffer = ByteArray(1201)
            while (!closed && !socket.isClosed) try {
                val packet = DatagramPacket(buffer,buffer.size); socket.receive(packet)
                val length = packet.length
                if (!PacketCodec.isValid(buffer,length) || length > 1200) continue
                val type = PacketCodec.typeOf(buffer,length)
                var name: String; var id: String?; var port: Int
                if (type == PacketCodec.TYPE_IDENTITY_REPLY && length >= 68) {
                    val nonce = buffer.copyOfRange(16,32).joinToString("") { "%02x".format(it.toInt() and 255) }
                    if (synchronized(gate) { nonce !in queries }) continue
                    id = String(buffer,32,32,Charsets.US_ASCII)
                    port = PacketCodec.getIntLE(buffer,64)
                    name = String(buffer,68,length-68,Charsets.UTF_8).trimEnd('\u0000')
                } else if (type == PacketCodec.TYPE_OFFER) {
                    val zero = PacketCodec.findZero(buffer,16,length)
                    if (zero !in 17..(length-5)) continue
                    name = String(buffer,16,zero-16,Charsets.UTF_8)
                    port = PacketCodec.getIntLE(buffer,zero+1)
                    id = if (length == zero+37) String(buffer,zero+5,32,Charsets.US_ASCII) else null
                } else continue
                if (name.length > 64 || port !in 1..65535 || (id != null && !id.matches(Regex("[0-9a-f]{32}")))) continue
                found(DiscoveredDevice(name,packet.address.hostAddress!!,port,SystemClock.elapsedRealtime(),id), buffer[5].toInt() and 128 != 0)
            } catch (_: SocketTimeoutException) { } catch (_: SocketException) { if (!socket.isClosed) evidence("Discovery receive socket failure"); break }
            catch (error: Exception) { evidence("Discovery payload: ${error.javaClass.simpleName}") }
        }, "bas2-discovery-receive").apply { isDaemon = true; start() }
    }
    override fun close() = synchronized(gate) {
        if (closed) return@synchronized
        closed = true; worker.shutdownNow(); listener?.close(); sockets.values.forEach { it.close() }; sockets.clear()
        lock?.let { if(it.isHeld) it.release() }
        Unit
    }
}
