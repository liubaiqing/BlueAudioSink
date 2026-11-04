using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SinkLite.Core;

static class IdentityRegression
{
    static void Check(bool ok,string text) { if(!ok) throw new Exception(text); }
    public static void Run(SinkLite.Platform.Log log,Action<string,Action> Test)
    {
        Test("PC identity survives reload and concurrent creation; corrupt identity never silently changes",()=>{
            string folder=Path.Combine(Path.GetTempPath(),"bas-identity-"+Guid.NewGuid().ToString("N"));
            string file=Path.Combine(folder,"id");
            try {
                string[] ids=new string[8];Parallel.For(0,8,i=>ids[i]=WifiIdentity.LoadOrCreate(file));
                Check(ids.Distinct().Count()==1&&WifiIdentity.Valid(ids[0]),"unstable identity");
                File.WriteAllText(file,"corrupt"); bool rejected=false;
                try { WifiIdentity.LoadOrCreate(file); } catch(InvalidDataException) { rejected=true; }
                Check(rejected&&File.ReadAllText(file)=="corrupt","corrupt identity replaced");
            } finally { Directory.Delete(folder,true); }
        });
        Test("Identity reply echoes nonce and rejects wrong identity or malformed query",()=>{
            string id=new string('a',32);var payload=new byte[48];Random.Shared.NextBytes(payload.AsSpan(0,16));
            var query=WifiTransport.BuildPacket(11,0,0,0,payload);
            byte[] reply=WifiIdentity.Reply(query,query.Length,id,"电脑",45679);
            Check(reply!=null&&reply.Skip(16).Take(16).SequenceEqual(payload.Take(16)),"nonce not echoed");
            Check(Encoding.ASCII.GetString(reply,32,32)==id&&WifiTransport.ReadUInt32(reply,64)==45679,"identity/port mismatch");
            Encoding.ASCII.GetBytes(new string('b',32)).CopyTo(query,32);
            Check(WifiIdentity.Reply(query,query.Length,id,"PC",45679)==null,"wrong identity accepted");
            Check(WifiIdentity.Reply(query,63,id,"PC",45679)==null,"short query accepted");
        });
        Test("Real UDP identity query never selects a phone or opens playback",()=>{
            using var listener=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback,0));listener.ReceiveTimeout=2000;
            using var rx=new WifiReceiver(log,0,0,80,200,"identity-test");Check(rx.Start(),"receiver bind");
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var socket=(Socket)typeof(WifiReceiver).GetField("controlSocket",flags).GetValue(rx);
            int port=((IPEndPoint)socket.LocalEndPoint).Port;
            byte[] query=WifiTransport.BuildPacket(11,0,0,0,new byte[48]);
            listener.SendTo(query,new IPEndPoint(IPAddress.Loopback,port));
            byte[] buffer=new byte[256];EndPoint source=new IPEndPoint(IPAddress.Any,0);int count=listener.ReceiveFrom(buffer,ref source);
            Check(((IPEndPoint)source).Port==port&&WifiTransport.TryReadHeader(buffer,count,out var header)&&header.Type==12,"reply missing or wrong source");
            Check(rx.GetSnapshot().Remote==null&&!rx.GetSnapshot().OutputActive,"query created a playback session");
        });
    }
}
