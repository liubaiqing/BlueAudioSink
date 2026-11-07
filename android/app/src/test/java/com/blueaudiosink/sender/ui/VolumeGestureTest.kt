package com.blueaudiosink.sender.ui
import org.junit.Assert.*
import org.junit.Test
class VolumeGestureTest {
    @Test fun delayedOrDifferentNetworkReplyDoesNotPullBackFinger() {
        val moving=VolumeGesture().drag(67f)
        assertEquals(67f,moving.reconcile(19,true).shown(19))
        val released=moving.finish()
        assertEquals(67f,released.reconcile(20,true).shown(20))
        assertNull(released.reconcile(67,true).draft)
    }
    @Test fun disconnectionClearsGestureAndReconnectionStartsAtAuthority() {
        val cleared=VolumeGesture().drag(94f).reconcile(null,false)
        assertNull(cleared.draft); assertFalse(cleared.dragging)
        assertEquals(19f,cleared.shown(19))
    }
}
