using System.Reflection;
using System.Net;
using System.Text.Json;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using SinkLite.Core;
using SinkLite.Platform;
using SinkLite.UI;

class Program
{
    static readonly BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(object o, string name, object value) => o.GetType().GetField(name, F).SetValue(o, value);
    static object Get(object o, string name) => o.GetType().GetField(name, F).GetValue(o);
    static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, F).Invoke(o, args);
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static byte[] Signal(int ms, int startFrame = 0)
    {
        byte[] b = new byte[ms * 192];
        for (int frame = 0; frame < b.Length / 4; frame++)
        {
            short v = (short)(11000 * Math.Sin(2 * Math.PI * 997 * (startFrame + frame) / 48000.0));
            for (int ch = 0; ch < 2; ch++) { int i = frame * 4 + ch * 2; b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); }
        }
        return b;
    }
    static long Ticks(int ms) => System.Diagnostics.Stopwatch.Frequency * ms / 1000;
    static void Main(string[] args)
    {
        if(args.Length>0 && args[0]=="--session-status"){WasapiCadenceProbe.SessionStatus();return;}
        if(args.Length>1 && args[0]=="--output-benchmark"){WasapiCadenceProbe.OutputBenchmark(args[1],args.Length>2?args[2]:null);return;}
        if(args.Length>1 && args[0]=="--wasapi-cadence"){WasapiCadenceProbe.Run(args[1],args.Length>2?args[2]:null);return;}
        if (args.Length > 0 && args[0] == "--decode")
        {
            using var reader = new MediaFoundationReader(args[1]);
            using var resampler = new MediaFoundationResampler(reader, new WaveFormat(48000, 16, 2));
            resampler.ResamplerQuality = 60;
            WaveFileWriter.CreateWaveFile(args[2], resampler);
            return;
        }
        if (args.Length > 0 && args[0] == "--record") { Record(int.Parse(args[1]), args[2]); return; }
        using var log = new Log(Path.Combine(AppContext.BaseDirectory, "logs"));
        if (args.Length > 0 && args[0] == "--bas2-music-host") { Bas2MusicProbe.Run(log, args).GetAwaiter().GetResult(); return; }
        if(args.Length>0 && args[0]=="--output-flow"){OutputRegression.NativeFlow(log,args.Length>1?args[1]:null);return;}
        if(args.Length>0 && args[0]=="--exclusive-probe"){OutputRegression.HardwareProbe(log,args.Length>1?args[1]:null);return;}
        int passed = 0;
        void Test(string name, Action test) { test(); Console.WriteLine("PASS " + name); passed++; }
        Test("Healthy PCM remains bit exact across changing targets", () => {
            var b = new PlayoutBuffer(); var source = Signal(300); b.Add(source, 0, source.Length);
            byte[] actual = new byte[source.Length];
            for (int i = 0; i < 6; i++) { b.TargetMs = 80 + i * 10; b.Read(actual, i * 50 * 192, 50 * 192); }
            Assert(source.SequenceEqual(actual), "Changed healthy audio");
            Assert(b.PaddingFrames == 0 && b.Underruns == 0, "Unexpected padding");
        });
        Test("Actual shortage counted, recovery holds all PCM until threshold", () => {
            var b = new PlayoutBuffer(); var x = Signal(20); b.Add(x, 0, x.Length);
            byte[] read = new byte[50 * 192]; b.Read(read, 0, read.Length);
            Assert(b.PaddingFrames == 30 * 48 && b.Underruns == 1 && b.Recovering, "Shortage counter");
            x = Signal(40); b.Add(x, 0, x.Length); b.Read(read, 0, read.Length);
            Assert(b.BufferedMs == 40 && b.PaddingFrames == 80 * 48 && b.Underruns == 1, "Recovery consumed early");
            b.Add(x, 0, x.Length); b.Read(read, 0, read.Length);
            Assert(!b.Recovering && b.BufferedMs == 30, "Recovery failed to prefill");
        });
        Test("Normal drain does not count starvation", () => {
            var b = new PlayoutBuffer(); var x = Signal(20); b.Add(x, 0, x.Length); b.EndInput();
            Assert(b.Read(new byte[50 * 192], 0, 50 * 192) == x.Length, "Drain length");
            Assert(b.Underruns == 0 && b.PaddingFrames == 0, "Drain is not a fault");
        });
        Test("Target adaptation never inserts or deletes PCM; 80ms stays minimum", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            var o = new AudioOutput(log); var x = Signal(80); o.Add(x, 0, x.Length);
            Set(r, "output", o); Set(r, "playing", true); Set(r, "state", SinkState.Flowing);
            Call(r, "AdaptLocked", 0L);
            var b = (PlayoutBuffer)Get(o, "buffer"); byte[] y = new byte[x.Length]; b.Read(y, 0, y.Length);
            Assert(x.SequenceEqual(y) && r.GetSnapshot().TargetMs == 80, "Adaptation changed audio");
        });
        Test("Empty staging queue does not starve a healthy output buffer", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            var o = new AudioOutput(log); var x = Signal(80); o.Add(x, 0, x.Length);
            Set(r, "output", o); Set(r, "playing", true); Set(r, "state", SinkState.Flowing);
            Call(r, "ProcessLocked", Ticks(1));
            Assert(r.GetSnapshot().State == SinkState.Flowing && r.GetSnapshot().Underruns == 0
                && o.BufferedMs == 80, "False starvation or consumed audio");
        });
        Test("New START detaches the old consumer before prebuffering", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            var o = new AudioOutput(log); var x = Signal(80); o.Add(x, 0, x.Length);
            Set(r, "output", o); Set(r, "playing", true); Set(r, "state", SinkState.Flowing);
            Call(r, "BeginSessionLocked", Ticks(1), true, 0U);
            Assert(Get(r, "output") == null && ReferenceEquals(Get(r, "pendingRelease"), o)
                && !(bool)Get(r, "hasSessionStart"), "Old consumer or zero timestamp baseline retained");
            o.Dispose();
        });
        void Packet(WifiReceiver r, uint seq, long now)
        {
            byte[] data = WifiTransport.BuildPacket(WifiTransport.TypeAudio, 0, seq, seq * 5000, Signal(5));
            Call(r, "HandlePacket", data, data.Length, new IPEndPoint(IPAddress.Loopback, 45678), now, false);
        }
        Test("0,2,1 is reordered without concealment", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            Packet(r, 0, Ticks(1)); Call(r, "FlushReorderLocked", Ticks(1));
            Packet(r, 2, Ticks(2)); Call(r, "FlushReorderLocked", Ticks(10));
            Packet(r, 1, Ticks(11)); Call(r, "FlushReorderLocked", Ticks(11));
            var q = (System.Collections.IList)Get(r, "queue");
            Assert(q.Count == 3 && r.GetSnapshot().Missing == 0 && r.GetSnapshot().Late == 0, "Reorder loss");
            for (int i = 0; i < q.Count; i++) Assert((uint)q[i].GetType().GetField("Seq").GetValue(q[i]) == i, "Order");
        });
        Test("True gap concealed only after deadline; old packets never reset", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            Packet(r, 0, Ticks(1)); Packet(r, 2, Ticks(2));
            Call(r, "FlushReorderLocked", Ticks(21)); Assert(r.GetSnapshot().Missing == 0, "Early loss");
            Call(r, "FlushReorderLocked", Ticks(22)); Assert(r.GetSnapshot().Missing == 1, "Gap not concealed");
            Packet(r, unchecked((uint)-100), Ticks(30)); Assert(r.GetSnapshot().Resets == 0, "Old packet reset");
        });
        Test("Sequence wrap remains continuous", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            Packet(r, uint.MaxValue, Ticks(1)); Packet(r, 0, Ticks(2)); Call(r, "FlushReorderLocked", Ticks(2));
            Assert(r.GetSnapshot().Missing == 0 && ((System.Collections.IList)Get(r, "queue")).Count == 2, "Wrap");
        });
        Test("90 seconds independent arrival and 50ms device reads stay bit exact", () => {
            var b = new PlayoutBuffer(); using var expected = new MemoryStream(); using var actual = new MemoryStream();
            var prefill = Signal(100); b.Add(prefill, 0, prefill.Length); expected.Write(prefill);
            for (int ms = 0; ms < 90000; ms += 5)
            {
                var x = Signal(5, (100 + ms) * 48); b.Add(x, 0, x.Length); expected.Write(x);
                if (ms % 50 == 45) { var y = new byte[50 * 192]; b.Read(y, 0, y.Length); actual.Write(y); }
            }
            Assert(actual.ToArray().SequenceEqual(expected.ToArray().Take((int)actual.Length)), "PCM mismatch");
            Assert(b.Underruns == 0 && b.PaddingFrames == 0 && b.Overflows == 0, "Counter mismatch");
        });
        Test("Silent PCM is connected and quiet, not audible playback", () => {
            var snapshot = new WifiSnapshot { Enabled = true, State = SinkState.Flowing, Remote = "192.168.0.2:45678",
                LastAudioAgeMs = 10, OutputActive = true, HasAudibleSignal = false };
            var view = ConnectionPresentation.Wifi(snapshot);
            Assert(view.Title.Contains("静音") && view.PhoneReady && view.StreamReady && view.OutputReady, "Silent stream label");
            snapshot.HasAudibleSignal = true;
            Assert(ConnectionPresentation.Wifi(snapshot).Title.Contains("正在播放"), "Active audio label");
        });
        Test("A remembered phone endpoint alone is not a live connection", () => {
            var snapshot = new WifiSnapshot { Enabled = true, State = SinkState.Armed, Remote = "192.168.0.2:45678", ControlAgeMs = 7000 };
            var view = ConnectionPresentation.Wifi(snapshot);
            Assert(!view.PhoneReady && view.Title == "等待手机响应", "Old endpoint counted live");
            snapshot.ControlAgeMs = 20;
            Assert(ConnectionPresentation.Wifi(snapshot).Title == "手机已连接", "Control link not recognized");
        });
        Test("Stale audio cannot keep showing active playback", () => {
            var view = ConnectionPresentation.Wifi(new WifiSnapshot { Enabled = true, State = SinkState.Flowing,
                Remote = "192.168.0.2:45678", LastAudioAgeMs = 1500, HasAudibleSignal = true, OutputActive = true });
            Assert(view.Title == "音频暂时中断" && !view.StreamReady && !view.OutputReady, "Stale playing label");
        });
        Test("Recovering, draining and actual output failure remain distinct", () => {
            var snapshot = new WifiSnapshot { Enabled = true, State = SinkState.Flowing, LastAudioAgeMs = 10, Recovering = true };
            Assert(ConnectionPresentation.Wifi(snapshot).Title == "正在恢复播放", "Recovery label");
            snapshot.Draining = true;
            Assert(ConnectionPresentation.Wifi(snapshot).Title == "正在结束播放", "Drain label");
            snapshot.Draining = false; snapshot.Recovering = false;
            Assert(ConnectionPresentation.Wifi(snapshot).Title == "播放设备尚未就绪", "Output failure hidden");
        });
        Test("A fault stays actionable even if Wi-Fi failed to enable", () => {
            var view = ConnectionPresentation.Wifi(new WifiSnapshot { Enabled = false, State = SinkState.Faulted, Detail = "端口绑定失败" });
            Assert(view.Tone == ConnectionTone.Error && view.Description == "端口绑定失败", "Fault lost");
            Assert(ConnectionPresentation.Wifi(new WifiSnapshot { Enabled = true, State = SinkState.Busy }).Title == "正在准备连接", "Preparing label lost");
        });
        void From(WifiReceiver r, IPEndPoint source, byte type, long now, string name = null, uint seq = 0)
        {
            byte[] payload = type == WifiTransport.TypeHello ? System.Text.Encoding.UTF8.GetBytes(name) : type == WifiTransport.TypeAudio ? Signal(5) : null;
            byte[] data = WifiTransport.BuildPacket(type, 0, seq, (uint)(now / 1000), payload);
            Call(r, "HandlePacket", data, data.Length, source, now, false);
        }
        Test("Wi-Fi discovers a second phone without changing active name or stream", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            var a = new IPEndPoint(IPAddress.Loopback, 31001); var b = new IPEndPoint(IPAddress.Loopback, 31002);
            From(r, a, WifiTransport.TypeHello, Ticks(1), "Phone A");
            From(r, a, WifiTransport.TypeStart, Ticks(2));
            From(r, b, WifiTransport.TypeHello, Ticks(3), "Phone B");
            From(r, b, WifiTransport.TypeStart, Ticks(4));
            Assert(r.ListPeers().Count == 2 && r.GetSnapshot().DeviceName == "Phone A" && r.GetSnapshot().Remote == a.ToString(), "Foreign phone hijacked stream/name");
            Assert(r.ListPeers().Single(p => p.Active).Id == a.ToString() && r.ListPeers().All(p => !p.Selected), "Automatic source confused with explicit selection");
        });
        Test("Selecting another Wi-Fi phone releases PCM and rejects every old source message", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            var a = new IPEndPoint(IPAddress.Loopback, 31001); var b = new IPEndPoint(IPAddress.Loopback, 31002);
            From(r, a, WifiTransport.TypeHello, Ticks(1), "Phone A");
            From(r, a, WifiTransport.TypeAudio, Ticks(2));
            From(r, b, WifiTransport.TypeHello, Ticks(3), "Phone B");
            Assert(!r.SelectPeer("missing") && r.GetSnapshot().Remote == a.ToString(), "Invalid choice disturbed stream");
            Assert(r.SelectPeer(b.ToString()), "Selection failed");
            var selected = r.GetSnapshot();
            Assert(selected.Remote == b.ToString() && selected.Received == 0 && selected.BufferMs == 0 && selected.State == SinkState.Armed && double.IsNaN(selected.LastAudioAgeMs) && double.IsNaN(selected.ControlAgeMs), "Stale audio/control retained on switch");
            From(r, a, WifiTransport.TypeHello, Ticks(4), "Wrong name");
            From(r, a, WifiTransport.TypeStart, Ticks(5));
            From(r, a, WifiTransport.TypeAudio, Ticks(6));
            From(r, a, WifiTransport.TypeStop, Ticks(7));
            Assert(r.GetSnapshot().State == SinkState.Armed && r.GetSnapshot().DeviceName == "Phone B" && (long)Get(r, "lastAnyTicks") == 0, "Old source altered state");
            From(r, b, WifiTransport.TypeAudio, Ticks(8));
            Assert(r.GetSnapshot().Received == 1 && r.GetSnapshot().Remote == b.ToString(), "Selected source did not resume without START");
            Call(r, "GoArmedLocked", "timeout");
            From(r, a, WifiTransport.TypeStart, Ticks(9));
            Assert(r.GetSnapshot().State == SinkState.Armed && r.ListPeers().Single(p => p.Selected).Id == b.ToString(), "Timeout unlocked selected source");
        });
        Test("Wi-Fi discovery is bounded and retained candidates are not claimed online", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            for (int i = 0; i < 40; i++) From(r, new IPEndPoint(IPAddress.Loopback, 31000 + i), WifiTransport.TypeHello, -Ticks(11000), "Phone " + i);
            Assert(r.ListPeers().Count == 32 && r.ListPeers().All(p => !p.Recent), "Unbounded or stale presence");
        });
        Test("Two real UDP senders switch through receiver without mixed audio or takeover", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            Assert(r.Start(), "Cannot bind test receiver");
            var rx = (System.Net.Sockets.Socket)Get(r, "audioSocket");
            var target = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)rx.LocalEndPoint).Port);
            using var a = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var b = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            void Send(System.Net.Sockets.UdpClient sender, byte type, string name = null, uint seq = 0) {
                var payload = type == WifiTransport.TypeHello ? System.Text.Encoding.UTF8.GetBytes(name) : type == WifiTransport.TypeAudio ? Signal(5) : null;
                var packet = WifiTransport.BuildPacket(type, 0, seq, 0, payload); sender.Send(packet, packet.Length, target);
            }
            void Wait(Func<bool> check, string message) => Assert(SpinWait.SpinUntil(check, 2000), message);
            Send(a, WifiTransport.TypeHello, "UDP Phone A"); Send(b, WifiTransport.TypeHello, "UDP Phone B");
            Wait(() => r.ListPeers().Count == 2, "Discovery over UDP failed");
            string aId = a.Client.LocalEndPoint.ToString(), bId = b.Client.LocalEndPoint.ToString();
            Assert(r.SelectPeer(aId), "Select A"); Send(a, WifiTransport.TypeAudio);
            Wait(() => r.GetSnapshot().Received == 1, "A audio not received");
            Assert(r.SelectPeer(bId) && r.GetSnapshot().BufferMs == 0, "Switch did not clear A PCM");
            long foreign = r.GetSnapshot().Foreign;
            Send(a, WifiTransport.TypeStart); Send(a, WifiTransport.TypeAudio, seq: 1); Send(a, WifiTransport.TypeStop);
            Wait(() => r.GetSnapshot().Foreign >= foreign + 3, "Old source was not rejected");
            Assert(r.GetSnapshot().Remote == bId && r.GetSnapshot().State == SinkState.Armed, "Old source changed selected session");
            Send(b, WifiTransport.TypeAudio);
            Wait(() => r.GetSnapshot().Remote == bId && r.GetSnapshot().Received == 1 && r.GetSnapshot().BufferMs == 5, "B audio not independently buffered");
        });
        Test("Tray follows Wi-Fi audio, silence, interruption and recovery rather than Bluetooth", () => {
            var bt = new SinkSnapshot { State = SinkState.Faulted };
            var w = new WifiSnapshot { Enabled = true, State = SinkState.Armed };
            TrayState State() => TrayPresentation.Create(true, false, bt, w).State;
            Assert(State() == TrayState.Waiting, "Listening claimed a connection");
            w.Remote = "127.0.0.1:45678"; w.ControlAgeMs = 20;
            Assert(State() == TrayState.Connected, "HELLO/control presence lost");
            w.State = SinkState.LinkReady; w.LastAudioAgeMs = 10;
            Assert(State() == TrayState.Preparing, "Prebuffer claimed playback");
            w.State = SinkState.Flowing; w.OutputActive = true; w.HasAudibleSignal = true;
            Assert(State() == TrayState.Playing, "Audible playback not active");
            w.HasAudibleSignal = false;
            Assert(State() == TrayState.Connected, "Silence claimed audible playback");
            w.LastAudioAgeMs = 1200;
            Assert(State() == TrayState.Warning, "Stale audio claimed healthy playback");
            w.LastAudioAgeMs = 10; w.Recovering = true;
            Assert(State() == TrayState.Warning, "Recovery claimed healthy playback");
            w.Recovering = false; w.OutputActive = false;
            Assert(State() == TrayState.Warning, "Missing output claimed success");
            w.State = SinkState.Faulted;
            Assert(State() == TrayState.Faulted, "Fault hidden");
            Assert(TrayPresentation.Create(true, true, bt, w).State == TrayState.Preparing, "Switch status stale");
        });
        Test("Tray maps Bluetooth readiness separately from activity and mode", () => {
            var w = new WifiSnapshot { State = SinkState.Faulted };
            var expected = new[] { TrayState.Waiting, TrayState.Preparing, TrayState.Connected,
                TrayState.Connected, TrayState.Playing, TrayState.Faulted, TrayState.Warning };
            for (int i = 0; i < expected.Length; i++) {
                var p = TrayPresentation.Create(false, false, new SinkSnapshot { State = (SinkState)i }, w);
                Assert(p.State == expected[i] && p.Text.StartsWith("蓝牙"), "Bluetooth mapping " + i);
            }
        });
        Test("Native Shell tray registers, updates all states, removes and releases icon handles", () => {
            Assert(System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATA>() == (IntPtr.Size == 8 ? 976 : 956), "Incorrect native notification layout");
            Win32.WndProcDelegate proc = Win32.DefWindowProc;
            var wc = new Win32.WNDCLASSEX {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(proc),
                hInstance = Win32.GetModuleHandle(null), lpszClassName = "BlueAudioSink.TrayRegression"
            };
            Assert(Win32.RegisterClassEx(ref wc) != 0, "Register test window");
            IntPtr window = Win32.CreateWindowEx(0, wc.lpszClassName, "Tray regression", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            Assert(window != IntPtr.Zero, "Create test window");
            int before = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);
            try {
                using (var tray = new TrayIcon(window)) {
                    for (int cycle = 0; cycle < 20; cycle++) {
                        foreach (TrayState state in Enum.GetValues<TrayState>())
                            Assert(tray.Update(new TrayPresentation { State = state, Text = "Native regression · " + state }) && tray.Registered, "Shell update " + state);
                        tray.Remove(); Assert(!tray.Registered, "Remove failed");
                    }
                }
                int after = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);
                Assert(after == before, $"Leaked USER handles: {before} -> {after}");
            }
            finally { Win32.DestroyWindow(window); GC.KeepAlive(proc); }
        });
        OutputRegression.Run(log,Test);
        VolumeRegression.Run(log,Test);
        HotkeyRegression.Run(log,Test);
        WifiEnableRegression.Run(log,Test);
        WifiDiscoveryRegression.Run(log,Test);
        IdentityRegression.Run(log,Test);
        NetworkRecoveryRegression.Run(log,Test);
        Bas2PlaybackRegression.Run(log,Test);
        Bas2ProductionRegression.Run(log,Test);
        Console.WriteLine($"{passed} tests passed");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetGuiResources(IntPtr process, int flags);
    static void Record(int seconds, string path)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var capture = new WasapiLoopbackCapture(device);
        using var writer = new WaveFileWriter(path, capture.WaveFormat);
        object gate = new object();
        capture.DataAvailable += (_, e) => { lock (gate) writer.Write(e.Buffer, 0, e.BytesRecorded); };
        using var stopped = new ManualResetEventSlim();
        Exception error = null;
        capture.RecordingStopped += (_, e) => { error = e.Exception; stopped.Set(); };
        capture.StartRecording(); Thread.Sleep(seconds * 1000); capture.StopRecording();
        Assert(stopped.Wait(5000), "Recording stop timeout"); if (error != null) throw error;
        writer.Flush();
        Console.WriteLine(JsonSerializer.Serialize(new { device = device.FriendlyName, rate = capture.WaveFormat.SampleRate,
            bits = capture.WaveFormat.BitsPerSample, channels = capture.WaveFormat.Channels, format = capture.WaveFormat.ToString(), seconds, bytes = writer.Length, path }));
    }
}
