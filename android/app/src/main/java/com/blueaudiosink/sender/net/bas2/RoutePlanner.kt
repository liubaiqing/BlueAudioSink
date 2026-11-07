package com.blueaudiosink.sender.net.bas2

/** Pure route policy; Android Network objects are attached by NetworkEnvironment. */
internal data class LocalRoute(
    val interfaceName: String,
    val address: String,
    val prefix: Int,
    val networkHandle: Long?,
    val generation: Long,
)

internal data class PlannedRoute(val local: LocalRoute, val remote: String, val port: Int = 45678) {
    val key: String get() = "${local.interfaceName}/${local.address}/${local.networkHandle}/$remote:$port"
}

internal object RoutePlanner {
    fun ipv4(value: String): Long? {
        val parts = value.split('.')
        if (parts.size != 4) return null
        var result = 0L
        for (part in parts) {
            if (part.isEmpty() || part.length > 3 || part.any { it !in '0'..'9' }) return null
            val number = part.toIntOrNull() ?: return null
            if (number !in 0..255) return null
            result = (result shl 8) or number.toLong()
        }
        return result
    }

    fun sameSubnet(local: LocalRoute, remote: String): Boolean {
        if (local.prefix !in 1..32) return false
        val left = ipv4(local.address) ?: return false
        val right = ipv4(remote) ?: return false
        val mask = (0xffffffffL shl (32 - local.prefix)) and 0xffffffffL
        return left and mask == right and mask
    }

    fun candidates(locals: List<LocalRoute>, addresses: List<String>, lastSuccess: String?, explicit: String?): List<PlannedRoute> =
        locals.flatMap { local ->
            addresses.distinct().filter { ipv4(it) != null && it != local.address }
                .map { PlannedRoute(local, it) }
        }.distinctBy { it.key }.sortedWith(
            compareByDescending<PlannedRoute> { it.key == lastSuccess }
                .thenByDescending { sameSubnet(it.local, it.remote) }
                .thenByDescending { it.remote == explicit }
                .thenBy { it.key }
        ).take(64)

    /** Unrelated interfaces and a new global inventory generation cannot invalidate a live route. */
    fun stillPresent(route: LocalRoute, current: List<LocalRoute>): Boolean = current.any {
        it.interfaceName == route.interfaceName && it.address == route.address &&
            it.prefix == route.prefix && it.networkHandle == route.networkHandle
    }
}
