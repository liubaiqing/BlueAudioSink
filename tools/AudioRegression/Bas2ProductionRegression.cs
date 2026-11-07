using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using SinkLite.Core;
using SinkLite.Core.Bas2;
using SinkLite.Platform;

internal static class Bas2ProductionRegression
{
    public static void Run(Log log, Action<string, Action> test) => test("Production Wi-Fi entry uses BAS2 and synchronizes authoritative volume and durable Android diagnostics", () => Verify(log).GetAwaiter().GetResult());
    private static async Task Verify(Log log)
    {
        var reservation = new TcpListener(IPAddress.Loopback,0); reservation.Start(); int port=((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        // Real UDP bind failure must not disable otherwise usable TCP control/media.
        using var blockedUdp = new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp) { ExclusiveAddressUse = true };
        blockedUdp.Bind(new IPEndPoint(IPAddress.Any,port));
        using var controller = new WifiController(log,port,80,200,new WifiOutputOptions(false,37));
        if (!controller.Start()) throw new Exception("Production entry failed to bind");
        string local = WifiLanAddresses.Choose(WifiLanAddresses.Read());
        if (local != null) {
            using var discovery = new UdpClient(new IPEndPoint(IPAddress.Parse(local),0));
            byte[] query = new byte[48]; Random.Shared.NextBytes(query.AsSpan(0,16));
            byte[] packet = WifiTransport.BuildPacket(WifiTransport.TypeIdentityQuery,0,0,0,query);
            // UDP discovery is intentionally retried by the app; the local announcement may
            // consume the per-source rate slot immediately after server startup.
            UdpReceiveResult found = default;
            for (int attempt=0;attempt<3;attempt++) {
                await discovery.SendAsync(packet,new IPEndPoint(IPAddress.Parse(local),45678));
                using var deadline = new CancellationTokenSource(1200);
                try { found = await discovery.ReceiveAsync(deadline.Token); break; }
                catch (OperationCanceledException) when (attempt<2) { }
            }
            if ((found.Buffer[5]&128)==0 || !found.Buffer.AsSpan(16,16).SequenceEqual(query.AsSpan(0,16))) throw new Exception("BAS2 discovery capability or nonce lost");
        }
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback,45678);
        uint id=0; string session="";
        async Task<JsonElement> Request(string type, JsonObject body) {
            using var deadline=new CancellationTokenSource(3000);
            await client.GetStream().WriteAsync(ControlMessage.Encode(type,++id,session,body),deadline.Token);
            using var json=JsonDocument.Parse(await WireCodec.ReadControlAsync(client.GetStream(),deadline.Token));
            return json.RootElement.Clone();
        }
        var hello=await Request("hello",new JsonObject { ["protocol"]=2,["expectedDeviceId"]=WifiIdentity.Current,["instanceId"]=Guid.NewGuid().ToString("N"),["name"]="production BAS2 phone" });
        session=hello.GetProperty("sessionId").GetString();
        using var media=new TcpClient(); await media.ConnectAsync(IPAddress.Loopback,port);
        await media.GetStream().WriteAsync(ControlMessage.Encode("mediaHello",1,session,new JsonObject()));
        using var timeout=new CancellationTokenSource(3000); await WireCodec.ReadControlAsync(media.GetStream(),timeout.Token);
        var volume=(await Request("volumeGet",new JsonObject())).GetProperty("body");
        if(volume.GetProperty("percent").GetInt32()!=37) throw new Exception("Lost PC volume authority");
        var state=(await Request("volumeSet",new JsonObject { ["epoch"]=volume.GetProperty("epoch").GetString(),["revision"]=volume.GetProperty("revision").GetUInt32(),["percent"]=29 })).GetProperty("body");
        if(!state.GetProperty("accepted").GetBoolean() || controller.Current.VolumePercent!=29) throw new Exception("Phone volume did not reach playback settings");
        var stale=(await Request("volumeSet",new JsonObject { ["epoch"]=volume.GetProperty("epoch").GetString(),["revision"]=volume.GetProperty("revision").GetUInt32(),["percent"]=99 })).GetProperty("body");
        if(stale.GetProperty("accepted").GetBoolean() || controller.Current.VolumePercent!=29) throw new Exception("Stale volume replayed");
        string eventId=Guid.NewGuid().ToString("N");
        var rows=new JsonArray { new JsonArray { JsonValue.Create(eventId),JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),JsonValue.Create("Bas2.ProductionRegression"),JsonValue.Create(0),JsonValue.Create(0),JsonValue.Create(0),new JsonObject { ["attemptId"]="test" } } };
        var ack=await Request("diagnostics",new JsonObject { ["events"]=rows });
        if(ack.GetProperty("type").GetString()!="diagnosticsAck" || ack.GetProperty("body").GetProperty("ids")[0].GetString()!=eventId) throw new Exception("Diagnostic persistence not acknowledged");
        if(controller.Current.OutputMode!=AudioOutputMode.None) throw new Exception("Metadata opened the sound device");
        controller.Stop();
        using var controlPort=new TcpListener(IPAddress.Any,45678); controlPort.Start();
    }
}
