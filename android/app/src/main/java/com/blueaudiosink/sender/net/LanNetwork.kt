package com.blueaudiosink.sender.net

import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import java.net.Inet4Address

/** Select an underlying LAN even when VPN is the default network. */
internal object LanNetwork {
    fun vpnSignature(manager: ConnectivityManager?): String =
        manager?.allNetworks?.filter {
            manager.getNetworkCapabilities(it)?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true
        }?.map { it.networkHandle }?.sorted()?.joinToString(",").orEmpty()

    fun select(manager: ConnectivityManager?, current: Network?): Network? {
        if (manager == null) return null
        val candidates = manager.allNetworks.filter { network ->
            val capabilities = manager.getNetworkCapabilities(network) ?: return@filter false
            val physical = capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) &&
                !capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN) &&
                (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
                    capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))
            physical && manager.getLinkProperties(network)?.linkAddresses?.any {
                it.address is Inet4Address && !it.address.isLinkLocalAddress
            } == true
        }
        val coveredAddresses = candidates.flatMap { network ->
            manager.getLinkProperties(network)?.linkAddresses.orEmpty()
                .map { it.address.address.toList() }
        }.toSet()
        // Tethering interfaces often have no Network object. Binding the upstream
        // Wi-Fi Network would prevent the same socket from reaching hotspot clients.
        if (DiscoveryTargets.lans().any { it.ip.toList() !in coveredAddresses }) return null
        return candidates.firstOrNull { it == current } ?: candidates.minByOrNull { it.networkHandle }
    }
}
