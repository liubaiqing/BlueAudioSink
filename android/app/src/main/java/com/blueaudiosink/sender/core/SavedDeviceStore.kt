package com.blueaudiosink.sender.core

import android.content.Context
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject

class SavedDeviceStore(context: Context) {
    companion object { const val MAX_DEVICES=5 }
    private val lock=Any()
    private val prefs=context.applicationContext.getSharedPreferences("blueaudio_sender",Context.MODE_PRIVATE)
    fun load(): List<SavedDevice> = synchronized(lock) { read() }
    fun remember(name: String, ip: String, port: Int, id: String, legacyKey: String?): List<SavedDevice> = synchronized(lock) {
        val next=DeviceRecords.remember(read(),SavedDevice(name,ip,port,System.currentTimeMillis(),id),legacyKey)
        write(next); next
    }
    fun mergeLegacy(id: String, keys: Set<String>): List<SavedDevice> = synchronized(lock) {
        val next=DeviceRecords.mergeLegacy(read(),id,keys); write(next); next
    }
    fun remove(key: String): List<SavedDevice> = synchronized(lock) {
        val next=read().filterNot { it.key==key };write(next);next
    }
    private fun read(): List<SavedDevice> = try {
        val array=JSONArray(prefs.getString("saved_devices",null) ?: "[]")
        DeviceRecords.limit((0 until array.length()).mapNotNull { i ->
            val o=array.optJSONObject(i) ?: return@mapNotNull null
            val ip=o.optString("ip").trim(); if(ip.isEmpty()) return@mapNotNull null
            val id=o.optString("deviceId").takeIf(DeviceRecords::validId)
            val addresses=o.optJSONArray("addresses")
            val history=(listOf(ip)+(0 until (addresses?.length() ?: 0)).mapNotNull { addresses?.optString(it)?.takeIf { a->a.isNotBlank() } }).distinct().take(8)
            SavedDevice(o.optString("name").ifBlank { ip },ip,o.optInt("port",45679).takeIf { it in 1..65535 } ?: 45679,o.optLong("lastUsed",0),id,history)
        }.distinctBy { it.key })
    } catch(e: Exception) { Log.w("BlueAudioSink","saved device data invalid",e);emptyList() }
    private fun write(list: List<SavedDevice>) {
        val array=JSONArray()
        list.forEach { d -> array.put(JSONObject().apply {
            put("name",d.name);put("ip",d.ip);put("port",d.port);put("lastUsed",d.lastUsedMs)
            d.deviceId?.let { put("deviceId",it) };put("addresses",JSONArray(d.addresses))
        }) }
        prefs.edit().putString("saved_devices",array.toString()).apply()
    }
}
