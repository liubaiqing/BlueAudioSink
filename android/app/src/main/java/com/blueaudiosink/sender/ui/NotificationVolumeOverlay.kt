package com.blueaudiosink.sender.ui

import android.app.KeyguardManager
import android.content.Context
import android.graphics.PixelFormat
import android.provider.Settings
import android.view.Gravity
import android.view.WindowManager
import android.widget.Toast
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleRegistry
import androidx.lifecycle.setViewTreeLifecycleOwner
import androidx.savedstate.SavedStateRegistryOwner
import androidx.savedstate.SavedStateRegistryController
import androidx.savedstate.setViewTreeSavedStateRegistryOwner
import com.blueaudiosink.sender.R
import com.blueaudiosink.sender.core.LinkController
import com.blueaudiosink.sender.core.LinkPhase
import kotlin.math.roundToInt

/** Owned by the capture service. Window operations run on its main thread. */
internal class NotificationVolumeOverlay(private val context: Context) {
    private val windows = context.getSystemService(WindowManager::class.java)
    private var panel: ComposeView? = null
    private var owner: PanelOwner? = null

    fun show() {
        if (LinkController.state.value.phase != LinkPhase.STREAMING) return
        if (context.getSystemService(KeyguardManager::class.java)?.isKeyguardLocked == true) return
        if (!Settings.canDrawOverlays(context)) {
            dismiss()
            Toast.makeText(context, R.string.overlay_volume_permission, Toast.LENGTH_LONG).show()
            return
        }
        if (panel != null) return
        val manager = windows ?: return
        val shadowInsetDp = 14
        val viewOwner = PanelOwner()
        val view = ComposeView(context).apply {
            setViewTreeLifecycleOwner(viewOwner)
            setViewTreeSavedStateRegistryOwner(viewOwner)
            setContent {
                val state by LinkController.volumeState.collectAsState()
                LaunchedEffect(state.phase) {
                    if (state.phase != LinkPhase.STREAMING) dismiss()
                }
                BlueAudioSinkTheme {
                    // The window needs transparent space around the card for its shadow.
                    Surface(
                        modifier = Modifier.padding(shadowInsetDp.dp),
                        shape = MaterialTheme.shapes.extraLarge,
                        shadowElevation = 4.dp,
                    ) {
                        Column(Modifier.padding(start = 20.dp, end = 20.dp, top = 12.dp, bottom = 8.dp)) {
                            ReceiverVolumeControl(state, { percent ->
                                val current = LinkController.state.value
                                if (current.phase == LinkPhase.STREAMING &&
                                    current.pcConnected && current.remoteVolumeReady) {
                                    LinkController.setRemoteVolume(percent)
                                }
                            })
                            TextButton(onClick = ::dismiss, modifier = Modifier.align(Alignment.End)) {
                                Text(stringResource(R.string.action_done))
                            }
                        }
                    }
                }
            }
        }
        val width = minOf(dp(320 + 2 * shadowInsetDp),
            (context.resources.displayMetrics.widthPixels - dp(8)).coerceAtLeast(1))
        val params = WindowManager.LayoutParams(
            width, WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
            y = dp(24 - shadowInsetDp)
            title = "BlueAudioSink volume"
        }
        try {
            manager.addView(view, params)
        } catch (_: WindowManager.BadTokenException) {
            viewOwner.destroy()
            Toast.makeText(context, R.string.overlay_volume_unavailable, Toast.LENGTH_SHORT).show()
            return
        } catch (_: SecurityException) {
            viewOwner.destroy()
            Toast.makeText(context, R.string.overlay_volume_permission, Toast.LENGTH_LONG).show()
            return
        }
        panel = view
        owner = viewOwner
    }

    fun dismiss() {
        val view = panel ?: return
        panel = null
        owner?.destroy()
        owner = null
        view.disposeComposition()
        try {
            windows?.removeViewImmediate(view)
        } catch (_: IllegalArgumentException) {
            // The window may already have been removed after permission was revoked.
        }
    }

    private fun dp(value: Int): Int = (value * context.resources.displayMetrics.density).roundToInt()

    private class PanelOwner : SavedStateRegistryOwner {
        override val lifecycle = LifecycleRegistry(this)
        private val state = SavedStateRegistryController.create(this)
        override val savedStateRegistry get() = state.savedStateRegistry

        init {
            state.performAttach()
            state.performRestore(null)
            lifecycle.currentState = Lifecycle.State.RESUMED
        }

        fun destroy() {
            lifecycle.currentState = Lifecycle.State.DESTROYED
        }
    }
}
