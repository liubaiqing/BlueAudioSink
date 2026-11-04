package com.blueaudiosink.sender.core

import org.junit.Assert.assertEquals
import org.junit.Test

class SendDurationTest {
    @Test fun pausesDuringRecoveryAndResetsForNewSession() {
        var now = 0L
        val duration = SendDuration { now }
        duration.setActive(true)
        now = 3500
        duration.setActive(true) // duplicate START must not reset elapsed time
        duration.setActive(false)
        now = 15000 // identity lookup must not count
        assertEquals(3500L, duration.milliseconds())
        duration.setActive(true)
        now = 17500
        duration.setActive(false)
        now = 20000
        assertEquals(6000L, duration.milliseconds())
        duration.reset()
        assertEquals(0L, duration.milliseconds())
    }
    @Test fun formatsAcrossHourBoundary() {
        assertEquals("00:00", SendDuration.format(0))
        assertEquals("01:05", SendDuration.format(65))
        assertEquals("59:59", SendDuration.format(3599))
        assertEquals("01:00:00", SendDuration.format(3600))
        assertEquals("25:00:01", SendDuration.format(90001))
    }
}
