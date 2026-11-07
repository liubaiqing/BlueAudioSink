package com.blueaudiosink.sender.core

import org.junit.Test
import org.junit.Assert.*

class DeviceRecordsTest {
    private val id="a".repeat(32)
    private val other="b".repeat(32)
    @Test fun identityFormatRemainsStableAndRejectsInvalidRecords() {
        assertTrue(DeviceRecords.validId(id))
        assertTrue(DeviceRecords.validId("0123456789abcdef".repeat(2)))
        listOf("", "a".repeat(31), "a".repeat(33), "A".repeat(32), "g".repeat(32)).forEach {
            assertFalse(it,DeviceRecords.validId(it))
        }
    }
    @Test fun sameIdentityMergesNetworksButSameNameDoesNotMergeComputers() {
        val first=SavedDevice("PC","192.168.0.1",45679,1,id)
        val second=SavedDevice("PC","192.168.0.1",45679,2,other)
        val records=DeviceRecords.remember(listOf(first,second),first.copy(ip="192.168.233.8",lastUsedMs=3))
        assertEquals(2,records.size);assertEquals(listOf("192.168.233.8","192.168.0.1"),records[0].addresses)
        assertEquals(other,records[1].deviceId)
    }
    @Test fun legacyMergeRequiresExplicitSelectionAndPreservesKnownIdentity() {
        val a=SavedDevice("PC","192.168.0.1",45679,1)
        val b=SavedDevice("PC","192.168.1.1",45679,2)
        val target=SavedDevice("PC","192.168.233.8",45679,3,id)
        val known=target.copy(deviceId=other)
        val records=DeviceRecords.mergeLegacy(listOf(a,b,target,known),id,setOf(a.key,known.key))
        assertEquals(3,records.size);assertTrue(records.any{it.key==b.key});assertTrue(records.any{it.deviceId==other})
        assertTrue(records.first{it.deviceId==id}.addresses.contains(a.ip))
    }
    @Test fun onlyExplicitLegacyUpgradeRemovesAddressRecordAndHistoryIsBounded() {
        val old=SavedDevice("PC","192.168.0.1",45679,1)
        var list=DeviceRecords.remember(listOf(old),old.copy(deviceId=id,lastUsedMs=2))
        assertEquals(2,list.size)
        list=DeviceRecords.remember(list,old.copy(deviceId=id,lastUsedMs=3),old.key)
        assertEquals(1,list.size)
        repeat(20) { list=DeviceRecords.remember(list,list[0].copy(ip="192.168.0.${it+2}",lastUsedMs=it+4L)) }
        assertEquals(8,list.single().addresses.size);assertEquals(8,list.single().addresses.distinct().size)
    }
}
