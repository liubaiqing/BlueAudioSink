package com.blueaudiosink.sender.net.bas2

import java.io.Closeable
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

internal enum class ConnectionPhase { STOPPED, FINDING, CONTROL, IDENTITY, VERIFYING, READY, STREAMING, RECOVERING, FAILED }
internal data class ConnectionSnapshot(
    val phase: ConnectionPhase = ConnectionPhase.STOPPED,
    val attemptId: String? = null,
    val route: PlannedRoute? = null,
    val deviceId: String? = null,
    val deviceName: String? = null,
    val sessionId: String? = null,
    val usingTcp: Boolean = false,
    val error: String? = null,
)

internal interface SessionConnection : RouteProbe {
    val deviceId: String
    val deviceName: String
    val sessionId: String
    val usingTcp: Boolean
    fun start()
    fun stop()
    fun heartbeat()
    fun sendAudio(payload: ByteArray, timestampUs: Int, discontinuity: Boolean)
}

/** Owns session decisions; callbacks must enqueue UI/log work and return promptly. */
internal class SessionCoordinator(
    private val factory: (PlannedRoute, String?, (ConnectionPhase) -> Unit) -> SessionConnection,
    private val changed: (ConnectionSnapshot) -> Unit,
    private val diagnostic: (String, Map<String, String>) -> Unit = { _, _ -> },
    private val retryJitter: () -> Long = { (Math.random() * 251).toLong() },
    private val controlPort: Int = 45678,
) : Closeable {
    private val owner = Executors.newSingleThreadScheduledExecutor { runnable -> Thread(runnable, "bas2-session").apply { isDaemon = true } }
    private val rounds = Executors.newSingleThreadExecutor { runnable -> Thread(runnable, "bas2-connection-round").apply { isDaemon = true } }
    private val closed = AtomicBoolean()
    private val mediaFailureQueued = AtomicBoolean()
    private var locals = emptyList<LocalRoute>()
    private var addresses = emptyList<String>()
    private var expectedId: String? = null
    private var targetSelected = false
    private val wantSending = AtomicBoolean()
    private val targetIntent = AtomicLong()
    private var attempt: String? = null
    private var active: SelectedRoute<SessionConnection>? = null
    @Volatile private var race: RouteRace<SessionConnection>? = null
    @Volatile private var pcm: SessionConnection? = null
    @Volatile private var interruptible: SessionConnection? = null
    @Volatile var snapshot = ConnectionSnapshot(); private set
    private var retry: ScheduledFuture<*>? = null
    private var retryCount = 0
    private var lastSuccessfulPath: String? = null

    init {
        owner.scheduleWithFixedDelay({
            val current = active
            if (current != null && !closed.get()) {
                try { current.connection.heartbeat() }
                catch (error: Exception) { recover(error) }
            }
        }, 2, 2, TimeUnit.SECONDS)
    }

    private fun post(work: () -> Unit) {
        if (!closed.get()) runCatching { owner.execute { if (!closed.get()) work() } }
    }

    fun networkChanged(routes: List<LocalRoute>) = post {
        locals = routes.toList()
        val current = active
        if (current != null && RoutePlanner.stillPresent(current.route.local, locals)) return@post
        if (targetSelected) {
            if (current != null) recover(java.io.IOException("Active interface lost"), immediate = true)
            else begin()
        }
    }

    fun selectDevice(deviceId: String?, knownAddresses: List<String>) {
        val intent = targetIntent.incrementAndGet()
        // Interrupt pending reads immediately; state transitions remain on owner.
        pcm = null
        race?.close()
        interruptible?.close()
        post {
            if (targetIntent.get() != intent) return@post
            release()
            expectedId = deviceId
            addresses = knownAddresses.filter { RoutePlanner.ipv4(it) != null }.distinct().take(32)
            targetSelected = true
            retryCount = 0
            begin()
        }
    }

    fun rememberAddresses(deviceId: String, additional: List<String>) = post {
        if (targetSelected && expectedId == deviceId) {
            val updated = (addresses + additional.filter { RoutePlanner.ipv4(it) != null }).distinct().take(32)
            if (updated != addresses) { addresses = updated; if (active == null && race == null) begin() }
        }
    }

    fun setSending(enabled: Boolean) {
        wantSending.set(enabled)
        if (!enabled) pcm = null
        post {
            if (enabled != wantSending.get()) return@post
            val current = active ?: return@post
            try {
                if (enabled && snapshot.phase != ConnectionPhase.STREAMING) {
                    current.connection.start()
                    pcm = current.connection
                    publish(ConnectionPhase.STREAMING)
                } else if (!enabled && snapshot.phase == ConnectionPhase.STREAMING) {
                    current.connection.stop()
                    publish(ConnectionPhase.READY)
                }
            } catch (error: Exception) { recover(error) }
        }
    }

    fun disconnect() {
        targetIntent.incrementAndGet()
        wantSending.set(false)
        pcm = null
        race?.close()
        interruptible?.close()
        post {
            targetSelected = false
            addresses = emptyList()
            release()
            publish(ConnectionPhase.STOPPED)
        }
    }

    fun sendAudio(payload: ByteArray, timestampUs: Int, discontinuity: Boolean) {
        val connection = pcm ?: return
        try { connection.sendAudio(payload, timestampUs, discontinuity) }
        catch (error: Exception) {
            if (mediaFailureQueued.compareAndSet(false, true)) post {
                mediaFailureQueued.set(false)
                if (active?.connection === connection) recover(error)
            }
        }
    }

    private fun begin() {
        if (!targetSelected || closed.get()) return
        release()
        val id = UUID.randomUUID().toString().replace("-", "")
        val intent = targetIntent.get()
        attempt = id
        val candidates = RoutePlanner.candidates(locals, addresses, lastSuccessfulPath, addresses.firstOrNull(), controlPort)
        if (candidates.isEmpty()) {
            publish(ConnectionPhase.FINDING, "No LAN route or known address")
            scheduleRetry()
            return
        }
        publish(ConnectionPhase.CONTROL)
        val identity = expectedId
        val round = RouteRace<SessionConnection> { route ->
            factory(route, identity) { phase -> post { if (attempt == id && active == null) publish(phase) } }
        }
        race = round
        rounds.execute {
            try {
                val winner = round.select(candidates) { failure -> post {
                    if (attempt == id) diagnostic("Bas2.RouteFailed", mapOf("attemptId" to id, "pathId" to failure.route.key,
                        "errorType" to failure.error.javaClass.simpleName, "error" to failure.error.message.orEmpty()))
                } }
                if (closed.get()) winner.connection.close()
                else {
                    // A queued result always closes itself if a later attempt has taken ownership.
                    try { owner.execute {
                        if (closed.get() || attempt != id || targetIntent.get() != intent) winner.connection.close()
                        else accept(winner)
                    } } catch (_: java.util.concurrent.RejectedExecutionException) { winner.connection.close() }
                }
            } catch (error: Exception) { post {
                if (attempt == id) {
                    race = null
                    publish(ConnectionPhase.FAILED, error.message)
                    scheduleRetry()
                }
            } }
        }
    }

    private fun accept(winner: SelectedRoute<SessionConnection>) {
        race = null
        if (!RoutePlanner.stillPresent(winner.route.local, locals)) {
            winner.connection.close()
            begin()
            return
        }
        active = winner
        interruptible = winner.connection
        expectedId = winner.connection.deviceId
        lastSuccessfulPath = winner.route.key
        retryCount = 0
        publish(ConnectionPhase.READY)
        if (wantSending.get()) {
            try {
                winner.connection.start()
                pcm = winner.connection
                publish(ConnectionPhase.STREAMING)
            } catch (error: Exception) { recover(error) }
        }
    }

    private fun recover(error: Exception, immediate: Boolean = false) {
        release()
        if (closed.get()) return
        publish(ConnectionPhase.RECOVERING, "${error.javaClass.simpleName}: ${error.message}")
        if (immediate) begin() else scheduleRetry()
    }

    private fun scheduleRetry() {
        if (!targetSelected || closed.get()) return
        val seconds = intArrayOf(1, 2, 4, 8, 15)[retryCount.coerceAtMost(4)]
        retryCount++
        retry?.cancel(false)
        retry = owner.schedule({ if (!closed.get()) begin() }, seconds * 1000L + retryJitter().coerceIn(0, 250), TimeUnit.MILLISECONDS)
    }

    private fun release() {
        pcm = null
        attempt = null
        retry?.cancel(false)
        retry = null
        race?.close()
        race = null
        active?.connection?.close()
        active = null
        interruptible = null
    }

    private fun publish(phase: ConnectionPhase, error: String? = null) {
        if (closed.get()) return
        val current = active
        snapshot = ConnectionSnapshot(phase, attempt, current?.route, current?.connection?.deviceId,
            current?.connection?.deviceName, current?.connection?.sessionId, current?.connection?.usingTcp ?: false, error)
        runCatching { changed(snapshot) }
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        pcm = null
        race?.close()
        interruptible?.close()
        owner.execute { release() }
        owner.shutdown()
        rounds.shutdownNow()
    }
}
