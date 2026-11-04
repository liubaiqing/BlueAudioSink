using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Reflection;
using SinkLite.Core;

static class NetworkRecoveryRegression
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static WifiLanAddress Lan(string ip,bool wireless=true)=>new WifiLanAddress {InterfaceId=wireless?"wifi":"ethernet",Wireless=wireless,
        Address=IPAddress.Parse(ip),Mask=IPAddress.Parse("255.255.255.0")};
    public static void Run(SinkLite.Platform.Log log,Action<string,Action> Test)
    {
        Test("Stale hotspot peer never redirects displayed LAN address through a VPN",()=>{
            Check(!WifiLanAddresses.Eligible(NetworkInterfaceType.Ethernet,"TAP-Windows Adapter V9"),"TAP accepted");
            Check(!WifiLanAddresses.Eligible(NetworkInterfaceType.Ethernet,"Famatech Radmin VPN Ethernet Adapter"),"VPN accepted");
            Check(!WifiLanAddresses.Eligible(NetworkInterfaceType.Ethernet,"VMware Virtual Ethernet Adapter"),"virtual accepted");
            Check(WifiLanAddresses.Eligible(NetworkInterfaceType.Wireless80211,"Intel Wi-Fi"),"Wi-Fi excluded");
            var wifi=Lan("192.168.0.165");var wired=Lan("192.168.2.5",false);
            Check(WifiLanAddresses.Choose(new[]{wired,wifi},"192.168.233.231:45678")=="192.168.0.165","stale peer changed address");
            Check(WifiLanAddresses.Choose(new[]{wired,wifi},"192.168.2.8:45678")=="192.168.2.5","fresh on-link peer route lost");
            Check(WifiLanAddresses.Choose(Array.Empty<WifiLanAddress>())==null,"missing LAN became a fake address");
        });
        Test("Network reset removes the old phone lock and accepts a new HELLO without playback",()=>{
            using var rx=new WifiReceiver(log,0,0,80,200,"network-reset");Check(rx.Start(),"bind");
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var handle=typeof(WifiReceiver).GetMethod("HandlePacket",flags);
            var old=new IPEndPoint(IPAddress.Parse("192.168.233.231"),45678);
            var next=new IPEndPoint(IPAddress.Parse("192.168.0.116"),45678);
            var hello=WifiTransport.BuildPacket(7,0,0,0,System.Text.Encoding.UTF8.GetBytes("phone"));
            rx.PrepareReconnect(old,"old phone");handle.Invoke(rx,new object[]{hello,hello.Length,next,100L,true});
            Check(rx.GetSnapshot().Remote==old.ToString(),"fixture did not reproduce old source lock");
            rx.ResetNetworkPeers();handle.Invoke(rx,new object[]{hello,hello.Length,next,101L,true});
            Check(rx.GetSnapshot().Remote==next.ToString()&&!rx.GetSnapshot().OutputActive,"new HELLO still blocked");
            Check(double.IsNaN(rx.GetSnapshot().RttMs),"old RTT survived");
        });
        Test("A changed LAN generation cannot restore an old reconnect cache",()=>{
            var addresses=new[]{Lan("192.168.233.68")};
            using var controller=new WifiController(log,0,80,200,null,()=>addresses);
            using var rx=new WifiReceiver(log,0,0,80,200,"generation");Check(rx.Start(),"bind");
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            typeof(WifiController).GetField("receiver",flags).SetValue(controller,rx);
            long epoch=controller.Current.NetworkGeneration;
            controller.PrepareReconnect("192.168.233.231:45678","phone",epoch);
            Check(controller.Current.Remote!=null,"original reconnect failed");
            addresses=new[]{Lan("192.168.0.165")};controller.RefreshDiscovery();
            Check(controller.Current.NetworkGeneration!=epoch&&controller.Current.Remote==null,"network generation kept old peer");
            controller.PrepareReconnect("192.168.233.231:45678","phone",epoch);
            Check(controller.Current.Remote==null&&controller.Current.LocalAddress=="192.168.0.165","stale reconnect cache revived");
        });
    }
}
