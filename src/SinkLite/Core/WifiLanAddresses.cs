using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SinkLite.Core
{
    internal sealed class WifiLanAddress
    {
        public string InterfaceId;
        public bool Wireless;
        public IPAddress Address, Mask;
        public IPAddress[] Gateways = Array.Empty<IPAddress>();
    }
    internal static class WifiLanAddresses
    {
        internal static bool Eligible(NetworkInterfaceType type, string description)
        {
            if(type!=NetworkInterfaceType.Wireless80211 && type!=NetworkInterfaceType.Ethernet) return false;
            string text=(description ?? "").ToLowerInvariant();
            return !new[]{"vpn","tap-","wintun","wireguard","vmware","virtual","hyper-v","tunnel","loopback"}.Any(text.Contains);
        }
        public static WifiLanAddress[] Read()
        {
            var result=new List<WifiLanAddress>();
            try { foreach(var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                if(nic.OperationalStatus!=OperationalStatus.Up || !Eligible(nic.NetworkInterfaceType,nic.Description)) continue;
                try {
                    var properties=nic.GetIPProperties();
                    var gateways=properties.GatewayAddresses.Select(g=>g.Address).Where(Usable).ToArray();
                    foreach(var address in properties.UnicastAddresses)
                        if(Usable(address.Address) && address.DuplicateAddressDetectionState==DuplicateAddressDetectionState.Preferred)
                            result.Add(new WifiLanAddress {InterfaceId=nic.Id,Wireless=nic.NetworkInterfaceType==NetworkInterfaceType.Wireless80211,
                                Address=address.Address,Mask=address.IPv4Mask,Gateways=gateways});
                } catch(NetworkInformationException) { }
            } } catch(NetworkInformationException) { }
            return result.OrderBy(a=>a.InterfaceId,StringComparer.Ordinal).ThenBy(a=>a.Address.ToString(),StringComparer.Ordinal).ToArray();
        }
        private static bool Usable(IPAddress ip)
        {
            if(ip==null || ip.AddressFamily!=AddressFamily.InterNetwork) return false;
            var b=ip.GetAddressBytes();return b[0]!=0 && b[0]!=127 && b[0]<224 && !(b[0]==169&&b[1]==254);
        }
        internal static bool Contains(WifiLanAddress lan,IPAddress peer)
        {
            if(peer?.AddressFamily!=AddressFamily.InterNetwork || lan.Mask?.AddressFamily!=AddressFamily.InterNetwork) return false;
            byte[] a=lan.Address.GetAddressBytes(),b=peer.GetAddressBytes(),mask=lan.Mask.GetAddressBytes();
            for(int i=0;i<4;i++) if((a[i]&mask[i])!=(b[i]&mask[i])) return false;
            return true;
        }
        internal static bool OnLink(IEnumerable<WifiLanAddress> addresses,IPAddress peer) => addresses.Any(a=>Contains(a,peer));
        internal static string Choose(IEnumerable<WifiLanAddress> addresses,string freshPeer=null)
        {
            var values=addresses.OrderByDescending(a=>a.Wireless).ThenByDescending(a=>a.Gateways.Length>0).ToArray();
            if(IPEndPoint.TryParse(freshPeer,out var peer)) {
                var same=values.FirstOrDefault(a=>Contains(a,peer.Address));if(same!=null)return same.Address.ToString();
            }
            return values.FirstOrDefault()?.Address.ToString();
        }
        internal static string Signature(IEnumerable<WifiLanAddress> addresses) => string.Join("|",addresses.Select(a=>
            a.InterfaceId+":"+a.Address+"/"+a.Mask+":"+string.Join(",",a.Gateways.Select(g=>g.ToString()).OrderBy(g=>g))).OrderBy(a=>a,StringComparer.Ordinal));
    }
}
