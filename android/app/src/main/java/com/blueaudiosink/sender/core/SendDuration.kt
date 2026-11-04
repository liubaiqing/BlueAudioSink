package com.blueaudiosink.sender.core

import java.util.Locale

/** Monotonic, pauseable elapsed time for one capture session. */
internal class SendDuration(private val now: () -> Long) {
    private var accumulated = 0L
    private var started: Long? = null

    @Synchronized fun reset() { accumulated = 0; started = null }
    @Synchronized fun setActive(active: Boolean) {
        if (active && started == null) started = now()
        if (!active) {
            started?.let { accumulated += (now() - it).coerceAtLeast(0) }
            started = null
        }
    }
    @Synchronized fun milliseconds(): Long = accumulated +
        (started?.let { (now() - it).coerceAtLeast(0) } ?: 0)

    companion object {
        fun format(seconds: Long): String {
            val s = seconds.coerceAtLeast(0)
            return if (s < 3600) String.format(Locale.US, "%02d:%02d", s / 60, s % 60)
            else String.format(Locale.US, "%02d:%02d:%02d", s / 3600, s / 60 % 60, s % 60)
        }
    }
}
