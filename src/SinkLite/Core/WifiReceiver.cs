using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using SinkLite.Platform;

namespace SinkLite.Core
{
    internal sealed class WifiPeer
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Recent { get; set; }
        public bool Selected { get; set; }
        public bool Active { get; set; }
    }

    /// <summary>
    /// Wi-Fi 模式的 UDP 接收、抖动缓冲与播放调度。
    ///
    /// 协议实现要点（docs/02-protocol.md v1.1）：
    ///   - PC 绑定 0.0.0.0:45678（控制）与 0.0.0.0:45679（音频）；
    ///   - AUDIO 5 ms / 240 帧 / 960 字节，seq 200 包每秒；
    ///   - 20 ms 有界重排，到截止仍缺失才补包；旧迟到包不重置会话；
    ///   - D = 已入队未播放时长；500 ms 更新恢复阈值，不改写健康音频；
    ///   - 缺口补静音；d>5 记断流并 5 ms 淡入；d>10 清队重缓冲；
    ///   - 欠载补静音并重缓冲到 lastTarget；STOP 排空 ≤500 ms；5 秒无 AUDIO 回 Armed。
    /// </summary>
    internal sealed class WifiReceiver : IDisposable
    {
        private const int TimeoutMs = 5000;
        private const int DrainTimeoutMs = 500;
        private const int LockWindowMs = 10000;
        private const int StatsIntervalMs = 5000;
        private const int AggregateIntervalMs = 1000;
        private const int JitterWindowMs = 2000;
        private const int PingIntervalMs = 2000;
        private const int MaxGapPackets = 10;

        private sealed class AudioFrame
        {
            public uint Seq;
            public byte[] Data;
            public bool Silence;
            public bool FadeIn;
            public long ArrivalTicks;
        }

        private struct JitterSample
        {
            public long Ticks;
            public double DeltaUs;
        }

        private struct LossSample
        {
            public long Ticks;
            public int Received;
            public int Missing;
        }

        private readonly object sync = new object();
        private readonly Log log;
        private readonly int controlPort;
        private readonly int audioPort;
        private int baseMs;
        private int maxMs;
        private readonly string deviceName;
        private string identity;
        private readonly Stopwatch clock = Stopwatch.StartNew();

        private readonly Dictionary<uint, AudioFrame> reorder = new Dictionary<uint, AudioFrame>();
        private long gapSinceTicks;
        private const int ReorderWaitMs = 20;
        private readonly List<AudioFrame> queue = new List<AudioFrame>(64);
        private readonly Queue<JitterSample> jitterSamples = new Queue<JitterSample>(512);
        private readonly Queue<LossSample> lossWindow = new Queue<LossSample>(512);

        private volatile bool running;
        private Socket controlSocket;
        private Socket audioSocket;
        private Thread controlThread;
        private Thread audioThread;
        private Thread playoutThread;
        private bool externalTransport;
        private string verifiedStream;
        private readonly Func<WifiOutputOptions, AudioOutput> createOutput;
        private WifiOutputOptions outputOptions = new WifiOutputOptions();
        private long outputRevision;
        private AudioOutput output;
        private AudioOutput pendingRelease;

        private SinkState state = SinkState.Armed;
        private string detail = "等待手机连接";
        private IPEndPoint remote;
        private IPEndPoint sessionEndpoint;
        private string remoteName;
        private sealed class KnownPeer
        {
            public IPEndPoint Endpoint;
            public string Name;
            public long Seen;
        }
        private readonly Dictionary<string, KnownPeer> peers = new Dictionary<string, KnownPeer>();
        private IPEndPoint selectedPeer;

        private void RememberPeerLocked(IPEndPoint endpoint, long now, string name = null)
        {
            if (endpoint == null) return;
            string id = endpoint.ToString();
            if (!peers.TryGetValue(id, out KnownPeer peer))
            {
                if (peers.Count >= 32) return;
                peer = new KnownPeer { Endpoint = endpoint, Name = endpoint.Address.ToString() };
                peers.Add(id, peer);
            }
            peer.Seen = now;
            if (!string.IsNullOrWhiteSpace(name)) peer.Name = name;
        }

        public IReadOnlyList<WifiPeer> ListPeers()
        {
            lock (sync)
            {
                var result = new List<WifiPeer>();
                long now = clock.ElapsedTicks;
                foreach (var pair in peers)
                    result.Add(new WifiPeer { Id = pair.Key, Name = pair.Value.Name,
                        Recent = now - pair.Value.Seen <= TicksFromMs(10000),
                        Selected = SameEndpoint(selectedPeer, pair.Value.Endpoint),
                        Active = SameEndpoint(sessionEndpoint, pair.Value.Endpoint) });
                return result;
            }
        }

        public bool SelectPeer(string id)
        {
            lock (sync)
            {
                if (!peers.TryGetValue(id, out KnownPeer peer)) return false;
                selectedPeer = peer.Endpoint;
                if (SameEndpoint(sessionEndpoint, selectedPeer)) return true;
                GoArmedLocked("已选择手机，等待发送音频");
                received = missing = duplicates = late = resets = underruns = discontinuities = dropped = stale = 0;
                gapPending = malformedPending = underrunPending = rebufferPending = 0;
                remote = peer.Endpoint;
                remoteName = peer.Name;
                lastAnyTicks = 0;
                log.Info("Wifi.DeviceSelected", ("endpoint", id), ("name", peer.Name));
                return true;
            }
        }

        public void PrepareReconnect(IPEndPoint peer, string name)
        {
            lock(sync)
            {
                selectedPeer = remote = peer; remoteName = name;
                lastAnyTicks = clock.ElapsedTicks; lastPongTicks = 0;
                RememberPeerLocked(peer, lastAnyTicks, name);
                detail = "正在重新探测手机";
            }
        }

        public void ResetNetworkPeers()
        {
            lock(sync)
            {
                GoArmedLocked("网络已切换，等待手机重新连接");
                selectedPeer=remote=null; remoteName=null; peers.Clear();
                lastAnyTicks=0; lastPingSendUs=0;
                volumePeer=null;
                remoteVolume.Update(outputOptions.VolumePercent,outputOptions.Exclusive,true);
                lossWindow.Clear();
                received=missing=duplicates=late=resets=underruns=discontinuities=dropped=stale=0;
                gapPending=malformedPending=underrunPending=rebufferPending=0;
            }
        }

        private bool AcceptSourceLocked(IPEndPoint from)
        {
            return (selectedPeer == null || SameEndpoint(selectedPeer, from)) &&
                (sessionEndpoint == null || SameEndpoint(sessionEndpoint, from));
        }

        private bool expectedValid;
        private uint expectedSeq;
        private bool firstAudio;
        private bool playing;
        private bool draining;
        private bool rebuffering;
        private bool fadeInPending;
        private uint sessionStartUs;
        private bool hasSessionStart;

        private long drainEmptyTicks;
        private long stopTicks;
        private long lastAudioTicks;
        private long lastSignalTicks;
        private long lastAnyTicks;
        private long prevArrivalTicks;
        private uint lastTimestampUs;
        private long lastAdaptTicks;
        private long lastStatsLogTicks;
        private long lastAggregateLogTicks;
        private int targetMs;

        private double jitterUs;
        private bool prevAudioValid;
        private uint prevAudioSeq;
        private uint prevAudioTimestampUs;

        private double rttMs = double.NaN;
        private int clockOffsetUs;
        private bool offsetValid;
        private long lastPingTicks;
        private uint lastPingSendUs;
        private long lastPongTicks;

        private long received;
        private long missing;
        private long duplicates;
        private long late;
        private long foreign;
        private long malformed;
        private long unknown;
        private long resets;
        private long discontinuities;
        private long underruns;
        private long dropped;
        private long stale;

        private int gapPending;
        private uint gapLastSeq;
        private int malformedPending;
        private string malformedReason;
        private int underrunPending;
        private int rebufferPending;
        private int lastRebufferMs;

        public WifiReceiver(Log log, int controlPort, int audioPort, int bufferBaseMs, int bufferMaxMs, string deviceName, WifiOutputOptions outputOptions = null,
            Func<WifiOutputOptions, AudioOutput> createOutput = null)
        {
            this.log = log;
            this.createOutput = createOutput ?? (options => new AudioOutput(log, options));
            this.outputOptions = outputOptions ?? new WifiOutputOptions();
            remoteVolume.Update(this.outputOptions.VolumePercent, this.outputOptions.Exclusive);
            this.controlPort = controlPort;
            this.audioPort = audioPort;
            baseMs = Math.Clamp(bufferBaseMs, 10, 800);
            maxMs = Math.Clamp(bufferMaxMs, baseMs, 1000);
            this.deviceName = string.IsNullOrWhiteSpace(deviceName) ? "BlueAudioSink" : deviceName;
            targetMs = baseMs;
        }

        public void SetBufferBase(int milliseconds)
        {
            lock (sync)
            {
                baseMs = Math.Clamp(milliseconds, 40, 800);
                maxMs = Math.Max(maxMs, baseMs);
                targetMs = Math.Max(baseMs, targetMs);
            }
        }

        private readonly WifiVolumeControl remoteVolume = new WifiVolumeControl();
        private IPEndPoint volumePeer;
        public event Action RemoteVolumeChanged;

        private void HandleVolume(byte[] data, int length, WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            byte[] reply; bool changed = false;
            lock(sync)
            {
                if (!AcceptSourceLocked(from) || !IsCurrentEndpointLocked(from) || lastPongTicks == 0 || now - lastPongTicks > TicksFromMs(5000)) return;
                reply = remoteVolume.Handle(data, length, header, from.ToString(), out var requested);
                if (reply == null) return;
                volumePeer = from;
                if (requested.HasValue)
                {
                    outputOptions = new WifiOutputOptions(outputOptions.Exclusive, requested.Value, outputOptions.EndpointId);
                    outputRevision++;
                    if(output != null) output.VolumePercent = requested.Value;
                    changed = true;
                }
            }
            SendControlPacket(reply, from);
            if(changed) RemoteVolumeChanged?.Invoke();
        }

        public void SetOutputOptions(WifiOutputOptions options)
        {
            lock(sync)
            {
                bool rebuild=!outputOptions.SameDeviceAndMode(options);
                bool notify = remoteVolume.Update(options.VolumePercent, options.Exclusive, outputOptions.EndpointId != options.EndpointId);
                outputOptions=options;outputRevision++;
                if(notify && volumePeer != null && AcceptSourceLocked(volumePeer) && IsCurrentEndpointLocked(volumePeer))
                    SendControlPacket(remoteVolume.State(), volumePeer);
                if(output!=null)output.VolumePercent=options.VolumePercent;
                if(rebuild)RefreshOutput();
            }
        }

        public void RefreshOutput()
        {
            lock (sync)
            {
                outputRevision++;
                DetachOutputLocked();
                playing = false;
                rebuffering = false;
                if (sessionEndpoint != null) { state = SinkState.LinkReady; detail = "正在切换播放设备"; }
            }
        }

        public bool Start() => Start(false);

        // Transport adapters own their sockets; this entry starts only the existing playout engine.
        public bool StartPlaybackOnly() => Start(true);

        private bool Start(bool playbackOnly)
        {
            lock (sync)
            {
                if (running)
                {
                    if (externalTransport != playbackOnly) throw new InvalidOperationException("Stop the engine before changing transport ownership");
                    return true;
                }

                Socket control = null;
                Socket audio = null;
                try
                {
                    externalTransport = playbackOnly;
                    verifiedStream = null;
                    if (!playbackOnly)
                    {
                        identity = WifiIdentity.Current;
                        control = BindSocket(controlPort, true);
                        audio = BindSocket(audioPort, false);
                    }
                    controlSocket = control;
                    audioSocket = audio;
                    running = true;

                    controlThread = new Thread(() => ReceiveLoop(control, true));
                    controlThread.IsBackground = true;
                    controlThread.Name = "WifiControl";
                    audioThread = new Thread(() => ReceiveLoop(audio, false));
                    audioThread.IsBackground = true;
                    audioThread.Name = "WifiAudio";
                    playoutThread = new Thread(PlayoutLoop);
                    playoutThread.IsBackground = true;
                    playoutThread.Name = "WifiPlayout";

                    if (!playbackOnly) { controlThread.Start(); audioThread.Start(); }
                    else { controlThread = null; audioThread = null; }
                    playoutThread.Start();
                    state = SinkState.Armed;
                    detail = "等待手机连接";
                    return true;
                }
                catch (Exception ex)
                {
                    running = false;
                    controlSocket = null;
                    audioSocket = null;
                    CloseSocket(control);
                    CloseSocket(audio);
                    state = SinkState.Faulted;
                    detail = "端口 " + controlPort + "/" + audioPort + " 绑定失败";
                    log.Error(
                        "Wifi.BindFailed",
                        ("controlPort", controlPort),
                        ("audioPort", audioPort),
                        ("error", ex.Message));
                    return false;
                }
            }
        }

        public void Stop()
        {
            if (!running && controlSocket == null && audioSocket == null)
            {
                return;
            }

            running = false;
            CloseSocket(controlSocket);
            CloseSocket(audioSocket);
            JoinThread(controlThread, 1000);
            JoinThread(audioThread, 1000);
            JoinThread(playoutThread, 1500);

            AudioOutput release = null;
            AudioOutput pending = null;
            lock (sync)
            {
                ClearQueueLocked();
                verifiedStream = null;
                release = output;
                output = null;
                pending = pendingRelease;
                pendingRelease = null;
                state = SinkState.Armed;
                detail = "Wi-Fi 已停止";
                playing = false;
                draining = false;
                rebuffering = false;
                expectedValid = false;
                sessionEndpoint = null;
                hasSessionStart = false;
                offsetValid = false;
                rttMs = double.NaN;
                lastPingTicks = 0;
                lastAudioTicks = 0;
            }

            if (release != null)
            {
                release.Dispose();
            }

            if (pending != null)
            {
                pending.Dispose();
            }

            controlSocket = null;
            audioSocket = null;
            controlThread = null;
            audioThread = null;
            playoutThread = null;
        }

        /// <summary>从同一控制 socket 发送局域网发现，兼容多网卡和手机热点。</summary>
        public bool SendOffer(IEnumerable<IPAddress> targets = null)
        {
            Socket socket = controlSocket;
            if (socket == null)
            {
                return false;
            }

            try
            {
                byte[] offer = WifiTransport.BuildOffer(deviceName, audioPort);
                byte[] id = System.Text.Encoding.ASCII.GetBytes(identity ??= WifiIdentity.Current);
                Array.Resize(ref offer, offer.Length + id.Length);
                Buffer.BlockCopy(id,0,offer,offer.Length-id.Length,id.Length);
                int sent = WifiDiscoveryTargets.Send(targets ?? WifiDiscoveryTargets.GetCurrent(),
                    target => socket.SendTo(offer, new IPEndPoint(target, controlPort)));
                if (sent == 0) throw new SocketException((int)SocketError.NetworkUnreachable);
                return true;
            }
            catch (Exception ex)
            {
                if (DateTime.Now - lastSendErrorLog > TimeSpan.FromSeconds(30))
                {
                    lastSendErrorLog = DateTime.Now;
                    log.Warn("Wifi.Fault", ("code", "OfferFailed"), ("detail", ex.Message));
                }
                return false;
            }
        }

        private DateTime lastSendErrorLog = DateTime.MinValue;

        private static Socket BindSocket(int port, bool broadcast)
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.ReceiveBufferSize = 512 * 1024;
            socket.ReceiveTimeout = 250;
            socket.EnableBroadcast = broadcast;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }

        private void ReceiveLoop(Socket socket, bool control)
        {
            byte[] buffer = new byte[2048];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);

            while (running)
            {
                try
                {
                    int length = socket.ReceiveFrom(buffer, ref any);
                    long now = clock.ElapsedTicks;
                    IPEndPoint from = any as IPEndPoint;
                    if (from != null)
                    {
                        HandlePacket(buffer, length, from, now, control);
                    }
                    any = new IPEndPoint(IPAddress.Any, 0);
                }
                catch (SocketException ex)
                {
                    if (!running)
                    {
                        break;
                    }

                    if (ex.SocketErrorCode == SocketError.TimedOut ||
                        ex.SocketErrorCode == SocketError.ConnectionReset ||
                        ex.SocketErrorCode == SocketError.MessageSize ||
                        ex.SocketErrorCode == SocketError.Interrupted)
                    {
                        continue;
                    }

                    FailNetworkLocked("ReceiveError", ex.SocketErrorCode + ": " + ex.Message);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!running)
                    {
                        break;
                    }
                    FailNetworkLocked("ReceiveError", ex.Message);
                    break;
                }
            }
        }

        private void FailNetworkLocked(string code, string text)
        {
            lock (sync)
            {
                state = SinkState.Faulted;
                detail = "网络接收失败：" + text;
                DetachOutputLocked();
                playing = false;
            }
            log.Error("Wifi.Fault", ("code", code), ("detail", text));
        }

        // ===== 包解析与会话 =====

        private void HandlePacket(byte[] data, int length, IPEndPoint from, long now, bool control)
        {
            if (length < WifiTransport.HeaderSize)
            {
                AddMalformedLocked("short");
                return;
            }

            WifiTransport.WifiHeader header;
            if (!WifiTransport.TryReadHeader(data, length, out header))
            {
                AddMalformedLocked("magic");
                return;
            }

            switch (header.Type)
            {
                case WifiTransport.TypeIdentityQuery:
                    if (control)
                    {
                        byte[] reply = WifiIdentity.Reply(data,length,identity ??= WifiIdentity.Current,deviceName,audioPort);
                        if (reply != null) SendControlPacket(reply,from);
                    }
                    break;
                case WifiTransport.TypeIdentityReply:
                    break;
                case WifiTransport.TypeDiagnostics:
                    if (control) HandleDiagnostics(data, length, header, from, now);
                    break;
                case WifiTransport.TypeDiagnosticsAck:
                    break;
                case WifiTransport.TypeAudio:
                    HandleAudio(data, length, header, from, now);
                    break;
                case WifiTransport.TypeStart:
                    HandleStart(header, from, now);
                    break;
                case WifiTransport.TypeStop:
                    HandleStop(header, from, now);
                    break;
                case WifiTransport.TypePing:
                    HandlePing(header, from, now);
                    break;
                case WifiTransport.TypePong:
                    HandlePong(data, length, header, from, now);
                    break;
                case WifiTransport.TypeHello:
                    HandleHello(data, length, from, now);
                    break;
                case WifiTransport.TypeVolumeGet:
                case WifiTransport.TypeVolumeSet:
                    HandleVolume(data, length, header, from, now);
                    break;
                case WifiTransport.TypeOffer:
                    // 自己广播回来的 OFFER，忽略。
                    break;
                default:
                    lock (sync)
                    {
                        if (!AcceptSourceLocked(from)) return;
                        unknown++;
                        lastAnyTicks = now;
                    }
                    break;
            }
        }

        private long lastDiagnosticTicks;
        private uint lastDiagnosticPong;
        private static readonly Regex DiagnosticCode = new Regex(@"\A[A-Za-z][A-Za-z0-9.-]{0,47}\z", RegexOptions.CultureInvariant);

        private void HandleDiagnostics(byte[] data, int length, WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            int payloadLength = length - WifiTransport.HeaderSize;
            if (payloadLength <= 0 || payloadLength > 1100) return;
            lock (sync)
            {
                if (!AcceptSourceLocked(from) || !IsCurrentEndpointLocked(from) || lastPongTicks == 0 ||
                    now - lastPongTicks > TicksFromMs(5000) || header.TimestampUs != lastDiagnosticPong ||
                    (lastDiagnosticTicks != 0 && now - lastDiagnosticTicks < TicksFromMs(1000))) return;
                lastDiagnosticTicks = now;
            }

            var entries = new List<(string Id, long Time, string Code, long A, long B, long C)>();
            try
            {
                using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(data, WifiTransport.HeaderSize, payloadLength));
                var batch = document.RootElement;
                if (batch.ValueKind != JsonValueKind.Array || batch.GetArrayLength() is < 1 or > 6) return;
                foreach (var row in batch.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 6 ||
                        row[0].ValueKind != JsonValueKind.String || row[2].ValueKind != JsonValueKind.String) return;
                    string id = row[0].GetString(), code = row[2].GetString();
                    if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _) || !DiagnosticCode.IsMatch(code) ||
                        row[1].ValueKind != JsonValueKind.Number || !row[1].TryGetInt64(out long time) ||
                        row[3].ValueKind != JsonValueKind.Number || !row[3].TryGetInt64(out long a) ||
                        row[4].ValueKind != JsonValueKind.Number || !row[4].TryGetInt64(out long b) ||
                        row[5].ValueKind != JsonValueKind.Number || !row[5].TryGetInt64(out long c)) return;
                    entries.Add((id, time, code, a, b, c));
                }
            }
            catch (JsonException) { return; }

            foreach (var entry in entries)
                if (!log.WriteAndroidEvent(entry.Id, entry.Time, entry.Code, entry.A, entry.B, entry.C, header.TimestampUs)) return;

            SendControlPacket(WifiTransport.BuildPacket(WifiTransport.TypeDiagnosticsAck, 0, header.Seq, header.TimestampUs, null), from);
        }

        private void HandleAudio(byte[] data, int length, WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            if (length != WifiTransport.PacketBytes)
            {
                AddMalformedLocked("audio_length");
                return;
            }

            AcceptPcm(data, WifiTransport.HeaderSize, header, from, now);
        }

        public void BeginVerifiedStream(string stream, IPEndPoint endpoint, string name)
        {
            if (string.IsNullOrEmpty(stream) || endpoint == null) throw new ArgumentException("Missing verified stream");
            lock (sync)
            {
                if (!running || !externalTransport) throw new InvalidOperationException("Playback-only engine is not running");
                if (verifiedStream == stream) return;
                verifiedStream = stream;
                selectedPeer = null;
                sessionEndpoint = remote = endpoint;
                remoteName = name;
                RememberPeerLocked(endpoint, clock.ElapsedTicks, name);
                BeginSessionLocked(clock.ElapsedTicks, false, 0);
            }
        }

        public bool AcceptVerifiedPcm(string stream, byte[] pcm, uint sequence, uint timestampUs, byte flags)
        {
            if (pcm == null || pcm.Length != WifiTransport.PayloadBytes || flags > 1) throw new ArgumentException("Invalid verified PCM");
            lock (sync)
            {
                if (!running || !externalTransport || verifiedStream == null || stream != verifiedStream) { stale++; return false; }
                AcceptPcm(pcm, 0, new WifiTransport.WifiHeader { Type = 1, Flags = flags, Seq = sequence, TimestampUs = timestampUs },
                    sessionEndpoint ?? remote, clock.ElapsedTicks);
                return true;
            }
        }

        public void EndVerifiedStream(string stream, bool disconnected)
        {
            lock (sync)
            {
                if (verifiedStream == null || verifiedStream != stream) return;
                verifiedStream = null; // no queued late PCM may restart a stopped stream
                if (disconnected) GoArmedLocked("连接已断开，等待手机重连");
                else HandleStop(default, sessionEndpoint ?? remote, clock.ElapsedTicks);
            }
        }

        private void AcceptPcm(byte[] data, int payloadOffset, WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {

            lock (sync)
            {
                RememberPeerLocked(from, now);
                if (!AcceptSourceLocked(from)) { foreign++; return; }
                lastAnyTicks = now;

                if (state == SinkState.Faulted)
                {
                    return;
                }

                if (draining)
                {
                    late++;
                    return;
                }

                if (sessionEndpoint == null)
                {
                    // START 丢失：Armed 下首个 AUDIO 自动建会话，基线取该包 seq。
                    sessionEndpoint = from;
                    remote = from;
                    remoteName = peers.TryGetValue(from.ToString(), out KnownPeer peer) ? peer.Name : from.Address.ToString();
                    BeginSessionLocked(now, false, 0);
                    log.Info("Wifi.Start", ("endpoint", from.ToString()), ("packetTsUs", header.TimestampUs), ("auto", true));
                }
                else if (!SameEndpoint(sessionEndpoint, from))
                {
                    foreign++;
                    return;
                }

                remote = from;

                if (hasSessionStart)
                {
                    int diff = unchecked((int)(header.TimestampUs - sessionStartUs));
                    if (diff < -100000)
                    {
                        stale++;
                        return;
                    }
                }

                if (!CanQueueSequenceLocked(header.Seq, now))
                {
                    return;
                }

                lastAudioTicks = now;
                lastTimestampUs = header.TimestampUs;
                // Read-only activity observation for the UI; never changes packet scheduling.
                for (int sample = payloadOffset; sample + 1 < payloadOffset + WifiTransport.PayloadBytes; sample += 2)
                {
                    short value = (short)(data[sample] | data[sample + 1] << 8);
                    if (value > 32 || value < -32) { lastSignalTicks = now; break; }
                }

                byte[] rented = ArrayPool<byte>.Shared.Rent(WifiTransport.PayloadBytes);
                Buffer.BlockCopy(data, payloadOffset, rented, 0, WifiTransport.PayloadBytes);
                AudioFrame frame = new AudioFrame();
                frame.Seq = header.Seq;
                frame.Data = rented;
                frame.Silence = false;
                frame.FadeIn = (header.Flags & WifiTransport.FlagDiscontinuity) != 0;
                frame.ArrivalTicks = now;
                reorder.Add(frame.Seq, frame);
                received++;
                AddLossSampleLocked(1, 0);

                UpdateJitterLocked(now, header);
                if (!firstAudio)
                {
                    firstAudio = true;
                    log.Info("Wifi.FirstAudio", ("seq", header.Seq), ("packetTsUs", header.TimestampUs));
                }

                if (frame.FadeIn) discontinuities++;

                if (state != SinkState.Flowing)
                {
                    state = SinkState.LinkReady;
                    detail = "预缓冲中";
                }

            }
        }

        private bool CanQueueSequenceLocked(uint seq, long now)
        {
            if (!expectedValid)
            {
                expectedValid = true;
                expectedSeq = seq;
            }
            int distance = unchecked((int)(seq - expectedSeq));
            if (distance < 0) { late++; return false; }
            if (reorder.ContainsKey(seq)) { duplicates++; return false; }
            // A major forward jump is a discontinuity, but old late packets never reset playback.
            if (distance > 200)
            {
                ClearQueueLocked();
                expectedSeq = seq;
                resets++;
                discontinuities++;
                log.Warn("Wifi.Reset", ("newSeq", seq));
            }
            return true;
        }

        private void FlushReorderLocked(long now)
        {
            while (reorder.Count > 0)
            {
                AudioFrame next;
                if (reorder.TryGetValue(expectedSeq, out next))
                {
                    reorder.Remove(expectedSeq++);
                    queue.Add(next);
                    gapSinceTicks = 0;
                    continue;
                }
                // Wait on the oldest packet beyond the gap; the deadline does not slide
                // with subsequent arrivals or each newly discovered missing sequence.
                long oldest = long.MaxValue;
                uint nearest = 0;
                int distance = int.MaxValue;
                foreach (AudioFrame candidate in reorder.Values)
                {
                    oldest = Math.Min(oldest, candidate.ArrivalTicks);
                    int d = unchecked((int)(candidate.Seq - expectedSeq));
                    if (d >= 0 && d < distance) { distance = d; nearest = candidate.Seq; }
                }
                gapSinceTicks = oldest;
                if (!draining && now - gapSinceTicks < TicksFromMs(ReorderWaitMs)) break;
                if (distance > MaxGapPackets)
                {
                    expectedSeq = nearest;
                    discontinuities++;
                    fadeInPending = true;
                    continue;
                }
                queue.Add(new AudioFrame { Seq = expectedSeq++, Silence = true });
                missing++;
                AddLossSampleLocked(0, 1);
                gapPending++;
                gapLastSeq = expectedSeq;
            }
        }

        private void HandleStart(WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            lock (sync)
            {
                RememberPeerLocked(from, now);
                if (!AcceptSourceLocked(from)) { foreign++; return; }
                lastAnyTicks = now;
                sessionEndpoint = from;
                remote = from;
                remoteName = peers.TryGetValue(from.ToString(), out KnownPeer peer) ? peer.Name : from.Address.ToString();
                draining = false;
                rebuffering = false;
                BeginSessionLocked(now, true, header.TimestampUs);
                state = SinkState.LinkReady;
                detail = "预缓冲中";
                lastAudioTicks = now;
            }

            log.Info(
                "Wifi.Start",
                ("endpoint", from == null ? "?" : from.ToString()),
                ("packetTsUs", header.TimestampUs),
                ("auto", false));
        }

        private void HandleStop(WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            bool finish = false;
            lock (sync)
            {
                if (!AcceptSourceLocked(from)) { foreign++; return; }
                lastAnyTicks = now;
                if (state == SinkState.Faulted || state == SinkState.Armed)
                {
                    return;
                }

                if (sessionEndpoint != null && from != null && !SameEndpoint(sessionEndpoint, from))
                {
                    foreign++;
                    return;
                }

                if (draining)
                {
                    return;
                }

                draining = true;
                rebuffering = false;
                stopTicks = now;
                state = SinkState.LinkReady;
                detail = "排空缓冲中";
                if (!playing && queue.Count == 0 && reorder.Count == 0)
                {
                    finish = true;
                }
            }

            log.Info("Wifi.Stop", ("packetTsUs", header.TimestampUs));

            if (finish)
            {
                lock (sync)
                {
                    DrainDoneLocked(clock.ElapsedTicks);
                }
            }
        }

        private void HandlePing(WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            lock (sync)
            {
                if (!AcceptSourceLocked(from)) return;
                lastAnyTicks = now;
                if (state == SinkState.Armed && remote == null)
                {
                    remote = from;
                }
            }

            // v1.1 正常由 PC 发 PING；兼容手机主动 PING：payload 回填其 T1，头填 T2。
            SendControlPacket(WifiTransport.BuildPong(header.TimestampUs, ToMicroseconds(now)), from);
        }

        private void HandlePong(byte[] data, int length, WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            if (length != WifiTransport.HeaderSize + 4)
            {
                AddMalformedLocked("pong_length");
                return;
            }

            uint echoedUs = WifiTransport.ReadUInt32(data, WifiTransport.HeaderSize);
            lock (sync)
            {
                if (!AcceptSourceLocked(from)) return;
                if (!IsCurrentEndpointLocked(from))
                {
                    return;
                }

                lastAnyTicks = now;
                if (lastPingTicks == 0 || echoedUs != lastPingSendUs)
                {
                    return;
                }

                uint nowUs = ToMicroseconds(now);
                uint rttUs = unchecked(nowUs - lastPingSendUs);
                if (rttUs == 0 || rttUs > 2000000)
                {
                    return;
                }

                uint offset = unchecked(header.TimestampUs - lastPingSendUs - rttUs / 2);
                clockOffsetUs = unchecked((int)offset);
                offsetValid = true;
                lastPongTicks = now;
                lastDiagnosticPong = echoedUs;
                rttMs = rttUs / 1000.0;
                log.Info(
                    "Pong.Recv",
                    ("pingToken", echoedUs),
                    ("rttMs", Math.Round(rttMs, 1)),
                    ("offsetMs", Math.Round(clockOffsetUs / 1000.0, 1)));
            }
        }

        private void HandleHello(byte[] data, int length, IPEndPoint from, long now)
        {
            int payloadLength = length - WifiTransport.HeaderSize;
            if (payloadLength < 1 || payloadLength > 64)
            {
                AddMalformedLocked("hello_length");
                return;
            }

            string name = WifiTransport.ReadUtf8Z(data, WifiTransport.HeaderSize, payloadLength);
            bool changed = false;
            lock (sync)
            {
                RememberPeerLocked(from, now, name);
                if (!AcceptSourceLocked(from)) return;
                lastAnyTicks = now;
                if (state != SinkState.Armed && sessionEndpoint != null && from != null &&
                    !SameEndpoint(sessionEndpoint, from))
                {
                    foreign++;
                    return;
                }

                if (state == SinkState.Armed && from != null)
                {
                    // 待机时 HELLO 更新 PING 候选与显示端点；不创建会话。
                    remote = from;
                }

                if (!string.IsNullOrEmpty(name) && name != remoteName)
                {
                    remoteName = name;
                    changed = true;
                }

                if (state == SinkState.Armed)
                {
                    detail = string.IsNullOrEmpty(remoteName) ? "已发现手机" : "已发现 " + remoteName;
                }
            }

            if (changed)
            {
                log.Info("Wifi.Hello", ("name", name), ("endpoint", from == null ? "?" : from.ToString()));
            }
        }

        private void AddMalformedLocked(string reason)
        {
            lock (sync)
            {
                malformed++;
                malformedPending++;
                malformedReason = reason;
            }
        }

        // ===== 播放线程 =====

        private void PlayoutLoop()
        {
            while (running)
            {
                long now = clock.ElapsedTicks;
                try
                {
                    bool needOpen = false;
                    AudioOutput release = null;
                    lock (sync)
                    {
                        needOpen = ProcessLocked(now);
                        release = pendingRelease;
                        pendingRelease = null;
                    }

                    // 释放 WasapiOut 可能阻塞数百毫秒甚至更久；在接收锁外执行，
                    // 避免 UDP 接收线程被卡住导致整段音频丢在 socket 缓冲区里。
                    if (release != null)
                    {
                        release.Dispose();
                    }

                    if (needOpen && running)
                    {
                        OpenOutput();
                    }

                    Housekeeping(now);
                }
                catch (Exception ex)
                {
                    if (!running)
                    {
                        break;
                    }
                    log.Error("Wifi.Fault", ("code", "PlayoutFailed"), ("detail", ex.Message));
                    lock (sync)
                    {
                        state = SinkState.Faulted;
                        detail = "音频播放失败：" + ex.Message;
                        DetachOutputLocked();
                        playing = false;
                    }
                }

                Thread.Sleep(2);
            }
        }

        private void OpenOutput()
        {
            WifiOutputOptions preferences;long revision;
            lock(sync){preferences=outputOptions;revision=outputRevision;}
            AudioOutput created = createOutput(preferences);
            bool ok = created.EnsureInitialized();

            lock (sync)
            {
                if (ok && revision == outputRevision && running && output == null && state != SinkState.Faulted && state != SinkState.Armed)
                {
                    output = created;
                    created = null;
                }
                else if (!ok && revision == outputRevision)
                {
                    state = SinkState.Faulted;
                    detail = created.FailureReason ?? "音频输出不可用";
                }
            }

            if (created != null)
            {
                created.Dispose();
            }
        }

        private bool ProcessLocked(long now)
        {
            if(output?.PlaybackError!=null){state=SinkState.Faulted;detail=output.PlaybackError;}
            if (state == SinkState.Faulted)
            {
                DetachOutputLocked();
                playing = false;
                return false;
            }

            if (draining && stopTicks != 0 && ToMilliseconds(now - stopTicks) > DrainTimeoutMs)
            {
                DrainDoneLocked(now);
                return false;
            }

            if (state != SinkState.Armed && lastAudioTicks != 0 &&
                ToMilliseconds(now - lastAudioTicks) > TimeoutMs)
            {
                TimeoutLocked(now);
                return false;
            }

            if (state == SinkState.Armed)
            {
                DetachOutputLocked();
                playing = false;
                return false;
            }

            if (now - lastAdaptTicks >= TicksFromMs(500))
            {
                lastAdaptTicks = now;
                AdaptLocked(now);
            }

            FlushReorderLocked(now);
            int depth = DepthLocked();
            if (output == null)
            {
                if (depth >= targetMs || (draining && depth > 0)) return true;
                if (draining && depth == 0) DrainDoneLocked(now);
                return false;
            }

            output.TargetMs = targetMs;
            MoveFramesToOutputLocked(queue.Count);
            if (draining) output.EndInput();
            if (!playing && (output.BufferedMs >= Math.Max(targetMs,output.MinimumStartBufferMs) || (draining && output.BufferedMs > 0)))
            {
                output.Play();
                if(output.PlaybackError!=null){state=SinkState.Faulted;detail=output.PlaybackError;return false;}
                playing = output.IsPlaying;
                log.Info("Wifi.Playing", ("bufferMs", DepthLocked()));
            }
            if (playing)
            {
                long actual = output.ActualUnderruns;
                if (actual > underruns) underrunPending += (int)(actual - underruns);
                underruns = actual;
                bool wasRecovering = rebuffering;
                rebuffering = output.Recovering;
                if (wasRecovering && !rebuffering)
                {
                    rebufferPending++;
                    lastRebufferMs = output.BufferedMs;
                }
                state = rebuffering || draining ? SinkState.LinkReady : SinkState.Flowing;
                detail = draining ? "排空缓冲中" : rebuffering ? "实际输出欠载，重缓冲中" : "接收中";
            }
            if (draining && queue.Count == 0 && reorder.Count == 0 && output.BufferedMs == 0)
            {
                if (drainEmptyTicks == 0) drainEmptyTicks = now;
                if (now - drainEmptyTicks >= TicksFromMs(100)) DrainDoneLocked(now);
            }
            else drainEmptyTicks = 0;
            return false;
        }

        private void MoveFramesToOutputLocked(int count)
        {
            for (int i = 0; i < count && queue.Count > 0; i++)
            {
                AudioFrame frame = queue[0];
                queue.RemoveAt(0);
                try
                {
                    if (output != null)
                    {
                        if (frame.Silence)
                        {
                            output.AddSilence();
                            fadeInPending = true;
                        }
                        else
                        {
                            if (frame.FadeIn || fadeInPending)
                            {
                                output.AddFadeIn(frame.Data, 0, WifiTransport.PayloadBytes);
                                fadeInPending = false;
                            }
                            else output.Add(frame.Data, 0, WifiTransport.PayloadBytes);
                        }
                    }
                }
                finally { ReturnFrame(frame); }
            }
        }

        private void AdaptLocked(long now)
        {
            PruneJitterLocked(now);
            PruneLossLocked(now);

            double sum = 0;
            foreach (JitterSample sample in jitterSamples)
            {
                sum += sample.DeltaUs;
            }
            jitterUs = jitterSamples.Count > 0 ? sum / jitterSamples.Count : 0;

            int targetUs = (int)Math.Round(40000 + 4.0 * jitterUs);
            targetMs = Math.Clamp((int)Math.Ceiling(targetUs / 1000.0), baseMs, maxMs);
            // Only initial/recovery thresholds change. Never insert zeros or delete
            // healthy music to chase an instantaneous queue-depth measurement.
            if (output != null) output.TargetMs = targetMs;
        }

        private void UpdateJitterLocked(long now, WifiTransport.WifiHeader header)
        {
            if (prevAudioValid && header.Seq == prevAudioSeq + 1)
            {
                uint arrivalDiffUs = unchecked(ToMicroseconds(now) - ToMicroseconds(prevArrivalTicks == 0 ? now : prevArrivalTicks));
                uint sampleDiffUs = unchecked(header.TimestampUs - prevAudioTimestampUs);
                if (sampleDiffUs > 0 && sampleDiffUs <= 100000)
                {
                    JitterSample sample = new JitterSample();
                    sample.Ticks = now;
                    sample.DeltaUs = Math.Abs((double)arrivalDiffUs - sampleDiffUs);
                    jitterSamples.Enqueue(sample);
                }
            }

            prevAudioValid = true;
            prevAudioSeq = header.Seq;
            prevAudioTimestampUs = header.TimestampUs;
            prevArrivalTicks = now;
            PruneJitterLocked(now);
        }

        private void PruneJitterLocked(long now)
        {
            long cutoff = now - TicksFromMs(JitterWindowMs);
            while (jitterSamples.Count > 0 && jitterSamples.Peek().Ticks < cutoff)
            {
                jitterSamples.Dequeue();
            }
        }

        private void AddLossSampleLocked(int receivedCount, int missingCount)
        {
            LossSample sample = new LossSample();
            sample.Ticks = clock.ElapsedTicks;
            sample.Received = receivedCount;
            sample.Missing = missingCount;
            lossWindow.Enqueue(sample);
            PruneLossLocked(clock.ElapsedTicks);
        }

        private void PruneLossLocked(long now)
        {
            long cutoff = now - TicksFromMs(LockWindowMs);
            while (lossWindow.Count > 0 && lossWindow.Peek().Ticks < cutoff)
            {
                lossWindow.Dequeue();
            }
        }

        private double LossRateLocked(long now)
        {
            PruneLossLocked(now);
            long recv = 0;
            long miss = 0;
            foreach (LossSample sample in lossWindow)
            {
                recv += sample.Received;
                miss += sample.Missing;
            }
            if (recv + miss == 0)
            {
                return double.NaN;
            }
            return (double)miss / (recv + miss) * 100.0;
        }

        private int DepthLocked()
        {
            int provider = output == null ? 0 : output.BufferedMs;
            return provider + (queue.Count + reorder.Count) * WifiTransport.FrameMs;
        }

        // ===== 状态与生命周期 =====

        private void BeginSessionLocked(long now, bool fromStart, uint startUs)
        {
            ClearQueueLocked();
            // Stop the old consumer before a new session starts prebuffering.
            // Clearing an actively playing provider would manufacture startup underruns.
            if (output != null) DetachOutputLocked();

            expectedValid = false;
            expectedSeq = 0;
            firstAudio = false;
            playing = false;
            draining = false;
            rebuffering = false;
            fadeInPending = false;
            hasSessionStart = fromStart && startUs != 0;
            sessionStartUs = startUs;
            lastAudioTicks = now;
            lastTimestampUs = 0;
            lastSignalTicks = 0;
            prevAudioValid = false;
            prevArrivalTicks = 0;
            lastAdaptTicks = now;
            lastStatsLogTicks = now;
            lastAggregateLogTicks = now;
            targetMs = baseMs;
            jitterUs = 0;
            jitterSamples.Clear();
            lossWindow.Clear();
            drainEmptyTicks = 0;
            stopTicks = 0;
            received = 0;
            missing = 0;
            duplicates = 0;
            late = 0;
            resets = 0;
            underruns = 0;
            discontinuities = 0;
            dropped = 0;
            stale = 0;
            gapPending = 0;
            malformedPending = 0;
            underrunPending = 0;
            rebufferPending = 0;
            offsetValid = false;
            rttMs = double.NaN;
            lastPingTicks = 0;
            lastPongTicks = 0;
            state = SinkState.LinkReady;
            detail = "预缓冲中";
        }

        private void TimeoutLocked(long now)
        {
            long silentMs = lastAudioTicks == 0 ? 0 : (long)Math.Round(ToMilliseconds(now - lastAudioTicks));
            GoArmedLocked("等待手机发送音频");
            log.Info("Wifi.Timeout", ("silentMs", silentMs));
        }

        private void DrainDoneLocked(long now)
        {
            long drainedMs = stopTicks == 0 ? 0 : (long)Math.Round(ToMilliseconds(now - stopTicks));
            GoArmedLocked("等待手机连接");
            log.Info("Wifi.DrainDone", ("drainedMs", drainedMs));
        }

        private void GoArmedLocked(string text)
        {
            ClearQueueLocked();
            DetachOutputLocked();
            playing = false;
            draining = false;
            rebuffering = false;
            fadeInPending = false;
            expectedValid = false;
            firstAudio = false;
            hasSessionStart = false;
            sessionEndpoint = null;
            drainEmptyTicks = 0;
            stopTicks = 0;
            lastAudioTicks = 0;
            lastTimestampUs = 0;
            lastSignalTicks = 0;
            prevAudioValid = false;
            prevArrivalTicks = 0;
            targetMs = baseMs;
            jitterSamples.Clear();
            jitterUs = 0;
            offsetValid = false;
            rttMs = double.NaN;
            lastPingTicks = 0;
            lastPongTicks = 0;
            state = SinkState.Armed;
            detail = text;
        }

        private void DetachOutputLocked()
        {
            if (output != null)
            {
                if (pendingRelease != null)
                {
                    pendingRelease.Dispose();
                }
                pendingRelease = output;
                output = null;
            }
        }

        private void ClearQueueLocked()
        {
            for (int i = 0; i < queue.Count; i++)
            {
                ReturnFrame(queue[i]);
            }
            queue.Clear();
            foreach (AudioFrame frame in reorder.Values) ReturnFrame(frame);
            reorder.Clear();
            gapSinceTicks = 0;
        }

        private static void ReturnFrame(AudioFrame frame)
        {
            if (frame != null && frame.Data != null)
            {
                ArrayPool<byte>.Shared.Return(frame.Data);
                frame.Data = null;
            }
        }

        private bool IsCurrentEndpointLocked(IPEndPoint from)
        {
            IPEndPoint endpoint = sessionEndpoint ?? remote;
            return endpoint == null || SameEndpoint(endpoint, from);
        }

        private static bool SameEndpoint(IPEndPoint a, IPEndPoint b)
        {
            return a != null && b != null && a.Port == b.Port && a.Address.Equals(b.Address);
        }

        // ===== 定时任务 =====

        private void Housekeeping(long now)
        {
            IPEndPoint pingTarget = null;
            uint pingUs = 0;

            lock (sync)
            {
                FlushAggregatesLocked(now);
                LogStatsLocked(now);

                if (!externalTransport && state != SinkState.Faulted && remote != null && lastAnyTicks != 0 &&
                    now - lastAnyTicks <= TicksFromMs(LockWindowMs) &&
                    (lastPingTicks == 0 || now - lastPingTicks >= TicksFromMs(PingIntervalMs)))
                {
                    lastPingTicks = now;
                    lastPingSendUs = ToMicroseconds(now);
                    pingUs = lastPingSendUs;
                    pingTarget = sessionEndpoint ?? remote;
                }
            }

            if (pingTarget != null)
            {
                SendControlPacket(
                    WifiTransport.BuildPacket(WifiTransport.TypePing, WifiTransport.FlagDiagnostics, 0, pingUs, null),
                    pingTarget);
            }
        }

        private void FlushAggregatesLocked(long now)
        {
            if (now - lastAggregateLogTicks < TicksFromMs(AggregateIntervalMs))
            {
                return;
            }
            lastAggregateLogTicks = now;

            if (gapPending > 0)
            {
                log.Warn("Audio.Gap", ("missing", gapPending), ("seq", gapLastSeq));
                gapPending = 0;
            }

            if (malformedPending > 0)
            {
                log.Warn("Packet.Malformed", ("count", malformedPending), ("reason", malformedReason));
                malformedPending = 0;
            }

            if (underrunPending > 0)
            {
                log.Warn("Audio.Underrun", ("count", underrunPending));
                underrunPending = 0;
            }

            if (rebufferPending > 0)
            {
                log.Info("Audio.Rebuffer", ("count", rebufferPending), ("bufferMs", lastRebufferMs));
                rebufferPending = 0;
            }
        }

        private void LogStatsLocked(long now)
        {
            if (now - lastStatsLogTicks < TicksFromMs(StatsIntervalMs))
            {
                return;
            }

            if (state == SinkState.Armed && (lastAnyTicks == 0 || now - lastAnyTicks > TicksFromMs(LockWindowMs)))
            {
                return;
            }
            lastStatsLogTicks = now;

            double loss = LossRateLocked(now);
            long recv = 0;
            long miss = 0;
            foreach (LossSample sample in lossWindow)
            {
                recv += sample.Received;
                miss += sample.Missing;
            }

            log.Info(
                "Wifi.Stats",
                ("state", state.ToString()),
                ("rttMs", double.IsNaN(rttMs) ? (object)null : Math.Round(rttMs, 1)),
                ("offsetMs", offsetValid ? Math.Round(clockOffsetUs / 1000.0, 1) : (object)null),
                ("bufferMs", DepthLocked()),
                ("targetMs", targetMs),
                ("lossPct", double.IsNaN(loss) ? (object)null : Math.Round(loss, 2)),
                ("received", recv),
                ("missing", miss),
                ("dup", duplicates),
                ("late", late),
                ("resets", resets),
                ("underruns", output == null ? underruns : output.ActualUnderruns),
                ("outputPaddingFrames", output == null ? 0L : output.PaddingFrames),
                ("outputOverflows", output == null ? 0L : output.Overflows),
                ("mmcss", output != null && output.MmcssActive),
                ("maxReadGapMs", output == null ? 0 : Math.Round(output.MaxReadGapMs, 2)),
                ("inputFrames", output == null ? 0L : output.InputFrames),
                ("recentInputSignal", lastSignalTicks != 0 && now - lastSignalTicks < TicksFromMs(1200)),
                ("outputActive", playing && !rebuffering && output != null && output.IsPlaying),
                ("outputMode", (output?.Mode ?? AudioOutputMode.None).ToString()),
                ("softwareVolume", outputOptions.VolumePercent),
                ("maxInputZeroRunFrames", output == null ? 0L : output.MaxInputZeroRunFrames),
                ("discontinuities", discontinuities),
                ("malformed", malformed),
                ("foreign", foreign),
                ("stale", stale),
                ("unknown", unknown));
        }

        private void SendControlPacket(byte[] packet, IPEndPoint target)
        {
            Socket socket = controlSocket;
            if (socket == null || target == null || packet == null)
            {
                return;
            }

            try
            {
                socket.SendTo(packet, target);
            }
            catch (Exception ex)
            {
                if (DateTime.Now - lastSendErrorLog > TimeSpan.FromSeconds(30))
                {
                    lastSendErrorLog = DateTime.Now;
                    log.Warn("Wifi.Fault", ("code", "SendFailed"), ("detail", ex.Message));
                }
            }
        }

        public WifiSnapshot GetSnapshot()
        {
            lock (sync)
            {
                long now = clock.ElapsedTicks;
                PruneLossLocked(now);
                int bufferMs = DepthLocked();
                double loss = LossRateLocked(now);
                double latencyMs = double.NaN;

                if (playing && !rebuffering && offsetValid && lastTimestampUs != 0 &&
                    lastPongTicks != 0 && now - lastPongTicks <= TicksFromMs(LockWindowMs))
                {
                    uint nowUs = ToMicroseconds(now);
                    int ageUs = unchecked((int)(nowUs - lastTimestampUs)) + clockOffsetUs;
                    if (ageUs >= 0 && ageUs <= 2000000)
                    {
                        latencyMs = ageUs / 1000.0 + (output?.PlaybackLatencyMs ?? 50);
                    }
                }

                WifiSnapshot snapshot = new WifiSnapshot();
                snapshot.Enabled = true;
                snapshot.LastAudioAgeMs = lastAudioTicks == 0 ? double.NaN : ToMilliseconds(now - lastAudioTicks);
                snapshot.ControlAgeMs = lastPongTicks == 0 ? double.NaN : ToMilliseconds(now - lastPongTicks);
                snapshot.HasAudibleSignal = outputOptions.VolumePercent > 0 && lastSignalTicks != 0 && now - lastSignalTicks < TicksFromMs(1200);
                snapshot.OutputActive = playing && !rebuffering && output != null && output.IsPlaying;
                snapshot.Draining = draining;
                snapshot.Recovering = rebuffering;
                snapshot.OutputName = output == null ? null : output.OutputName;
                snapshot.OutputMode = output?.Mode ?? AudioOutputMode.None;
                snapshot.OutputFormat = output?.OutputFormat;
                snapshot.OutputBufferMs = output?.PlaybackLatencyMs ?? 0;
                snapshot.EffectivePrefillMs = Math.Max(targetMs,output?.MinimumStartBufferMs ?? 0);
                snapshot.OutputFallbackReason = output?.FallbackReason;
                snapshot.ExclusiveRequested = outputOptions.Exclusive;
                snapshot.VolumePercent = outputOptions.VolumePercent;
                snapshot.State = state;
                snapshot.Detail = detail;
                snapshot.Remote = remote == null ? null : remote.ToString();
                snapshot.DeviceName = remoteName;
                snapshot.RttMs = rttMs;
                snapshot.BufferMs = bufferMs;
                snapshot.TargetMs = targetMs;
                snapshot.LatencyMs = latencyMs;
                snapshot.LossPercent = loss;
                snapshot.Received = received;
                snapshot.Missing = missing;
                snapshot.Duplicates = duplicates;
                snapshot.Late = late;
                snapshot.Resets = resets;
                snapshot.Underruns = output == null ? underruns : output.ActualUnderruns;
                snapshot.Discontinuities = discontinuities;
                snapshot.Malformed = malformed;
                snapshot.Foreign = foreign;
                snapshot.Stale = stale;
                snapshot.Unknown = unknown;
                snapshot.Dropped = dropped;
                snapshot.Port = audioPort;
                return snapshot;
            }
        }

        private static void JoinThread(Thread thread, int milliseconds)
        {
            if (thread == null)
            {
                return;
            }

            try
            {
                thread.Join(milliseconds);
            }
            catch
            {
            }
        }

        private static void CloseSocket(Socket socket)
        {
            if (socket == null)
            {
                return;
            }

            try
            {
                socket.Close();
            }
            catch
            {
            }
        }

        private static uint ToMicroseconds(long ticks)
        {
            return unchecked((uint)(ticks * 1000000L / Stopwatch.Frequency));
        }

        private static double ToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static long TicksFromMs(int milliseconds)
        {
            return Stopwatch.Frequency * milliseconds / 1000L;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
