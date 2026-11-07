using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SinkLite.Platform;

namespace SinkLite.Core.Bas2
{
    /// <summary>Verified BAS2 media -> the existing PCM/WASAPI engine. Never runs playback or logging on socket callbacks.</summary>
    internal sealed class PlaybackReceiver : IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly Queue<MediaFrame> pending = new(40);
        private readonly Queue<ReceiverPeerSnapshot> trace = new();
        private readonly Dictionary<string,string> phases = new();
        private readonly AutoResetEvent wake = new(false);
        private readonly WifiReceiver playback;
        private readonly ControlServer server;
        private readonly Log log;
        private ReceiverPeerSnapshot incoming;
        private ReceiverPeerSnapshot processed;
        private ReceiverPeerSnapshot lastEnded;
        private bool endRequested;
        private Thread worker;
        private bool running;
        private long overflow;
        private long receivedAtIngress;
        private long nextStats;
        private bool disposed;
        public long DroppedAtIngress => Interlocked.Read(ref overflow);
        public long ReceivedAtIngress => Interlocked.Read(ref receivedAtIngress);
        public int ControlPort => server.ControlPort;
        public int MediaPort => server.MediaPort;

        public PlaybackReceiver(Log log, int controlPort, int mediaPort, int bufferBaseMs, int bufferMaxMs,
            string identity, string computerName, WifiOutputOptions options = null,
            Func<WifiOutputOptions, AudioOutput> createOutput = null, bool answerUdpProbes = true)
        {
            this.log = log;
            playback = new WifiReceiver(log, controlPort, mediaPort, bufferBaseMs, bufferMaxMs, computerName, options, createOutput);
            server = new ControlServer(IPAddress.Any, controlPort, mediaPort, identity, computerName, answerUdpProbes);
            server.PeerChanged += OnPeer;
            server.AudioReceived += OnAudio;
        }

        public void Start()
        {
            lock (gate)
            {
                if (running || disposed) throw new InvalidOperationException("BAS2 playback receiver cannot restart");
                if (!playback.StartPlaybackOnly()) throw new InvalidOperationException("Playback engine startup failed");
                try { server.Start(); }
                catch { playback.Stop(); throw; }
                running = true;
                worker = new Thread(Process) { IsBackground = true, Name = "BAS2PlaybackIngress" };
                worker.Start();
            }
        }

        private static string StreamKey(ReceiverPeerSnapshot peer) => peer == null ? null : peer.SessionId + "/" + peer.StreamEpoch.ToString("x16");

        private void OnPeer(ReceiverPeerSnapshot peer)
        {
            lock (gate)
            {
                if (!running) return;
                string phase = peer.Phase + "/" + peer.StreamEpoch;
                if (!phases.TryGetValue(peer.SessionId,out var previous) || previous != phase || peer.Error != null) {
                    if (trace.Count == 64) trace.Dequeue(); trace.Enqueue(peer); phases[peer.SessionId] = phase;
                    if (peer.Phase == ConnectionPhase.Closed) phases.Remove(peer.SessionId);
                }
                if (peer.Phase == ConnectionPhase.Streaming)
                {
                    if (StreamKey(incoming) != StreamKey(peer)) pending.Clear();
                    incoming = peer;
                    endRequested = false;
                }
                else if (incoming?.SessionId == peer.SessionId)
                {
                    lastEnded = peer;
                    if (peer.Phase == ConnectionPhase.Closed) { incoming = null; pending.Clear(); endRequested = false; }
                    else endRequested = true;
                }
                wake.Set();
            }
        }

        private void OnAudio(MediaFrame frame)
        {
            lock (gate)
            {
                if (!running) return;
                Interlocked.Increment(ref receivedAtIngress);
                if (pending.Count == 40) Interlocked.Increment(ref overflow);
                else pending.Enqueue(frame);
                wake.Set();
            }
        }

