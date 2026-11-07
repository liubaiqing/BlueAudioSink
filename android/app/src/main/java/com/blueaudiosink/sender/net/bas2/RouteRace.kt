package com.blueaudiosink.sender.net.bas2

import java.io.Closeable
import java.io.IOException
import java.util.concurrent.Callable
import java.util.concurrent.ExecutorCompletionService
import java.util.concurrent.Executors
import java.util.concurrent.Future
import java.util.concurrent.TimeUnit

internal interface RouteProbe : Closeable { fun verify() }
internal data class RouteFailure(val route: PlannedRoute, val error: Exception)
internal data class SelectedRoute<P : RouteProbe>(val route: PlannedRoute, val connection: P)

/** One round, at most three open probes. close() interrupts sockets, including losing probes. */
internal class RouteRace<P : RouteProbe>(private val factory: (PlannedRoute) -> P) : Closeable {
    private val gate = Any()
    private val opened = HashSet<P>()
    private val pool = Executors.newFixedThreadPool(3) { runnable ->
        Thread(runnable, "bas2-route-probe").apply { isDaemon = true }
    }
    private val futures = ArrayList<Future<SelectedRoute<P>>>()
    @Volatile private var closed = false
    private var used = false

    fun select(routes: List<PlannedRoute>, budgetMs: Long = 15_000, failure: (RouteFailure) -> Unit = {}): SelectedRoute<P> {
        synchronized(gate) { check(!used && !closed); used = true }
        val deadline = System.nanoTime() + budgetMs.coerceIn(1, 15_000) * 1_000_000
        val completion = ExecutorCompletionService<SelectedRoute<P>>(pool)
        val iterator = routes.take(64).iterator()
        var pending = 0
        fun launch() {
            val route = iterator.next()
            val future = completion.submit(Callable {
                var connection: P? = null
                try {
                    connection = factory(route)
                    synchronized(gate) {
                        if (closed) throw IOException("Connection attempt cancelled")
                        opened.add(connection)
                    }
                    connection.verify()
                    SelectedRoute(route, connection)
                } catch (error: Exception) {
                    connection?.let { item ->
                        synchronized(gate) { opened.remove(item) }
                        runCatching { item.close() }
                    }
                    throw ProbeFailure(route, error)
                }
            })
            synchronized(gate) { futures.add(future); if (closed) future.cancel(true) }
            pending++
        }
        try {
            repeat(minOf(3, routes.size, 64)) { launch() }
            while (pending > 0 && !closed) {
                val remaining = deadline - System.nanoTime()
                if (remaining <= 0) break
                val completed = completion.poll(remaining, TimeUnit.NANOSECONDS) ?: break
                pending--
                try {
                    val selected = completed.get()
                    synchronized(gate) {
                        if (closed) throw IOException("Connection attempt cancelled")
                        opened.remove(selected.connection) // transfer ownership to session
                        close()
                        return selected
                    }
                } catch (error: java.util.concurrent.ExecutionException) {
                    val cause = error.cause
                    if (cause is ProbeFailure) failure(RouteFailure(cause.route, cause.original))
                    else throw error
                }
                if (iterator.hasNext() && !closed) launch()
            }
            throw IOException(if (closed) "Connection attempt cancelled" else "No verified route within attempt budget")
        } finally { close() }
    }

    override fun close() {
        val pending: List<P>
        synchronized(gate) {
            if (closed) return
            closed = true
            pending = opened.toList()
            opened.clear()
            futures.forEach { it.cancel(true) }
            pool.shutdownNow()
        }
        pending.forEach { runCatching { it.close() } }
    }

    private class ProbeFailure(val route: PlannedRoute, val original: Exception) : Exception(original)
}
