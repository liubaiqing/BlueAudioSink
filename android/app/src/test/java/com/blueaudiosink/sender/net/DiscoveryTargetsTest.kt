package com.blueaudiosink.sender.net

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DiscoveryTargetsTest {
    @Test
    fun excludesVpnAndCellularWhileKeepingWifiEthernetAndHotspot() {
        listOf("tun0", "TAP1", "wg0", "ipsec2", "ppp0", "vti0", "rmnet_data0", "ccmni0").forEach {
            assertFalse(it, DiscoveryTargets.eligibleInterface(it))
        }
        listOf("wlan0", "wlan1", "ap0", "swlan0", "eth0", "rndis0").forEach {
            assertTrue(it, DiscoveryTargets.eligibleInterface(it))
        }
    }
}