        private void Process()
        {
            while (true)
            {
                ReceiverPeerSnapshot current, ended;
                MediaFrame frame;
                ReceiverPeerSnapshot evidence;
                lock (gate)
                {
                    if (!running) break;
                    current = incoming;
                    ended = lastEnded;
                    frame = pending.Count == 0 ? null : pending.Dequeue();
                    evidence = trace.Count == 0 ? null : trace.Dequeue();
                    // Audio accepted before STOP must reach finite drain. A disconnected or
                    // replaced stream is discarded immediately; the server rejects post-STOP audio.
                    if (endRequested && frame == null) { current = incoming = null; endRequested = false; }
                }
                try
                {
                    if (evidence != null) log.Info("Bas2.Session", ("appRunId",evidence.AppRunId),("attemptId",evidence.AttemptId),("pathId",evidence.PathId),
                        ("sessionId",evidence.SessionId),("streamEpoch",evidence.StreamEpoch),("phase",evidence.Phase.ToString()),("tcp",evidence.UsingTcp),
                        ("local",evidence.LocalAddress),("remote",evidence.RemoteAddress),("error",evidence.Error));
                    if (StreamKey(current) != StreamKey(processed))
                    {
                        if (processed != null)
                            playback.EndVerifiedStream(StreamKey(processed), current != null || ended?.Phase == ConnectionPhase.Closed);
                        if (current != null)
                            playback.BeginVerifiedStream(StreamKey(current), new IPEndPoint(current.RemoteAddress, 0), current.PhoneName);
                        processed = current;
                    }
                    if (current != null && frame != null && frame.StreamEpoch == current.StreamEpoch &&
                        Convert.ToHexString(frame.SessionId).Equals(current.SessionId, StringComparison.OrdinalIgnoreCase))
                        playback.AcceptVerifiedPcm(StreamKey(current), frame.Payload, frame.Sequence, frame.TimestampUs, frame.Flags);
                    long now = Environment.TickCount64;
                    if (now >= nextStats)
                    {
                        nextStats = now + 5000;
                        log.Info("Bas2.PlaybackIngress", ("sessionId", current?.SessionId), ("streamEpoch", current?.StreamEpoch),
                            ("transportReceived", ReceivedAtIngress), ("queueDropped", DroppedAtIngress));
                    }
                }
                catch (Exception error)
                {
                    log.Error("Bas2.PlaybackIngressFailed", ("sessionId", current?.SessionId), ("error", error.Message));
                }
                if (frame == null) wake.WaitOne(100);
            }
        }

        public void SetOutputOptions(WifiOutputOptions options) => playback.SetOutputOptions(options);
        public void SetBufferBase(int milliseconds) => playback.SetBufferBase(milliseconds);
        public void RefreshOutput() => playback.RefreshOutput();
        public ReceiverPeerSnapshot[] GetPeers() => server.GetPeers();
        public void SelectPeer(string instanceId) => server.SelectPeer(instanceId);
        public Func<ReceiverPeerSnapshot, ControlMessage, Task<ApplicationReply>> ApplicationHandler { set => server.ApplicationHandler = value; }

        public WifiSnapshot GetSnapshot()
        {
            var snapshot = playback.GetSnapshot();
            var peers = server.GetPeers();
            var peer = peers.FirstOrDefault(item => item.Phase == ConnectionPhase.Streaming) ??
                peers.FirstOrDefault(item => item.Phase == ConnectionPhase.Ready) ?? peers.FirstOrDefault(item => item.Phase == ConnectionPhase.VerifyingTransport);
            if (peer != null)
            {
                snapshot.Remote = new IPEndPoint(peer.RemoteAddress, 0).ToString();
                snapshot.DeviceName = peer.PhoneName;
                snapshot.LocalAddress = peer.LocalAddress.ToString();
                snapshot.ControlAgeMs = Math.Max(0, Environment.TickCount64 - peer.LastHeartbeatMs);
                if (peer.Phase == ConnectionPhase.VerifyingTransport) { snapshot.State = SinkState.Busy; snapshot.Detail = "正在验证手机传输"; snapshot.ControlAgeMs = double.NaN; }
                snapshot.RttMs = peer.RttMs;
                snapshot.CompatibleTransport = peer.UsingTcp;
                if (snapshot.State == SinkState.Armed) snapshot.Detail = "已连接，等待手机发送音频";
                if (peer.UsingTcp) snapshot.Detail += "（兼容传输）";
            }
            snapshot.Dropped += DroppedAtIngress;
            return snapshot;
        }

        public async ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                running = false;
                incoming = null;
                pending.Clear();
                wake.Set();
            }
            await server.DisposeAsync().ConfigureAwait(false);
            // The ingress thread must exit before its playback engine and wait handle are released.
            if (worker != null) await Task.Run(() => worker.Join()).ConfigureAwait(false);
            playback.Dispose();
            wake.Dispose();
        }
    }
}
