package com.blueaudiosink.sender

import com.blueaudiosink.sender.core.Diagnostics

import android.app.Activity
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.produceState
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.repeatOnLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.remember
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.rememberSaveable
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.distinctUntilChanged
import com.blueaudiosink.sender.core.SenderUiState
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.graphics.toArgb
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import com.blueaudiosink.sender.capture.AudioCaptureService
import com.blueaudiosink.sender.capture.MediaProjectionHelper
import com.blueaudiosink.sender.core.LinkController
import com.blueaudiosink.sender.ui.BlueAudioSinkTheme
import com.blueaudiosink.sender.ui.SenderScreen
import com.blueaudiosink.sender.ui.useDarkTheme

/**
 * 发送端主界面。
 *
 * 开始发送的三步（顺序不可换）：
 * 1. 申请运行时权限（录音、通知）；
 * 2. 弹系统录屏授权对话框（每次都必须由用户确认）；
 * 3. 带着授权结果启动 mediaProjection 类型的前台服务。
 */
class MainActivity : ComponentActivity() {

    private var pendingStart = false

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
            val granted = result.values.all { it }
            if (!pendingStart) return@registerForActivityResult
            if (granted) {
                requestConsent()
            } else {
                pendingStart = false
                LinkController.postMessage(getString(R.string.error_permissions_required))
            }
        }

    private val consentLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            pendingStart = false
            val data = result.data
            Diagnostics.record("Capture.Consent", if (result.resultCode == Activity.RESULT_OK && data != null) 1 else 0)
            if (result.resultCode == Activity.RESULT_OK && data != null) {
                val state = LinkController.state.value
                val intent = AudioCaptureService.startIntent(
                    context = this,
                    resultCode = result.resultCode,
                    resultData = data,
                    tierName = state.tier.name,
                    deviceName = deviceName(),
                )
                ContextCompat.startForegroundService(this, intent)
            } else {
                LinkController.postMessage(getString(R.string.error_consent_cancelled))
            }
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        LinkController.attach(this)
        LinkController.setDeviceName(deviceName())
        setContent {
            val dark = useDarkTheme()
            BlueAudioSinkTheme(darkTheme = dark) {
                val barColor = MaterialTheme.colorScheme.surface.toArgb()
                SideEffect {
                    window.statusBarColor = barColor
                    window.navigationBarColor = barColor
                    WindowCompat.getInsetsController(window, window.decorView).apply {
                        isAppearanceLightStatusBars = !dark
                        isAppearanceLightNavigationBars = !dark
                    }
                }
                var page by rememberSaveable { mutableStateOf("main") }
                val visibleState=remember(page) {
                    if(page=="main") LinkController.state else LinkController.state.map {
                        SenderUiState(message=it.message,messageToken=it.messageToken)
                    }.distinctUntilChanged()
                }
                val state by produceState(LinkController.state.value,visibleState) {
                    lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) { visibleState.collect { value=it } }
                }
                SenderScreen(
                    page=page, onPageChange={page=it},
                    state = state,
                    onStart = ::handleStart,
                    onStop = {
                        ContextCompat.startForegroundService(
                            this,
                            AudioCaptureService.stopIntent(this),
                        )
                    },
                    onConnect = { ip, port -> LinkController.connect(ip, port) },
                    onConnectManual = { ip -> LinkController.connect(ip) },
                    onRemoveSaved = { ip -> LinkController.removeSavedDevice(ip) },
                    onTierChange = { tier -> LinkController.setTier(tier) },
                    onRemoteVolumeChange = { LinkController.setRemoteVolume(it) },
                    onDismissMessage = { LinkController.clearMessage() },
                    onRefreshDiscovery = { LinkController.restartDiscovery(this) },
                )
            }
        }
    }

    override fun onStart() {
        super.onStart()
        // 绑定 45678 属于网络操作，不能在主线程做
        val context = applicationContext
        LinkController.startDiscovery(context)
    }

    override fun onStop() {
        super.onStop()
        LinkController.stopDiscovery()
    }

    private fun handleStart() {
        if (!LinkController.state.value.pcConnected || LinkController.state.value.resolving) {
            LinkController.postMessage(getString(R.string.error_no_target))
            return
        }
        val missing = MediaProjectionHelper.missingPermissions(this)
        if (missing.isNotEmpty()) {
            pendingStart = true
            permissionLauncher.launch(missing.toTypedArray())
            return
        }
        requestConsent()
    }

    private fun requestConsent() {
        val intent = MediaProjectionHelper.createConsentIntent(this)
        if (intent == null) {
            pendingStart = false
            LinkController.postMessage(getString(R.string.error_projection_unavailable))
            return
        }
        consentLauncher.launch(intent)
    }

    private fun deviceName(): String =
        Build.MODEL?.takeIf { it.isNotBlank() } ?: getString(R.string.default_device_name)
}
