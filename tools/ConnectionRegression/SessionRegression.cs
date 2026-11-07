using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SinkLite.Core.Bas2;

internal static class SessionRegression
{
    private static void Check(bool value, string detail) { if (!value) throw new Exception(detail); }
    private static async Task<JsonElement> Request(NetworkStream stream, string type, uint request, string session, object body)
    {
        using var timeout = new CancellationTokenSource(3000);
        await stream.WriteAsync(ControlMessage.Encode(type, request, session, (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(body)), timeout.Token);
        using var document = JsonDocument.Parse(await WireCodec.ReadControlAsync(stream, timeout.Token));
        return document.RootElement.Clone();
    }

    public static async Task Run()
    {
        await using var server = new ControlServer(IPAddress.Loopback, 0, 0, new string('a', 32), "test-PC");
        server.Start();
        int accepted = 0;
        server.AudioReceived += _ => Interlocked.Increment(ref accepted);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.ControlPort);
        var stream = client.GetStream();
        var welcome = await Request(stream, "hello", 1, "", new {
            protocol = 2, expectedDeviceId = new string('a', 32), instanceId = Guid.NewGuid().ToString("N"), name = "test-phone"
        });
        Check(welcome.GetProperty("type").GetString() == "welcome", "No welcome");
        string session = welcome.GetProperty("sessionId").GetString()!;
        var premature = await Request(stream, "start", 2, session, new { });
        Check(premature.GetProperty("type").GetString() == "error", "Started before path verification");

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] challenge = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var probe = new MediaFrame(2, 0, Convert.FromHexString(session), 0, 0, 1, challenge);
        await udp.SendAsync(WireCodec.EncodeMedia(probe), new IPEndPoint(IPAddress.Loopback, server.MediaPort));
        using var receiveTimeout = new CancellationTokenSource(3000);
        var reply = await udp.ReceiveAsync(receiveTimeout.Token);
        Check(reply.RemoteEndPoint.Port == server.MediaPort, "Probe reply used wrong source port");
        Check(WireCodec.DecodeMedia(reply.Buffer).Type == 3, "No probe reply");
        var ready = await Request(stream, "pathConfirmed", 3, session, new { challenge = Convert.ToHexString(challenge) });
        Check(ready.GetProperty("type").GetString() == "pathReady", "Path not ready");
        var start = await Request(stream, "start", 4, session, new { });
        var duplicate = await Request(stream, "start", 4, session, new { });
        string epoch = start.GetProperty("body").GetProperty("streamEpoch").GetString()!;
        Check(duplicate.GetProperty("body").GetProperty("streamEpoch").GetString() == epoch, "Repeated START changed epoch");
        var audio = probe with { Type = 1, StreamEpoch = Convert.ToUInt64(epoch, 16), Payload = new byte[960] };
        await udp.SendAsync(WireCodec.EncodeMedia(audio), new IPEndPoint(IPAddress.Loopback, server.MediaPort));
        await Task.Delay(100);
        Check(accepted == 1, "Verified UDP audio not accepted");
        await Request(stream, "stop", 5, session, new { });
        await udp.SendAsync(WireCodec.EncodeMedia(audio), new IPEndPoint(IPAddress.Loopback, server.MediaPort));
        await Task.Delay(100);
        Check(accepted == 1, "Stopped stream accepted audio");
        var restarted = await Request(stream, "start", 6, session, new { });
        Check(restarted.GetProperty("body").GetProperty("streamEpoch").GetString() != epoch, "New START reused epoch");
        await udp.SendAsync(WireCodec.EncodeMedia(audio), new IPEndPoint(IPAddress.Loopback, server.MediaPort));
        await Task.Delay(100);
        Check(accepted == 1, "Stale stream accepted audio");

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, server.ControlPort);
        var tcpStream = tcpClient.GetStream();
        var tcpWelcome = await Request(tcpStream, "hello", 1, "", new {
            protocol = 2, expectedDeviceId = "", instanceId = Guid.NewGuid().ToString("N"), name = "tcp-phone"
        });
        string tcpSession = tcpWelcome.GetProperty("sessionId").GetString()!;
        using var data = new TcpClient();
        await data.ConnectAsync(IPAddress.Loopback, server.MediaPort);
        var mediaReady = await Request(data.GetStream(), "mediaHello", 1, tcpSession, new { });
        Check(mediaReady.GetProperty("type").GetString() == "mediaReady", "TCP fallback not ready");
        var busy = await Request(tcpStream, "start", 2, tcpSession, new { });
        Check(busy.GetProperty("body").GetProperty("code").GetString() == "Busy", "Second phone took over");
        await Request(stream, "stop", 7, session, new { });
        var busyReplay = await Request(tcpStream, "start", 2, tcpSession, new { });
        Check(busyReplay.GetProperty("body").GetProperty("code").GetString() == "Busy", "Rejected request replay acquired a stream");
        var tcpStart = await Request(tcpStream, "start", 3, tcpSession, new { });
        ulong tcpEpoch = Convert.ToUInt64(tcpStart.GetProperty("body").GetProperty("streamEpoch").GetString(), 16);
        byte[] tcpPacket = WireCodec.EncodeMedia(audio with { SessionId = Convert.FromHexString(tcpSession), StreamEpoch = tcpEpoch });
        byte[] prefix = BitConverter.GetBytes(tcpPacket.Length);
        await data.GetStream().WriteAsync(prefix);
        await data.GetStream().WriteAsync(tcpPacket);
        await Task.Delay(100);
        Check(accepted == 2, "TCP fallback audio not delivered");
        await Request(tcpStream, "stop", 4, tcpSession, new { });
        var stale = await Request(tcpStream, "start", uint.MaxValue, tcpSession, new { });
        Check(stale.GetProperty("body").GetProperty("code").GetString() == "StaleRequest", "Old request executed again");
        var diagnosticsEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDiagnostics=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ApplicationHandler=async (_,message)=>{
            diagnosticsEntered.TrySetResult(); await releaseDiagnostics.Task;
            return new ApplicationReply("diagnosticsAck",new System.Text.Json.Nodes.JsonObject { ["ids"]=new System.Text.Json.Nodes.JsonArray() });
        };
        using(var sidecar=new TcpClient()) {
            await sidecar.ConnectAsync(IPAddress.Loopback,server.ControlPort);
            await sidecar.GetStream().WriteAsync(ControlMessage.Encode("diagnostics",1,tcpSession,new System.Text.Json.Nodes.JsonObject { ["events"]=new System.Text.Json.Nodes.JsonArray() }));
            await diagnosticsEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var heartbeat=await Request(tcpStream,"heartbeat",5,tcpSession,new { });
            Check(heartbeat.GetProperty("type").GetString()=="heartbeatAck","Blocked log write stalled the control connection");
            releaseDiagnostics.TrySetResult();
            using var limit=new CancellationTokenSource(2000);
            var ack=ControlMessage.Parse(await WireCodec.ReadControlAsync(sidecar.GetStream(),limit.Token));
            Check(ack.Type=="diagnosticsAck","Sidecar did not acknowledge persisted batch");
        }
        Console.WriteLine("PASS real TCP control, UDP bidirectional probe, epochs, idempotency, source isolation, busy and TCP fallback");
    }
}
