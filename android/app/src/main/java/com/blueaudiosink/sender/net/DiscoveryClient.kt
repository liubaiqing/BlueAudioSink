package com.blueaudiosink.sender.net

import android.os.SystemClock

/** 局域网中发现到的一台电脑。 */
data class DiscoveredDevice(
    val name: String,
    val ip: String,
    val port: Int,
    val lastSeenMs: Long,
    val deviceId: String? = null,
)

/**
 * 发现列表维护器（协议第 8 节）。
 *
 * 发现socket与解析由BAS2 LanDiscovery独立持有，
 * 本类按设备身份去重，旧包按 IP 区分、6 秒过期、把最新列表推给界面。
 */
class DiscoveryClient(private val onDevices: (List<DiscoveredDevice>) -> Unit) {

    companion object {
        const val DISCOVERY_PORT = 45_678
        private const val DEVICE_TTL_MS = 6_000L
        private const val SWEEP_INTERVAL_MS = 1_000L
    }

    private val lock = Any()
    private val seen = LinkedHashMap<String, DiscoveredDevice>()

    @Volatile
    private var running = false

    private var sweepThread: Thread? = null

    fun start() {
        if (running) return
        running = true
        synchronized(lock) { seen.values.toList() }.let(onDevices)
        sweepThread = Thread({ sweepLoop() }, "discovery-ttl").apply {
            isDaemon = true
            start()
        }
    }

    fun stop() {
        running = false
        sweepThread?.interrupt()
        sweepThread = null
    }

    /** 收到 OFFER 时由 LanDiscovery 的接收线程调用。 */
    fun offer(device: DiscoveredDevice) {
        val snapshot = synchronized(lock) {
            seen[device.deviceId ?: device.ip] = device
            seen.values.sortedBy { it.name }
        }
        onDevices(snapshot)
    }

    fun snapshot(): List<DiscoveredDevice> = synchronized(lock) { seen.values.toList() }

    private fun sweepLoop() {
        while (running) {
            try {
                Thread.sleep(SWEEP_INTERVAL_MS)
            } catch (_: InterruptedException) {
                break
            }
            val now = SystemClock.elapsedRealtime()
            val snapshot = synchronized(lock) {
                val removed = seen.entries.removeAll { now - it.value.lastSeenMs > DEVICE_TTL_MS }
                if (removed) seen.values.toList() else null
            }
            if (snapshot != null) onDevices(snapshot)
        }
    }
}
