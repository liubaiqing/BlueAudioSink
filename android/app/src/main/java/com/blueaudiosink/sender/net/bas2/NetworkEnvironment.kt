package com.blueaudiosink.sender.net.bas2

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import com.blueaudiosink.sender.net.DiscoveryTargets
import java.io.Closeable
import java.net.Inet4Address
import java.net.NetworkInterface
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

internal data class NetworkSnapshot(
    val generation: Long,
    val routes: List<LocalRoute>,
    val networks: Map<Long, Network>,
    val vpnHandles: Set<Long>,
    val enumerationError: String? = null,
) {
    fun candidate(route: PlannedRoute): RouteCandidate = RouteCandidate(
        route.local.interfaceName, route.local.networkHandle?.let(networks::get),
        java.net.InetAddress.getByName(route.local.address) as Inet4Address,
        java.net.InetAddress.getByName(route.remote) as Inet4Address, route.port, generation,
    )
}

/** Callbacks are debounced; slow enumeration also finds tethering interfaces without a Network. */
internal class NetworkEnvironment(context: Context, private val changed: (NetworkSnapshot) -> Unit) : Closeable {
    private val manager = context.applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    private val executor = Executors.newSingleThreadScheduledExecutor { runnable ->
        Thread(runnable, "bas2-network-environment").apply { isDaemon = true }
    }
    private val gate = Any()
    private var pending: ScheduledFuture<*>? = null
    @Volatile private var closed = false
    @Volatile var snapshot = NetworkSnapshot(0, emptyList(), emptyMap(), emptySet()); private set
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = schedule()
        override fun onLost(network: Network) = schedule()
        override fun onLinkPropertiesChanged(network: Network, properties: LinkProperties) = schedule()
        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) = schedule()
    }
    private var registered = false

    init {
        try {
            manager.registerNetworkCallback(NetworkRequest.Builder().clearCapabilities().build(), callback)
            registered = true
            executor.execute(::refresh)
            executor.scheduleWithFixedDelay(::refresh, 2, 2, TimeUnit.SECONDS)
        } catch (error: Exception) { close(); throw error }
    }

    private fun schedule() = synchronized(gate) {
        if (!closed) {
            pending?.cancel(false)
            pending = executor.schedule(::refresh, 200, TimeUnit.MILLISECONDS)
        }
        Unit
    }

    private fun refresh() {
        if (closed) return
        val previous = snapshot
        val networks = linkedMapOf<Long, Network>()
        val locals = linkedMapOf<String, LocalRoute>()
        val vpnHandles = mutableSetOf<Long>()
        val vpnInterfaces = mutableSetOf<String>()
        var failure: String? = null
        try {
            for (network in manager.allNetworks) {
                val caps = manager.getNetworkCapabilities(network) ?: continue
                val properties = manager.getLinkProperties(network) ?: continue
                if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) {
                    vpnHandles.add(network.networkHandle)
                    properties.interfaceName?.let(vpnInterfaces::add)
                    continue
                }
                if (!caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) ||
                    (!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) && !caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))) continue
                val name = properties.interfaceName ?: continue
                networks[network.networkHandle] = network
                properties.linkAddresses.forEach { link ->
                    val address = link.address
                    if (address is Inet4Address && !address.isLoopbackAddress && !address.isLinkLocalAddress && link.prefixLength in 1..32) {
                        val ip = address.hostAddress ?: return@forEach
                        locals["$name/$ip"] = LocalRoute(name, ip, link.prefixLength, network.networkHandle, 0)
                    }
                }
            }
            NetworkInterface.getNetworkInterfaces()?.toList().orEmpty().forEach { nic ->
                if (!nic.isUp || nic.isLoopback || nic.isPointToPoint || nic.name in vpnInterfaces || !DiscoveryTargets.eligibleInterface(nic.name)) return@forEach
                for (link in nic.interfaceAddresses) {
                    val address = link.address
                    val prefix = link.networkPrefixLength.toInt()
                    if (address !is Inet4Address || address.isLoopbackAddress || address.isLinkLocalAddress || prefix !in 1..32) continue
                    val ip = address.hostAddress ?: continue
                    locals.putIfAbsent("${nic.name}/$ip", LocalRoute(nic.name, ip, prefix, null, 0))
                }
            }
        } catch (error: Exception) { failure = "${error.javaClass.simpleName}: ${error.message}" }
        // Do not publish partial inventories: a transient enumeration error is not interface loss.
        if (failure != null) {
            val next = previous.copy(enumerationError = failure)
            snapshot = next
            if (!closed && next != previous) changed(next)
            return
        }
        val ordered = locals.values.sortedWith(compareBy({ it.interfaceName }, { it.address }))
        val oldNormalized = previous.routes.map { it.copy(generation = 0) }
        if (ordered == oldNormalized && vpnHandles == previous.vpnHandles && previous.enumerationError == null) return
        val generation = previous.generation + 1
        val next = NetworkSnapshot(generation, ordered.map { it.copy(generation = generation) }, networks.toMap(), vpnHandles.toSet())
        snapshot = next
        if (!closed) changed(next)
    }

    override fun close() {
        synchronized(gate) {
            if (closed) return
            closed = true
            pending?.cancel(false)
        }
        if (registered) runCatching { manager.unregisterNetworkCallback(callback) }
        executor.shutdownNow()
    }
}
