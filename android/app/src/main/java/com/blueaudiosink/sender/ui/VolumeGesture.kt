package com.blueaudiosink.sender.ui

import kotlin.math.roundToInt

/** Local presentation only: network acknowledgement never moves a finger in progress. */
internal data class VolumeGesture(val draft: Float?=null,val dragging: Boolean=false) {
    fun drag(value: Float)=VolumeGesture(value.coerceIn(0f,100f),true)
    fun finish()=copy(dragging=false)
    fun reconcile(authority: Int?,ready: Boolean): VolumeGesture =
        if(!ready || (!dragging && draft?.roundToInt()==authority)) VolumeGesture() else this
    fun shown(authority: Int?)=draft ?: (authority ?: 0).toFloat()
}
