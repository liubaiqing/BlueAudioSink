package com.blueaudiosink.sender.net.bas2

import android.app.Activity
import android.os.Bundle
import android.util.Log
import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Included only by the isolated probe Gradle init script, never in the product APK. */
class Bas2ProbeActivity : Activity() {
    override fun onCreate(state: Bundle?) {
        super.onCreate(state)
        val host = intent.getStringExtra("host") ?: return finish()
        val seconds = intent.getIntExtra("seconds", 10).coerceIn(1, 3600)
        val music = intent.getBooleanExtra("music", false)
        Thread({ runProbe(host, seconds, music); runOnUiThread { finish() } }, "bas2-physical-probe").start()
    }

    private fun runProbe(host: String, seconds: Int, music: Boolean) {
        var environment: NetworkEnvironment? = null
        var coordinator: SessionCoordinator? = null
        var frames = 0
        val output = StringBuilder()
        fun record(message: String) { output.appendLine(message); Log.i("BAS2Probe", message) }
        try {
            val reference = if (music) {
                val file = File(getExternalFilesDir(null), "reference.pcm")
                check(file.length() in (seconds * 200L * 960)..(10L * 1024 * 1024)) { "Invalid music PCM length" }
                file.readBytes().also { record("referenceSha256=" + java.security.MessageDigest.getInstance("SHA-256").digest(it).joinToString("") { b -> "%02x".format(b.toInt() and 255) }) }
            } else null
            val inventoryReady = CountDownLatch(1)
            environment = NetworkEnvironment(this) { if (it.routes.isNotEmpty()) inventoryReady.countDown() }
            check(inventoryReady.await(5, TimeUnit.SECONDS)) { "No physical LAN inventory" }
            val network = environment
            val streaming = CountDownLatch(1)
            val stopAcknowledged = CountDownLatch(1)
            val stopping = java.util.concurrent.atomic.AtomicBoolean()
            val instance = ControlConnection.instanceId()
            val session = SessionCoordinator({ route, identity, phase ->
                AndroidSessionConnection(network.snapshot.candidate(route), identity, instance, "BAS2-probe-phone", phase)
            }, { snapshot ->
                record("phase=${snapshot.phase} path=${snapshot.route?.key} tcp=${snapshot.usingTcp} error=${snapshot.error}")
                if (snapshot.phase == ConnectionPhase.STREAMING) streaming.countDown()
                if (snapshot.phase == ConnectionPhase.READY && stopping.get()) stopAcknowledged.countDown()
            }, { event, fields -> record("$event $fields") }, controlPort = 45778)
            coordinator = session
            session.networkChanged(network.snapshot.routes)
            // Test ports do not overlap the running product.
            session.selectDevice("a".repeat(32), listOf(host))
            session.setSending(true)
            check(streaming.await(18, TimeUnit.SECONDS)) { "Transport not ready" }
            val begin = System.nanoTime()
            repeat(seconds * 200) { sequence ->
                val payload = reference?.copyOfRange(sequence * 960, (sequence + 1) * 960)
                    ?: ByteArray(960) { index -> ((sequence * 31L + index) % 251).toByte() }
                session.sendAudio(payload, (System.nanoTime() / 1000).toInt(), !music && sequence == 0)
                frames++
                val left = begin + frames * 5_000_000L - System.nanoTime()
                if (left > 0) TimeUnit.NANOSECONDS.sleep(left)
            }
            check(session.snapshot.phase == ConnectionPhase.STREAMING) { "Stream lost: ${session.snapshot}" }
            record("PASS generatedFrames=$frames transport=${if (session.snapshot.usingTcp) "tcp" else "udp"} path=${session.snapshot.route?.key}")
            // Keep the verified session alive for finite output drain. Immediate process close
            // intentionally means disconnect and would discard the receiver's remaining buffer.
            Thread.sleep(30)
            stopping.set(true)
            session.setSending(false)
            check(stopAcknowledged.await(3, TimeUnit.SECONDS)) { "Stop was not acknowledged" }
            Thread.sleep(600)
            record("STOP acknowledged; finite output drain completed")
        } catch (error: Exception) { record("FAIL frames=$frames ${error.javaClass.name}: ${error.message}") }
        finally {
            coordinator?.close()
            environment?.close()
            File(getExternalFilesDir(null), "result.txt").writeText(output.toString())
        }
    }
}
