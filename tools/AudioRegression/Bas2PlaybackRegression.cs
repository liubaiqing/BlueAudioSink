using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SinkLite.Core;
using SinkLite.Core.Bas2;
using SinkLite.Platform;

internal static class Bas2PlaybackRegression
{
    internal sealed class Player : IWavePlayer
    {
        public IWaveProvider Source;
        public bool Disposed;
        public PlaybackState PlaybackState { get; private set; }
        public float Volume { get; set; } = 1;
        public WaveFormat OutputWaveFormat => Source.WaveFormat;
        public event EventHandler<StoppedEventArgs> PlaybackStopped;
        public void Init(IWaveProvider source) => Source = source;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Stop() { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Dispose() => Disposed = true;
    }
    internal sealed class Device : IWasapiOutputDevice
    {
        public string Name => "isolated BAS2 endpoint";
        public readonly Player Player = new();
        public bool Disposed;
        public bool SupportsExclusive(WaveFormat format) => format.BitsPerSample == 16;
        public IWavePlayer CreatePlayer(AudioClientShareMode mode) => Player;
        public void Dispose() => Disposed = true;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static async Task Wait(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(4000);
        while (!ready()) await Task.Delay(5, timeout.Token);
    }
    private static async Task<JsonElement> Request(NetworkStream stream, string type, uint id, string session, JsonObject body)
    {
        using var timeout = new CancellationTokenSource(3000);
        await stream.WriteAsync(ControlMessage.Encode(type, id, session, body), timeout.Token);
        using var reply = JsonDocument.Parse(await WireCodec.ReadControlAsync(stream, timeout.Token));
        return reply.RootElement.Clone();
    }
    public static void Run(Log log, Action<string, Action> test)
    {
        test("Playback-only engine never binds transport ports or silently changes ownership", () => {
            using var control = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            using var media = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            using var playback = new WifiReceiver(log, ((IPEndPoint)control.Client.LocalEndPoint).Port,
                ((IPEndPoint)media.Client.LocalEndPoint).Port, 80, 200, "test-PC");
            Check(playback.StartPlaybackOnly(), "Playback-only mode attempted to bind occupied ports");
            try { playback.Start(); throw new Exception("Changed transport owner while running"); }
            catch (InvalidOperationException) { }
            Check(playback.GetSnapshot().OutputMode == AudioOutputMode.None, "Idle engine opened a sound device");
        });
        test("BAS2 verified UDP and TCP feed identical PCM into the existing playback pipeline", () => {
            Verify(log, false).GetAwaiter().GetResult();
            Verify(log, true).GetAwaiter().GetResult();
        });
        test("BAS2 STOP drains already-verified ingress while rejecting subsequent media", () => Verify(log, true, true).GetAwaiter().GetResult());
    }

    private static async Task Verify(Log log, bool tcp, bool queuedStop = false)
    {
        var devices = new System.Collections.Concurrent.ConcurrentQueue<Device>();
        await using var receiver = new PlaybackReceiver(log, 0, 0, 80, 200, new string('a', 32), "test-PC",
            new WifiOutputOptions(false, 100), options => new AudioOutput(log, options, _ => {
                var device = new Device(); devices.Enqueue(device); return device;
            }));
        receiver.Start();
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, receiver.ControlPort);
        var stream = control.GetStream();
        var welcome = await Request(stream, "hello", 1, "", new JsonObject { ["protocol"] = 2,
            ["expectedDeviceId"] = new string('a', 32), ["instanceId"] = Guid.NewGuid().ToString("N"), ["name"] = "BAS2 test phone" });
        string session = welcome.GetProperty("sessionId").GetString();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var media = new TcpClient();
        var destination = new IPEndPoint(IPAddress.Loopback, receiver.MediaPort);
        uint request = 1;
        if (tcp)
        {
            await media.ConnectAsync(destination);
            await Request(media.GetStream(), "mediaHello", 1, session, new JsonObject());
        }
        else
        {
            byte[] challenge = new byte[16]; Random.Shared.NextBytes(challenge);
            await udp.SendAsync(WireCodec.EncodeMedia(new MediaFrame(2, 0, Convert.FromHexString(session), 0, 0, 0, challenge)), destination);
            using var timeout = new CancellationTokenSource(3000);
            await udp.ReceiveAsync(timeout.Token);
            await Request(stream, "pathConfirmed", ++request, session, new JsonObject { ["challenge"] = Convert.ToHexString(challenge) });
        }
        byte[] firstPcm = new byte[40 * 960]; new Random(701).NextBytes(firstPcm);
        byte[] nextPcm = new byte[firstPcm.Length]; new Random(902).NextBytes(nextPcm);
        ulong oldEpoch = 0;
        foreach (byte[] pcm in queuedStop ? new[] { firstPcm } : new[] { firstPcm, nextPcm })
        {
            var start = await Request(stream, "start", ++request, session, new JsonObject());
            ulong epoch = Convert.ToUInt64(start.GetProperty("body").GetProperty("streamEpoch").GetString(), 16);
            Check(epoch != oldEpoch, "New stream reused epoch");
            if (oldEpoch != 0) await Send(oldEpoch, firstPcm.AsSpan(0, 960).ToArray(), 40);
            if (queuedStop)
            {
                // Pause the disk-writing playback worker while sockets continue receiving.
                // This reproduces STOP arriving with verified PCM still in the bounded inbox.
                var logGate = typeof(Log).GetField("sync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(log);
                Monitor.Enter(logGate);
                try
                {
                    for (uint sequence = 0; sequence < 40; sequence++)
                        Send(epoch, pcm.AsSpan((int)sequence * 960, 960).ToArray(), sequence).GetAwaiter().GetResult();
                    Check(SpinWait.SpinUntil(() => receiver.ReceivedAtIngress == 40, 2000), "Receiver did not queue the finite stream");
                    Request(stream, "stop", ++request, session, new JsonObject()).GetAwaiter().GetResult();
                }
                finally { Monitor.Exit(logGate); }
            }
            else
            {
                for (uint sequence = 0; sequence < 40; sequence++)
                {
                    await Send(epoch, pcm.AsSpan((int)sequence * 960, 960).ToArray(), sequence);
                    await Task.Delay(2);
                }
            }
            await Wait(() => receiver.GetSnapshot().Received == 40 && receiver.GetSnapshot().OutputActive);
            var player = devices.Last().Player;
            byte[] read = new byte[pcm.Length];
            Check(player.Source.Read(read, 0, read.Length) == read.Length && read.SequenceEqual(pcm), "BAS2 playback changed PCM or mixed old streams");
            Check(receiver.GetSnapshot().DeviceName == "BAS2 test phone", "Lost verified phone identity");
            if (!queuedStop) await Request(stream, "stop", ++request, session, new JsonObject());
            await Send(epoch, pcm.AsSpan(0, 960).ToArray(), 40);
            await Wait(() => player.Disposed);
            Check(receiver.GetSnapshot().Received == 40, "Stopped stream accepted late PCM");
            oldEpoch = epoch;
        }
        Check(receiver.DroppedAtIngress == 0, "Healthy media overflowed ingress queue");

        async Task Send(ulong epoch, byte[] payload, uint sequence)
        {
            var packet = WireCodec.EncodeMedia(new MediaFrame(1, 0, Convert.FromHexString(session), epoch, sequence, sequence * 5000, payload));
            if (tcp) { await media.GetStream().WriteAsync(BitConverter.GetBytes(packet.Length)); await media.GetStream().WriteAsync(packet); }
            else await udp.SendAsync(packet, destination);
        }
    }
}
