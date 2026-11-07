package com.blueaudiosink.sender.core

import android.content.Context
import android.content.SharedPreferences
import android.os.Build
import org.json.JSONArray
import java.util.UUID
import java.util.concurrent.Executors

internal object Diagnostics {
    val runId: String = UUID.randomUUID().toString().replace("-", "")
    private var connectionContext = emptyMap<String,String>()
    @Synchronized fun connection(fields: Map<String,String>) { connectionContext = fields.toMap() }
    private fun context(fields: Map<String,String>): org.json.JSONObject {
        val result = org.json.JSONObject().put("appRunId",runId)
        (connectionContext + fields).entries.take(12).forEach { result.put(it.key.take(48),it.value.take(256)) }
        while(result.toString().toByteArray(Charsets.UTF_8).size > 1800) {
            val key = result.keys().asSequence().maxByOrNull { result.optString(it).length } ?: break
            val value = result.optString(key); if(value.length < 16) result.remove(key) else result.put(key,value.take(value.length/2))
        }
        return result
    }
    private data class Entry(val row: JSONArray, var pending: Boolean)
    internal data class Batch(val sequence: Int, val payload: ByteArray, val ids: Set<String>)

    private val entries = ArrayDeque<Entry>()
    private var preferences: SharedPreferences? = null
    private var sequence = UUID.randomUUID().hashCode()
    private val worker = Executors.newSingleThreadExecutor()

    @Synchronized
    fun attach(context: Context) {
        if (preferences != null) return
        val prefs = context.getSharedPreferences("connection_diagnostics", Context.MODE_PRIVATE)
        preferences = prefs
        runCatching {
            val saved = JSONArray(prefs.getString("entries", "[]"))
            for (index in maxOf(0, saved.length() - 4096) until saved.length()) {
                val entry = saved.getJSONArray(index)
                entries.addLast(Entry(entry.getJSONArray(0), entry.getBoolean(1)))
            }
        }
        val version = runCatching { context.packageManager.getPackageInfo(context.packageName, 0).longVersionCode }.getOrDefault(0)
        record("App.Start", version, Build.VERSION.SDK_INT.toLong())
        val name = runCatching { context.packageManager.getPackageInfo(context.packageName, 0).versionName }.getOrNull()
        if (name != null && name.matches(Regex("[A-Za-z0-9.-]{1,30}"))) record("App.Version.$name")
    }

    fun recordAsync(code: String, a: Long, b: Long, c: Long) {
        worker.execute { record(code, a, b, c) }
    }

    @Synchronized
    fun record(code: String, a: Long = 0, b: Long = 0, c: Long = 0) {
        if (preferences == null) return
        val id = UUID.randomUUID().toString().replace("-", "")
        entries.addLast(Entry(JSONArray().put(id).put(System.currentTimeMillis()).put(code).put(a).put(b).put(c).put(context(emptyMap())), true))
        prune()
        persist()
    }

    @Synchronized
    fun batch(): Batch? {
        val pending = entries.asSequence().filter { it.pending }.take(6).toList()
        if (pending.isEmpty()) return null
        val rows = JSONArray()
        pending.forEach { rows.put(it.row) }
        return Batch(++sequence, rows.toString().toByteArray(Charsets.UTF_8), pending.map { it.row.getString(0) }.toSet())
    }

    @Synchronized
    fun acknowledge(batch: Batch) {
        entries.forEach { if (it.row.getString(0) in batch.ids) it.pending = false }
        persist()
    }

    @Synchronized
    fun export(): String = entries.joinToString(separator = "\n", postfix = "\n") { entry ->
        val row = entry.row
        org.json.JSONObject().put("source", "Android").put("id", row.getString(0))
            .put("phoneUnixMs", row.getLong(1)).put("event", row.getString(2))
            .put("context", row.optJSONObject(6)).put("a", row.getLong(3)).put("b", row.getLong(4)).put("c", row.getLong(5)).toString()
    }

    fun recordContext(code: String, fields: Map<String, String>) {
        val at = System.currentTimeMillis()
        val captured = synchronized(this) { context(fields) }
        worker.execute { synchronized(this) {
            if (preferences == null) return@synchronized
            entries.addLast(Entry(JSONArray().put(UUID.randomUUID().toString().replace("-", ""))
                .put(at).put(code.take(96)).put(0).put(0).put(0).put(captured), true))
            prune(); persist()
        } }
    }
    private fun prune() {
        val cutoff = System.currentTimeMillis() - 7L*24*60*60*1000
        while (entries.isNotEmpty() && (entries.size > 4096 || entries.first().row.optLong(1) < cutoff)) entries.removeFirst()
        var bytes = entries.sumOf { it.row.toString().toByteArray(Charsets.UTF_8).size + 16 }
        while (entries.isNotEmpty() && bytes > 2*1024*1024) bytes -= entries.removeFirst().row.toString().toByteArray(Charsets.UTF_8).size + 16
    }
    private fun persist() {
        val saved = JSONArray()
        entries.forEach { saved.put(JSONArray().put(it.row).put(it.pending)) }
        preferences?.edit()?.putString("entries", saved.toString())?.apply()
    }
}
