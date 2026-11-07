package com.blueaudiosink.sender.capture

import com.blueaudiosink.sender.core.Diagnostics

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.blueaudiosink.sender.MainActivity
import com.blueaudiosink.sender.R
import com.blueaudiosink.sender.core.LinkController
import com.blueaudiosink.sender.ui.NotificationVolumeOverlay
import kotlinx.coroutines.*

/**
 * 采集前台服务。
 *
 * Android 14 起 mediaProjection 类型的授权只能由“已进入前台”的服务消费：
 * 因此顺序必须是 startForeground(...) → getMediaProjection(...) → AudioRecord。
 * 每次开始都需要用户重新确认系统授权（Token 一次性）。
 */
class AudioCaptureService : Service() {
    private val serviceScope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)

    companion object {
        private const val CHANNEL_ID = "capture_status"
        private const val NOTIFICATION_ID = 1001

        const val ACTION_START = "com.blueaudiosink.sender.action.START"
        const val ACTION_STOP = "com.blueaudiosink.sender.action.STOP"
        private const val ACTION_VOLUME = "com.blueaudiosink.sender.action.VOLUME"

        private const val EXTRA_RESULT_CODE = "extra_result_code"
        private const val EXTRA_RESULT_DATA = "extra_result_data"
        private const val EXTRA_TIER = "extra_tier"
        private const val EXTRA_DEVICE_NAME = "extra_device_name"

        fun startIntent(
            context: Context,
            resultCode: Int,
            resultData: Intent,
            tierName: String,
            deviceName: String,
        ): Intent = Intent(context, AudioCaptureService::class.java).apply {
            action = ACTION_START
            putExtra(EXTRA_RESULT_CODE, resultCode)
            putExtra(EXTRA_RESULT_DATA, resultData)
            putExtra(EXTRA_TIER, tierName)
            putExtra(EXTRA_DEVICE_NAME, deviceName)
        }

        fun stopIntent(context: Context): Intent =
            Intent(context, AudioCaptureService::class.java).apply { action = ACTION_STOP }
    }

    private var projection: MediaProjection? = null
    private var recorder: PcmRecorder? = null

    private val mainHandler = Handler(Looper.getMainLooper())

    private val TAG = "BlueAudioSink"

    private val projectionCallback = object : MediaProjection.Callback() {
        override fun onStop() {
            Diagnostics.record("Capture.ProjectionStopped")
            // 用户点了系统「停止投放」，或系统回收了授权
            releaseCapture()
            LinkController.postMessage(getString(R.string.error_projection_stopped))
            LinkController.stopStreaming()
            stopSelf()
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        LinkController.attach(applicationContext)
        createNotificationChannel()
        serviceScope.launch {
            LinkController.notificationState.collect { (phase,connected,stage) ->
                if(recorder!=null) updateNotification(when {
                    stage!=null -> stage
                    !connected -> "等待电脑连接"
                    phase==com.blueaudiosink.sender.core.LinkPhase.STREAMING -> getString(R.string.notification_streaming)
                    else -> "已连接，等待发送"
                })
            }
        }
    }

    private val volumeOverlay by lazy { NotificationVolumeOverlay(this) }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                startForegroundCompat(getString(R.string.notification_capturing))
                val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
                val resultData: Intent? = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent::class.java)
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableExtra(EXTRA_RESULT_DATA)
                }
                val tier = BufferTier.fromName(intent.getStringExtra(EXTRA_TIER))
                val deviceName = intent.getStringExtra(EXTRA_DEVICE_NAME)
                    ?: getString(R.string.default_device_name)
                if (resultData == null) {
                    failAndStop(getString(R.string.error_no_projection))
                } else {
                    // 只清理上一次的采集会话，绝不能在这里 stopForeground：
                    // 否则会丢掉 mediaProjection 前台类型，导致 getMediaProjection 抛 SecurityException
                    releaseProjection()
                    startCapture(resultCode, resultData, tier, deviceName)
                }
            }

            ACTION_VOLUME -> {
                if (recorder != null) volumeOverlay.show() else stopSelf(startId)
            }

            ACTION_STOP -> {
                stopCapture()
                stopSelf()
            }

            else -> stopSelf()
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        serviceScope.cancel()
        releaseCapture()
        super.onDestroy()
    }

    // ---------------------------------------------------------------- 采集

    private fun startCapture(resultCode: Int, resultData: Intent, tier: BufferTier, deviceName: String) {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            failAndStop(getString(R.string.error_record_permission))
            return
        }
        val manager = getSystemService(MediaProjectionManager::class.java)
        val mediaProjection = try {
            manager?.getMediaProjection(resultCode, resultData)
        } catch (e: Exception) {
            Log.w(TAG, "getMediaProjection failed", e)
            null
        }
        if (mediaProjection == null) {
            Log.w(TAG, "getMediaProjection returned null, resultCode=" + resultCode)
            failAndStop(getString(R.string.error_projection_denied))
            return
        }
        Log.i(TAG, "MediaProjection acquired, starting recorder")
        mediaProjection.registerCallback(projectionCallback, mainHandler)
        projection = mediaProjection

        val pcmRecorder = PcmRecorder(
            projection = mediaProjection,
            tier = tier,
            onPacket = { buffer, length, timestampUs, discontinuity ->
                LinkController.sendAudio(buffer, length, timestampUs, discontinuity)
            },
            onError = { code -> mainHandler.post { handleRecorderError(code) } },
            onTierApplied = { applied -> LinkController.tierApplied(applied) },
        )
        if (!pcmRecorder.start()) {
            Log.w(TAG, "PcmRecorder.start() returned false")
            releaseCapture()
            stopSelf()
            return
        }
        Log.i(TAG, "recorder started, sending START")
        recorder = pcmRecorder
        LinkController.tierChanged = pcmRecorder::setTier
        if (!LinkController.startStreaming(deviceName)) { releaseCapture(); stopSelf(); return }
        updateNotification(getString(R.string.notification_streaming))
    }

    private fun handleRecorderError(code: Int) {
        val text = when (code) {
            PcmRecorder.ERROR_PERMISSION -> getString(R.string.error_record_permission)
            PcmRecorder.ERROR_BUILD -> getString(R.string.error_recorder_build)
            PcmRecorder.ERROR_START -> getString(R.string.error_recorder_start)
            else -> getString(R.string.error_recorder_read)
        }
        failAndStop(text)
    }

    private fun failAndStop(message: String) {
        Log.w(TAG, "capture failed: " + message)
        Diagnostics.record("Capture.Failed")
        LinkController.fail(message)
        releaseCapture()
        stopSelf()
    }

    private fun stopCapture() {
        LinkController.stopStreaming()
        releaseCapture()
    }

    /** 只释放采集对象，保留前台服务状态（启动新会话时使用）。 */
    private fun releaseProjection() {
        LinkController.tierChanged=null
        volumeOverlay.dismiss()
        recorder?.stop()
        recorder = null
        projection?.let {
            try {
                it.unregisterCallback(projectionCallback)
                it.stop()
            } catch (_: Exception) {
            }
        }
        projection = null
    }

    /** 释放采集对象并退出前台状态（停止/失败/销毁时使用）。 */
    private fun releaseCapture() {
        releaseProjection()
        try {
            stopForeground(STOP_FOREGROUND_REMOVE)
        } catch (_: Exception) {
        }
        val manager = getSystemService(NotificationManager::class.java)
        manager?.cancel(NOTIFICATION_ID)
    }

    // ---------------------------------------------------------------- 通知

    private fun createNotificationChannel() {
        val manager = getSystemService(NotificationManager::class.java) ?: return
        val channel = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.notification_channel_name),
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = getString(R.string.notification_channel_desc)
            setShowBadge(false)
        }
        manager.createNotificationChannel(channel)
    }

    private fun startForegroundCompat(text: String) {
        startForeground(
            NOTIFICATION_ID,
            buildNotification(text),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION,
        )
    }

    private fun updateNotification(text: String) {
        val manager = getSystemService(NotificationManager::class.java) ?: return
        manager.notify(NOTIFICATION_ID, buildNotification(text, canAdjustVolume = true))
    }

    private fun buildNotification(text: String, canAdjustVolume: Boolean = false): Notification {
        val contentAction = if (canAdjustVolume) {
            PendingIntent.getService(this, 2,
                Intent(this, AudioCaptureService::class.java).setAction(ACTION_VOLUME),
                PendingIntent.FLAG_IMMUTABLE)
        } else {
            PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java),
                PendingIntent.FLAG_IMMUTABLE)
        }
        val stopAction = PendingIntent.getService(
            this,
            1,
            stopIntent(this),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_audio)
            .setContentTitle(getString(when {
                !canAdjustVolume -> R.string.notification_preparing
                !LinkController.state.value.pcConnected || LinkController.state.value.connectionStage!=null -> R.string.notification_recovering
                else -> R.string.notification_title
            }))
            .setContentText(text)
            .setOngoing(true)
            .setSilent(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setContentIntent(contentAction)
            .apply {
                if (canAdjustVolume) {
                    addAction(0, getString(R.string.action_adjust_volume), contentAction)
                }
            }
            .addAction(0, getString(R.string.action_stop), stopAction)
            .build()
    }
}
