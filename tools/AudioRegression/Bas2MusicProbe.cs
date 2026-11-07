using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SinkLite.Core;
using SinkLite.Core.Bas2;
using SinkLite.Platform;

internal static class Bas2MusicProbe
{
    private sealed class CapturePlayer : IWavePlayer
    {
        private readonly Action<byte[], int> collect;
        private IWaveProvider source;
        private Thread thread;
        private volatile bool running;
        public string Failure;
        public CapturePlayer(Action<byte[], int> collect) => this.collect = collect;
        public WaveFormat OutputWaveFormat => source.WaveFormat;
        public PlaybackState PlaybackState { get; private set; }
        public float Volume { get; set; }
        public event EventHandler<StoppedEventArgs> PlaybackStopped;
        public void Init(IWaveProvider provider) => source = provider;
        public void Play()
        {
            running = true; PlaybackState = PlaybackState.Playing;
            thread = new Thread(() => {
                try {
                    var clock = Stopwatch.StartNew(); int blocks = 0;
                    byte[] block = new byte[192 * 10];
                    while (running) {
                        int read = source.Read(block, 0, block.Length);
                        if (read == 0) break;
                        collect(block, read);
                        blocks++;
                        int wait = (int)(blocks * 10L - clock.ElapsedMilliseconds);
                        if (wait > 0) Thread.Sleep(wait);
                    }
                } catch (Exception error) { Failure = error.ToString(); }
            }) { IsBackground = true, Name = "BAS2ProbeOutputClock" };
            thread.Start();
        }
        public void Stop() { running = false; if (thread != null && Thread.CurrentThread != thread) thread.Join(2000); PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Pause() => Stop();
        public void Dispose() => Stop();
    }
    private sealed class CaptureDevice : IWasapiOutputDevice
    {
        public CapturePlayer Player;
        private readonly Action<byte[], int> collect;
        public CaptureDevice(Action<byte[], int> collect) => this.collect = collect;
        public string Name => "BAS2 pre-WASAPI PCM verification";
        public bool SupportsExclusive(WaveFormat format) => false;
        public IWavePlayer CreatePlayer(AudioClientShareMode mode) => Player = new CapturePlayer(collect);
        public void Dispose() { }
    }
    public static async Task Run(Log log, string[] args)
    {
        string Option(string name) => args.SkipWhile(item => item != name).Skip(1).First();
        byte[] reference = await File.ReadAllBytesAsync(Option("--reference"));
        if (reference.Length == 0 || reference.Length % 960 != 0) throw new ArgumentException("Expected whole 5ms PCM frames");
        bool tcp = args.Contains("--block-udp");
        var devices = new System.Collections.Concurrent.ConcurrentQueue<CaptureDevice>();
        using var captured = new MemoryStream();
        object gate = new();
        void Collect(byte[] data, int count) { lock (gate) captured.Write(data, 0, count); }
        long received, missing, dropped, underruns, late, ingressReceived;
        await using (var receiver = new PlaybackReceiver(log, 45778, 45779, 80, 200, new string('a', 32), "BAS2-music-probe",
            new WifiOutputOptions(false, 100), options => new AudioOutput(log, options, _ => {
                var device = new CaptureDevice(Collect); devices.Enqueue(device); return device;
            }), !tcp))
        {
            receiver.Start();
            Console.WriteLine($"READY actual LAN music -> shared playback provider; UDP-probe={!tcp}; no sound device");
            await Task.Delay(TimeSpan.FromSeconds(int.Parse(Option("--seconds"))));
            var snapshot = receiver.GetSnapshot();
            received = snapshot.Received; missing = snapshot.Missing; dropped = snapshot.Dropped; underruns = snapshot.Underruns;
            late = snapshot.Late; ingressReceived = receiver.ReceivedAtIngress;
        }
        byte[] actual; lock (gate) actual = captured.ToArray();
        int mismatchAt = -1;
        for (int i = 0; i < Math.Min(actual.Length, reference.Length); i++) if (actual[i] != reference[i]) { mismatchAt = i; break; }
        bool exact = actual.AsSpan().SequenceEqual(reference);
        var report = new { transport = tcp ? "tcp-fallback" : "udp", referenceBytes = reference.Length, outputBytes = actual.Length,
            referenceSha256 = Convert.ToHexString(SHA256.HashData(reference)), outputSha256 = Convert.ToHexString(SHA256.HashData(actual)),
            exact, mismatchAt, ingressReceived, received, missing, late, dropped, underruns, playerErrors = devices.Select(item => item.Player?.Failure).Where(item => item != null).ToArray() };
        string result = Option("--result");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(result))!);
        await File.WriteAllBytesAsync(Path.ChangeExtension(result, ".pcm"), actual);
        await File.WriteAllTextAsync(result, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        if (!exact) Environment.ExitCode = 1;
    }
}
