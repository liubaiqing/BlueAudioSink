package com.blueaudiosink.sender.net.bas2

import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

class RoutePlannerTest {
    private val wifi = LocalRoute("wlan0", "192.168.0.116", 24, 101, 1)
    private val hotspot = LocalRoute("ap0", "192.168.233.231", 24, null, 1)

    @Test fun hotspotAndUpstreamHaveIndependentRoutes() {
        val routes = RoutePlanner.candidates(listOf(wifi, hotspot), listOf("192.168.233.68", "192.168.0.165"), null, "192.168.233.68")
        assertEquals(4, routes.size)
        assertEquals(hotspot, routes.first().local)
        assertTrue(RoutePlanner.sameSubnet(routes.first().local, routes.first().remote))
        assertNull(routes.first().local.networkHandle)
        assertEquals(wifi, routes[1].local)
    }

    @Test fun lastSuccessfulPathRanksFirstAndUnrelatedChangesDoNotInvalidate() {
        val previous = PlannedRoute(wifi, "192.168.0.165")
        val routes = RoutePlanner.candidates(listOf(wifi, hotspot), listOf("192.168.233.68", "192.168.0.165"), previous.key, "192.168.233.68")
        assertEquals(previous.key, routes.first().key)
        assertTrue(RoutePlanner.stillPresent(wifi, listOf(wifi.copy(generation = 8), hotspot)))
        assertFalse(RoutePlanner.stillPresent(wifi, listOf(wifi.copy(networkHandle = 102), hotspot)))
        assertFalse(RoutePlanner.stillPresent(wifi, listOf(hotspot)))
    }

    @Test fun malformedAndUnboundedCandidatesAreRejected() {
        assertNull(RoutePlanner.ipv4("192.168.1.256"))
        assertNull(RoutePlanner.ipv4("192.168.1.-1"))
        assertNull(RoutePlanner.ipv4("example.com"))
        assertEquals(1, RoutePlanner.candidates(listOf(wifi, wifi), listOf(wifi.address, "bad", "192.168.0.165"), null, null).size)
        assertEquals(64, RoutePlanner.candidates(listOf(wifi), (1..200).map { "10.0.0.$it" }, null, null).size)
    }

    private class Probe(val work: (Probe) -> Unit) : RouteProbe {
        val closeSignal = CountDownLatch(1)
        val closed = AtomicInteger()
        override fun verify() = work(this)
        override fun close() { if (closed.compareAndSet(0, 1)) closeSignal.countDown() }
    }

    @Test fun raceOwnsLosersAndTransfersOnlyWinner() {
        val all = java.util.Collections.synchronizedList(mutableListOf<Probe>())
        val opened = CountDownLatch(3)
        val routes = (1..6).map { PlannedRoute(wifi, "192.168.0.$it") }
        val race = RouteRace { route ->
            Probe { probe ->
                opened.countDown()
                assertTrue(opened.await(2, TimeUnit.SECONDS))
                if (route.remote != routes[1].remote) {
                    probe.closeSignal.await(2, TimeUnit.SECONDS)
                    throw IOException("cancelled")
                }
            }.also { all.add(it) }
        }
        val selected = race.select(routes)
        assertEquals(routes[1], selected.route)
        assertEquals(3, all.size)
        assertEquals(0, selected.connection.closed.get())
        assertTrue(all.filter { it !== selected.connection }.all { it.closed.get() == 1 })
        selected.connection.close()
    }

    @Test fun failedCandidatesAdvanceAndCancellationClosesPendingSockets() {
        val failures = mutableListOf<RouteFailure>()
        val failedRace = RouteRace { Probe { throw IOException("unreachable") } }
        try { failedRace.select((1..5).map { PlannedRoute(wifi, "192.168.0.$it") }, failure = failures::add); fail() }
        catch (_: IOException) { }
        assertEquals(5, failures.size)

        val entered = CountDownLatch(1)
        val probe = Probe { item -> entered.countDown(); item.closeSignal.await(); throw IOException("closed") }
        val race = RouteRace { probe }
        val worker = Executors.newSingleThreadExecutor()
        try {
            val result = worker.submit<Boolean> {
                try { race.select(listOf(PlannedRoute(wifi, "192.168.0.165"))); false }
                catch (_: Exception) { true }
            }
            assertTrue(entered.await(2, TimeUnit.SECONDS))
            race.close()
            assertTrue(result.get(2, TimeUnit.SECONDS))
            assertEquals(1, probe.closed.get())
        } finally { race.close(); worker.shutdownNow() }
    }
}
