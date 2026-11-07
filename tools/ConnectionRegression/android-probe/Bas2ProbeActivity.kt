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
        Thread({ runProbe(host, seconds); runOnUiThread { finish() } }, "bas2-physical-probe").start()
    }

    private fun runProbe(host: String, seconds: Int) {
        var environment: NetworkEnvironment? = null
        var coordinator: SessionCoordinator? = null
        var frames = 0
        val output = StringBuilder()
        fun record(message: String) { output.appendLine(message); Log.i("BAS2Probe", message) }
        try {
            val inventoryReady = CountDownLatch(1)
            environment = NetworkEnvironment(this) { if (it.routes.isNotEmpty()) inventoryReady.countDown() }
            check(inventoryReady.await(5, TimeUnit.SECONDS)) { "No physical LAN inventory" }
            val network = environment
            val streaming = CountDownLatch(1)
            val instance = ControlConnection.instanceId()
            val session = SessionCoordinator({ route, identity, phase ->
                AndroidSessionConnection(network.snapshot.candidate(route), identity, instance, "BAS2-probe-phone", phase)
            }, { snapshot ->
                record("phase=${snapshot.phase} path=${snapshot.route?.key} tcp=${snapshot.usingTcp} error=${snapshot.error}")
                if (snapshot.phase == ConnectionPhase.STREAMING) streaming.countDown()
            }, { event, fields -> record("$event $fields") }, controlPort = 45778)
            coordinator = session
            session.networkChanged(network.snapshot.routes)
            // Test ports do not overlap the running product.
            session.selectDevice("a".repeat(32), listOf(host))
            session.setSending(true)
            check(streaming.await(18, TimeUnit.SECONDS)) { "Transport not ready" }
            val begin = System.nanoTime()
            repeat(seconds * 200) { sequence ->
                val payload = ByteArray(960) { index -> ((sequence * 31L + index) % 251).toByte() }
                session.sendAudio(payload, (System.nanoTime() / 1000).toInt(), sequence == 0)
                frames++
                val left = begin + frames * 5_000_000L - System.nanoTime()
                if (left > 0) TimeUnit.NANOSECONDS.sleep(left)
            }
            check(session.snapshot.phase == ConnectionPhase.STREAMING) { "Stream lost: ${session.snapshot}" }
            record("PASS generatedFrames=$frames transport=${if (session.snapshot.usingTcp) "tcp" else "udp"} path=${session.snapshot.route?.key}")
            session.setSending(false)
        } catch (error: Exception) { record("FAIL frames=$frames ${error.javaClass.name}: ${error.message}") }
        finally {
            coordinator?.close()
            environment?.close()
            File(getExternalFilesDir(null), "result.txt").writeText(output.toString())
        }
    }
}
