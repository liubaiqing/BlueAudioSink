package com.blueaudiosink.sender.capture

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.AccessibilityServiceInfo
import android.content.Context
import android.util.Log
import android.view.KeyEvent
import android.view.accessibility.AccessibilityEvent
import com.blueaudiosink.sender.core.LinkController
import com.blueaudiosink.sender.core.LinkPhase
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch

class SpeakerGuardService : AccessibilityService() {
    private val scope = MainScope()
    private var guarding = false
    private var consumedDown = false
    private var monitor: Job? = null

    override fun onServiceConnected() {
        super.onServiceConnected()
        setRequested(preferences(this).getBoolean(ENABLED, false))
        _available.value = true
        Log.i(TAG, "Accessibility service connected; enabled=${requested.value}")
        monitor?.cancel()
        monitor = scope.launch {
            combine(LinkController.state, requested) { state, enabled ->
                enabled && state.phase == LinkPhase.STREAMING && state.pcConnected && !state.resolving
            }.collect { active ->
                updateFilter(active)
                if (active == guarding) return@collect
                guarding = active
                Log.i(TAG, "Guard active=$active")
            }
        }
    }

    private fun updateFilter(active: Boolean = guarding) {
        val info = serviceInfo ?: return
        val flag = AccessibilityServiceInfo.FLAG_REQUEST_FILTER_KEY_EVENTS
        val flags = if (active || consumedDown) info.flags or flag else info.flags and flag.inv()
        if (flags != info.flags) {
            info.flags = flags
            serviceInfo = info
            Log.i(TAG, "Requested key filtering=${flags and flag != 0}")
        }
    }

    override fun onKeyEvent(event: KeyEvent): Boolean {
        if (event.keyCode != KeyEvent.KEYCODE_VOLUME_UP) return false
        if (event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0) consumedDown = guarding
        val handled = consumedDown
        if (event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0 && guarding)
            Log.i(TAG, "Volume-up key intercepted")
        if (event.action == KeyEvent.ACTION_UP) {
            consumedDown = false
            updateFilter()
        }
        return handled
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) = Unit
    override fun onInterrupt() = Unit

    override fun onDestroy() {
        scope.cancel()
        _available.value = false
        super.onDestroy()
    }

    companion object {
        private const val TAG = "BasSpeakerGuard"
        private const val ENABLED = "speaker_guard"
        private val requested = MutableStateFlow(false)
        private val _available = MutableStateFlow(false)
        internal val available = _available.asStateFlow()

        private fun preferences(context: Context) =
            context.getSharedPreferences("speaker_guard", Context.MODE_PRIVATE)

        internal fun isEnabled(context: Context) = preferences(context).getBoolean(ENABLED, false)

        internal fun setEnabled(context: Context, enabled: Boolean) {
            preferences(context).edit().putBoolean(ENABLED, enabled).apply()
            setRequested(enabled)
        }

        private fun setRequested(enabled: Boolean) { requested.value = enabled }
    }
}
