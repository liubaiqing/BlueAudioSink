package com.blueaudiosink.sender.core

import com.blueaudiosink.sender.capture.BufferTier
import com.blueaudiosink.sender.net.DiscoveredDevice

/** 界面三态：未连接 / 已连接 / 发送中。 */
enum class LinkPhase { IDLE, CONNECTED, STREAMING }

/** 单页 UI 的全部状态（由 [LinkController] 唯一持有）。 */
data class SenderUiState(
    val phase: LinkPhase = LinkPhase.IDLE,
    val resolving: Boolean = false,
    val targetDeviceId: String? = null,
    val targetName: String? = null,
    val targetIp: String? = null,
    val targetPort: Int = 45_679,
    val devices: List<DiscoveredDevice> = emptyList(),
    /** 已记住的设备，按最近使用倒序，最多 [SavedDeviceStore.MAX_DEVICES] 台。 */
    val savedDevices: List<SavedDevice> = emptyList(),
    /** 最近一次 PING/PONG 的往返时延，-1 表示还没测到。 */
    val rttMs: Int = -1,
    /** 探测丢包率（千分比），888 表示 88.8%。 */
    val lossPermille: Int = 0,
    val pcConnected: Boolean = false,
    val remoteVolume: Int? = null,
    val remoteVolumeReady: Boolean = false,
    val packetsSent: Long = 0,
    val sendingSeconds: Long = 0,
    val tier: BufferTier = BufferTier.BALANCED,
    val message: String? = null,
    val messageToken: Long = 0,
)
