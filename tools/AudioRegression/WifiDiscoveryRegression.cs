using System.Net;
using System.Net.Sockets;
using SinkLite.Core;

static class WifiDiscoveryRegression
{
    static IPAddress Ip(string value) => IPAddress.Parse(value);
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run(SinkLite.Platform.Log log, Action<string, Action> Test)
    {
        Test("Discovery includes each LAN broadcast and the hotspot gateway, with deduplication", () => {
            var targets = new HashSet<IPAddress>();
            for (int n=0;n<2;n++) WifiDiscoveryTargets.AddSubnet(targets, Ip("192.168.233.68"), Ip("255.255.255.0"), new[]{Ip("192.168.233.231"), Ip("10.1.1.1"), Ip("192.168.233.68")});
            WifiDiscoveryTargets.AddSubnet(targets, Ip("10.2.3.4"), Ip("255.255.0.0"), new[]{Ip("10.2.0.1")});
            Check(targets.SetEquals(new[]{Ip("192.168.233.255"),Ip("192.168.233.231"),Ip("10.2.255.255"),Ip("10.2.0.1")}), "wrong subnet targets");
        });
        Test("Discovery ignores invalid addresses and masks; /31 and /32 never broadcast", () => {
            var targets = new HashSet<IPAddress>();
            foreach(string local in new[]{"127.0.0.1","169.254.5.6","0.0.0.0","224.1.2.3","::1"})
                WifiDiscoveryTargets.AddSubnet(targets,Ip(local),Ip("255.255.255.0"),Array.Empty<IPAddress>());
            foreach(string mask in new[]{"255.0.255.0","0.0.0.0","255.255.255.255","255.255.255.254"})
                WifiDiscoveryTargets.AddSubnet(targets,Ip("192.168.1.2"),Ip(mask),Array.Empty<IPAddress>());
            Check(targets.Count==0,"invalid broadcast admitted");
            WifiDiscoveryTargets.AddSubnet(targets,Ip("192.168.1.2"),Ip("255.255.255.254"),new[]{Ip("192.168.1.3")});
            Check(targets.SetEquals(new[]{Ip("192.168.1.3")}),"/31 unicast gateway lost");
        });
        Test("A failed discovery destination never suppresses other interfaces", () => {
            var delivered=new List<IPAddress>();
            int sent=WifiDiscoveryTargets.Send(new[]{Ip("10.0.0.1"),Ip("192.168.1.1")},ip=>{
                if(ip.Equals(Ip("10.0.0.1")))throw new SocketException((int)SocketError.NetworkUnreachable);
                delivered.Add(ip);
            });
            Check(sent==1&&delivered.Single().Equals(Ip("192.168.1.1")),"partial failure broke discovery");
            Check(WifiDiscoveryTargets.Send(new[]{IPAddress.Loopback},_=>throw new ObjectDisposedException("socket"))==0,"closed socket reported success");
        });
        Test("Unicast OFFER reaches a real UDP listener with original control source and format", () => {
            using var listener=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback,0)); listener.ReceiveTimeout=2000;
            int port=((IPEndPoint)listener.LocalEndPoint).Port;
            using var sender=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
            sender.Bind(new IPEndPoint(IPAddress.Loopback,0));
            byte[] offer=WifiTransport.BuildOffer("hotspot-test",45679);
            Check(WifiDiscoveryTargets.Send(new[]{IPAddress.Loopback},ip=>sender.SendTo(offer,new IPEndPoint(ip,port)))==1,"send failed");
            var bytes=new byte[256]; EndPoint from=new IPEndPoint(IPAddress.Any,0);int count=listener.ReceiveFrom(bytes,ref from);
            Check(((IPEndPoint)from).Port==((IPEndPoint)sender.LocalEndPoint).Port,"source changed");
            Check(WifiTransport.TryReadHeader(bytes,count,out var header)&&header.Type==WifiTransport.TypeOffer&&bytes.Take(count).SequenceEqual(offer),"OFFER corrupted");
        });
    }
}
