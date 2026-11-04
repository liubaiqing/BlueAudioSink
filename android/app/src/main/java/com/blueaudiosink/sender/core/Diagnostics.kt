package com.blueaudiosink.sender.core

import android.content.Context
import android.content.SharedPreferences
import android.os.Build
import org.json.JSONArray
import java.util.UUID
import java.util.concurrent.Executors

internal object Diagnostics {
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
            for (index in maxOf(0, saved.length() - 96) until saved.length()) {
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
        entries.addLast(Entry(JSONArray().put(id).put(System.currentTimeMillis()).put(code).put(a).put(b).put(c), true))
        if (entries.size > 96) entries.removeFirst()
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
            .put("a", row.getLong(3)).put("b", row.getLong(4)).put("c", row.getLong(5)).toString()
    }

    private fun persist() {
        val saved = JSONArray()
        entries.forEach { saved.put(JSONArray().put(it.row).put(it.pending)) }
        preferences?.edit()?.putString("entries", saved.toString())?.apply()
    }
}
