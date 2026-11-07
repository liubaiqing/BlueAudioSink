package com.blueaudiosink.sender.core

import android.content.Context
import android.content.SharedPreferences
import android.os.Build
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** Only worker owns JSON and preferences. Callers enqueue or read immutable snapshots. */
internal object Diagnostics {
    val runId = UUID.randomUUID().toString().replace("-", "")
    @Volatile private var connectionContext = emptyMap<String,String>()
    fun connection(fields: Map<String,String>) { connectionContext = fields.toMap() }
    internal data class Batch(val sequence: Int, val payload: ByteArray, val ids: Set<String>)
    private data class Entry(val row: JSONArray, val bytes: Int, val export: String, var pending: Boolean)
    private val attached = AtomicBoolean()
    private val worker = Executors.newSingleThreadScheduledExecutor()
    private val entries = ArrayDeque<Entry>()
    private var bytes = 0
    private var preferences: SharedPreferences? = null
    private var sequence = 0
    private var persistScheduled = false
    @Volatile private var pendingBatch: Batch? = null
    @Volatile private var exportLines: List<String> = emptyList()

    fun attach(context: Context) {
        if (!attached.compareAndSet(false,true)) return
        val app = context.applicationContext
        worker.execute {
            preferences = app.getSharedPreferences("connection_diagnostics",Context.MODE_PRIVATE)
            runCatching {
                val saved = JSONArray(preferences?.getString("entries","[]"))
                for (index in maxOf(0,saved.length()-4096) until saved.length()) {
                    val value = saved.getJSONArray(index)
                    add(value.getJSONArray(0),value.getBoolean(1))
                }
            }
            prune(); publish()
            val info = runCatching { app.packageManager.getPackageInfo(app.packageName,0) }.getOrNull()
            append("App.Start",info?.longVersionCode ?: 0,Build.VERSION.SDK_INT.toLong(),0,emptyMap())
            info?.versionName?.takeIf { it.matches(Regex("[A-Za-z0-9.-]{1,30}")) }?.let { append("App.Version.$it",0,0,0,emptyMap()) }
        }
    }
    fun recordAsync(code: String,a: Long,b: Long,c: Long) = record(code,a,b,c)
    fun record(code: String,a: Long=0,b: Long=0,c: Long=0) {
        val at = System.currentTimeMillis(); val fields = connectionContext
        worker.execute { append(code,a,b,c,fields,at) }
    }
    fun recordContext(code: String,fields: Map<String,String>) {
        val at = System.currentTimeMillis(); val captured = connectionContext + fields
        worker.execute { append(code,0,0,0,captured,at) }
    }
    private fun append(code: String,a: Long,b: Long,c: Long,fields: Map<String,String>,at: Long=System.currentTimeMillis()) {
        if (preferences == null) return
        val context = JSONObject().put("appRunId",runId)
        fields.entries.take(10).forEach { context.put(it.key.take(48),it.value.take(128)) }
        add(JSONArray().put(UUID.randomUUID().toString().replace("-",""))
            .put(at).put(code.take(96)).put(a).put(b).put(c).put(context),true)
        prune(); publish(); schedulePersist()
    }
    private fun add(row: JSONArray,pending: Boolean) {
        val line = JSONObject().put("source","Android").put("id",row.getString(0)).put("phoneUnixMs",row.getLong(1))
            .put("event",row.getString(2)).put("a",row.getLong(3)).put("b",row.getLong(4)).put("c",row.getLong(5)).put("context",row.optJSONObject(6)).toString()
        val size = row.toString().toByteArray(Charsets.UTF_8).size+16
        entries.addLast(Entry(row,size,line,pending)); bytes += size
    }
    private fun prune() {
        val cutoff = System.currentTimeMillis()-7L*24*60*60*1000
        while(entries.isNotEmpty() && (entries.size>4096 || bytes>2*1024*1024 || entries.first().row.getLong(1)<cutoff)) bytes-=entries.removeFirst().bytes
    }
    private fun publish() {
        val rows=ArrayList<Entry>(); var encoded=ByteArray(0)
        for(entry in entries) {
            if(!entry.pending) continue
            val next=JSONArray(); rows.forEach { next.put(it.row) }; next.put(entry.row)
            val candidate=next.toString().toByteArray(Charsets.UTF_8)
            if(candidate.size>12000) break
            rows.add(entry); encoded=candidate
            if(rows.size==6) break
        }
        pendingBatch = if(rows.isEmpty()) null else Batch(++sequence,encoded,rows.map { it.row.getString(0) }.toSet())
        exportLines = entries.map { it.export }
    }
    fun batch(): Batch? = pendingBatch
    fun acknowledge(batch: Batch) { worker.execute {
        entries.forEach { if(it.row.getString(0) in batch.ids) it.pending=false }
        publish(); schedulePersist()
    } }
    fun export(): String = exportLines.joinToString("\n",postfix="\n")
    private fun schedulePersist() {
        if(persistScheduled) return
        persistScheduled=true
        worker.schedule({
            persistScheduled=false
            val saved = JSONArray(); entries.forEach { saved.put(JSONArray().put(it.row).put(it.pending)) }
            preferences?.edit()?.putString("entries",saved.toString())?.apply()
        },500,TimeUnit.MILLISECONDS)
    }
}
