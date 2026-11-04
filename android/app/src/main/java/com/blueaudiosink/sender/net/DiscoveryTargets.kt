package com.blueaudiosink.sender.net

import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

internal object DiscoveryTargets {
    data class Lan(val ip: ByteArray, val prefix: Int)
    fun lans(): List<Lan> = try {
        NetworkInterface.getNetworkInterfaces().toList().filter {
            it.isUp && !it.isLoopback && !it.isPointToPoint &&
                !it.name.startsWith("rmnet") && !it.name.startsWith("ccmni")
        }.flatMap { nic -> nic.interfaceAddresses.mapNotNull { a ->
            val ip = a.address
            if (ip is Inet4Address && !ip.isLinkLocalAddress && a.networkPrefixLength.toInt() in 1..30)
                Lan(ip.address,a.networkPrefixLength.toInt()) else null
        } }
    } catch (_: Exception) { emptyList() }
    private fun number(b: ByteArray): Long = b.fold(0L) { v, n -> (v shl 8) or (n.toLong() and 255) }
    private fun mask(prefix: Int): Long = (0xffffffffL shl (32-prefix)) and 0xffffffffL
    fun signature(): String = lans().map { "${number(it.ip)}/${it.prefix}" }.sorted().joinToString(",")
    fun addresses(): Set<String> = buildSet {
        add("255.255.255.255")
        lans().forEach { lan ->
            val value = (number(lan.ip) and mask(lan.prefix)) or (mask(lan.prefix) xor 0xffffffffL)
            add((3 downTo 0).joinToString(".") { ((value shr (it*8)) and 255).toString() })
        }
    }
    fun isLocal(host: String): Boolean = try {
        if (!Regex("[0-9.]+").matches(host)) false else {
            val ip = InetAddress.getByName(host)
            ip is Inet4Address && lans().any { (number(ip.address) and mask(it.prefix)) == (number(it.ip) and mask(it.prefix)) }
        }
    } catch (_: Exception) { false }
}
