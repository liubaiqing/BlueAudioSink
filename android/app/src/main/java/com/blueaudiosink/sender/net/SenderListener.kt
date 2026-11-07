package com.blueaudiosink.sender.net

/** Transport-independent application callbacks. */
interface SenderListener {
    fun onStage(stage: String?, compatible: Boolean) {}
    fun onAudioReceipt(sent: Long, received: Long) {}
    fun onResolving(active: Boolean, error: String?)
    fun onResolved(device: DiscoveredDevice)
    fun onOffer(device: DiscoveredDevice)
    fun onPing(t1Us: Int, receivedAtUs: Int)
    fun onRemoteVolume(percent: Int?, ready: Boolean)
    fun onError(message: String)
}
