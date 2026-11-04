package com.blueaudiosink.sender.net

import org.junit.Test
import org.junit.Assert.*
import com.blueaudiosink.sender.core.DeviceRecords
import com.blueaudiosink.sender.core.SavedDevice

class IdentityResolverTest {
    private val id="a".repeat(32)
    private val other="b".repeat(32)
    private fun reply(query: ByteArray, identity: String=id): ByteArray = ByteArray(54).also {
        query.copyInto(it,0,0,16);identity.toByteArray().copyInto(it,16);PacketCodec.putIntLE(it,48,45679);"PC".toByteArray().copyInto(it,52)
    }
    @Test fun knownIdentityFindsNewAddress() {
        var query=ByteArray(0);var result: DiscoveredDevice?=null
        val r=IdentityResolver({q,_->query=q.copyOf()},{result=it},{_,_->})
        r.select(id,listOf("192.168.0.1"),0);r.tick(0)
        r.reply("192.168.233.8",45678,reply(query),1)
        assertEquals("192.168.233.8",result?.ip);assertEquals(id,result?.deviceId);assertTrue(r.ready)
    }
    @Test fun wrongIdentityAtOldAddressCannotConnect() {
        var query=ByteArray(0);var connected=false
        val r=IdentityResolver({q,_->query=q.copyOf()},{connected=true},{_,_->})
        r.select(id,listOf("192.168.0.1"),0);r.tick(0);r.reply("192.168.0.1",45678,reply(query,other),1)
        assertFalse(connected);assertTrue(r.waiting)
    }
    @Test fun newAttemptRejectsOldNonceAndWrongPort() {
        var query=ByteArray(0);var connected=false
        val r=IdentityResolver({q,_->query=q.copyOf()},{connected=true},{_,_->})
        r.select(id,listOf("192.168.0.1"),0);r.tick(0);val old=query.copyOf()
        r.restart(100);r.tick(100);r.reply("192.168.0.1",45678,reply(old),101)
        r.reply("192.168.0.1",45679,reply(query),101);assertFalse(connected)
        r.reply("192.168.0.1",45678,reply(query),101);assertTrue(connected)
    }
    @Test fun initialManualPairingOnlyAcceptsChosenAddress() {
        var query=ByteArray(0);var connected=false
        val r=IdentityResolver({q,host->assertEquals("192.168.0.1",host);query=q.copyOf()},{connected=true},{_,_->})
        r.select(null,listOf("192.168.0.1"),0);r.tick(0)
        r.reply("192.168.0.2",45678,reply(query),1);assertFalse(connected)
        r.reply("192.168.0.1",45678,reply(query),1);assertTrue(connected)
    }
    @Test fun offerIsOnlyHintAndTimeoutRemainsDisconnected() {
        var count=0;var error: String?=null
        val r=IdentityResolver({_,_->count++},{fail("OFFER cannot authorize connection")},{_,e->error=e})
        r.select(id,listOf("192.168.0.1"),0);r.hint(DiscoveredDevice("PC","192.168.0.2",45679,0,id))
        r.tick(0);val initial=count;r.tick(100);assertEquals(initial,count)
        r.tick(10000);assertFalse(r.ready);assertFalse(r.waiting);assertNotNull(error)
    }
    @Test fun clearRejectsAllLateReplies() {
        var query=ByteArray(0)
        val r=IdentityResolver({q,_->query=q.copyOf()},{fail("deleted attempt revived")},{_,_->})
        r.select(id,listOf("192.168.0.1"),0);r.tick(0);r.clear();r.reply("192.168.0.1",45678,reply(query),1);r.tick(20000)
        assertFalse(r.ready)
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
    @Test fun matchingOfferAfterTimeoutRestartsChallengeButNotAfterExplicitClear() {
        var queries=0;var connected=false
        val r=IdentityResolver({_,_->queries++},{connected=true},{_,_->})
        r.select(id,listOf("192.168.233.8"),0);r.tick(0);r.tick(10000)
        assertFalse(r.waiting)
        r.hint(DiscoveredDevice("PC","192.168.0.8",45679,11000,other));assertFalse(r.waiting)
        r.hint(DiscoveredDevice("PC","192.168.0.8",45679,11000,id));assertTrue(r.waiting)
        r.tick(11000);assertTrue(queries>2);assertFalse(connected)
        r.clear();r.hint(DiscoveredDevice("PC","192.168.0.8",45679,12000,id));assertFalse(r.waiting)
    }

}
