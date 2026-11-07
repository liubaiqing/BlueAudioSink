package com.blueaudiosink.sender.core

data class SavedDevice(
    val name: String, val ip: String, val port: Int, val lastUsedMs: Long,
    val deviceId: String? = null, val addresses: List<String> = listOf(ip),
) { val key: String get() = deviceId?.let { "id:$it" } ?: "ip:$ip" }

object DeviceRecords {
    fun validId(id: String) = id.length == 32 && id.all { it in '0'..'9' || it in 'a'..'f' }
    fun limit(records: List<SavedDevice>): List<SavedDevice> {
        val ordered=records.sortedByDescending { it.lastUsedMs }
        return (ordered.filter { it.deviceId!=null }.take(5)+ordered.filter { it.deviceId==null }.take(5)).sortedByDescending { it.lastUsedMs }
    }
    fun remember(records: List<SavedDevice>, next: SavedDevice, legacyKey: String? = null): List<SavedDevice> {
        require(next.deviceId != null)
        val same=records.filter { it.deviceId==next.deviceId || (it.deviceId==null && it.key==legacyKey) }
        val history=(listOf(next.ip)+same.flatMap { it.addresses+it.ip }).distinct().take(8)
        return limit(listOf(next.copy(addresses=history))+records.filterNot { it in same })
    }
    fun mergeLegacy(records: List<SavedDevice>, targetId: String, legacyKeys: Set<String>): List<SavedDevice> {
        val target=records.firstOrNull { it.deviceId==targetId } ?: return records
        val old=records.filter { it.deviceId==null && it.key in legacyKeys }
        val updated=target.copy(addresses=(listOf(target.ip)+target.addresses+old.flatMap { it.addresses+it.ip }).distinct().take(8))
        return records.filterNot { it in old }.map { if(it.deviceId==targetId) updated else it }
    }
}
