using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SinkLite.Platform;

namespace SinkLite.Core.Bas2
{
    internal sealed class WifiReceiverFacade : IWifiReceiver
    {
        private readonly object gate = new();
        private readonly PlaybackReceiver playback;
        private readonly DiscoveryServer discovery;
        private readonly Log log;
        private WifiOutputOptions options;
        private readonly string epoch = Guid.NewGuid().ToString("N");
        private uint revision = 1;
        private bool started, stopped;
        private string legacyWarning;
        public event Action RemoteVolumeChanged;
        public WifiReceiverFacade(Log log, int controlPort, int audioPort, int bufferBaseMs, int bufferMaxMs, string name, WifiOutputOptions options)
        {
            this.log = log; this.options = options;
            playback = new PlaybackReceiver(log, controlPort, audioPort, bufferBaseMs, bufferMaxMs, WifiIdentity.Current, name, options);
            playback.ApplicationHandler = Handle;
            discovery = new DiscoveryServer(WifiIdentity.Current, name, audioPort, controlPort);
            discovery.LegacyPhone += () => legacyWarning = "检测到旧版手机，请同时更新手机端和电脑端";
        }
        public bool Start()
        {
            try {
                playback.Start();
                try { discovery.Start(); } catch (System.Net.Sockets.SocketException error) { log.Warn("Bas2.DiscoveryUnavailable",("nativeError",error.NativeErrorCode),("error",error.Message)); }
                started = true; log.Info("Bas2.Started", ("protocol", 2)); return true;
            }
            catch (Exception error) { log.Error("Bas2.StartFailed", ("error", error.ToString())); Stop(); return false; }
        }
        private JsonObject Volume(bool accepted = true) => new() { ["epoch"] = epoch, ["revision"] = revision,
            ["percent"] = options.VolumePercent, ["exclusive"] = options.Exclusive, ["accepted"] = accepted };
        private async Task<ApplicationReply> Handle(ReceiverPeerSnapshot peer, ControlMessage request)
        {
            if (request.Type == "diagnostics") return await Task.Run(() => {
                if (!request.Body.TryGetProperty("events", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 6)
                    return new ApplicationReply("error", new JsonObject { ["code"] = "InvalidDiagnostics" });
                var ids = new JsonArray();
                foreach (var row in rows.EnumerateArray()) {
                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) throw new System.IO.InvalidDataException("Invalid diagnostic row");
                    string id = row[0].GetString(), code = row[2].GetString();
                    if (id?.Length != 32 || !Guid.TryParseExact(id, "N", out _) || code == null || code.Length > 96) throw new System.IO.InvalidDataException("Invalid diagnostic event");
                    string context = row.GetArrayLength() > 6 ? row[6].GetRawText() : null;
                    if (context?.Length > 2048) throw new System.IO.InvalidDataException("Diagnostic context too large");
                    if (!log.WriteAndroidEvent(id, row[1].GetInt64(), code, row[3].GetInt64(), row[4].GetInt64(), row[5].GetInt64(), 0,
                        "deliverySession=" + peer.SessionId + "; " + context)) return new ApplicationReply("error", new JsonObject { ["code"] = "LogWriteFailed" });
                    ids.Add((JsonNode)JsonValue.Create(id));
                }
                return new ApplicationReply("diagnosticsAck", new JsonObject { ["ids"] = ids });
            }).ConfigureAwait(false);
            bool changed = false; JsonObject state;
            lock (gate) {
                if (request.Type == "volumeSet") {
                    var body = request.Body;
                    bool valid = body.TryGetProperty("epoch", out var e) && e.GetString() == epoch &&
                        body.TryGetProperty("revision", out var r) && r.TryGetUInt32(out uint value) && value == revision &&
                        body.TryGetProperty("percent", out var p) && p.TryGetInt32(out int percent) && percent >= 0 && percent <= 100;
                    if (valid) { options = new WifiOutputOptions(options.Exclusive, body.GetProperty("percent").GetInt32(), options.EndpointId); revision++; playback.SetOutputOptions(options); changed = true; }
                    state = Volume(valid);
                } else state = Volume();
            }
            if (changed) RemoteVolumeChanged?.Invoke();
            return new ApplicationReply("volumeState", state);
        }
        public void SetOutputOptions(WifiOutputOptions value)
        { lock (gate) { if (options.VolumePercent != value.VolumePercent || !options.SameDeviceAndMode(value)) revision++; options = value; playback.SetOutputOptions(value); } }
        public void SetBufferBase(int milliseconds) => playback.SetBufferBase(milliseconds);
        public void RefreshOutput() => playback.RefreshOutput();
        public bool SendOffer(IEnumerable<IPAddress> targets = null) => started && !stopped && discovery.SendOffer();
        public IReadOnlyList<WifiPeer> ListPeers() => playback.GetPeers().Where(p => p.InstanceId != null)
            .GroupBy(p => p.InstanceId).Select(group => group.OrderByDescending(p => p.Phase == ConnectionPhase.Streaming).First())
            .Select(p => new WifiPeer { Id = p.InstanceId, Name = p.PhoneName, Recent = true, Active = p.Phase == ConnectionPhase.Streaming, Selected = p.Phase == ConnectionPhase.Streaming }).ToArray();
        public bool SelectPeer(string id) { if (!ListPeers().Any(p => p.Id == id)) return false; playback.SelectPeer(id); return true; }
        public void PrepareReconnect(IPEndPoint peer, string name) { /* Android owns outbound control and recovery. */ }
        public void ResetNetworkPeers() { /* Listener accepts all actual local addresses; healthy routes remain intact. */ }
        public WifiSnapshot GetSnapshot() { var value = playback.GetSnapshot(); if (legacyWarning != null && (double.IsNaN(value.ControlAgeMs) || value.ControlAgeMs > 6000)) value.Detail = legacyWarning; return value; }
        public void Stop() { if (stopped) return; stopped = true; discovery.DisposeAsync().AsTask().GetAwaiter().GetResult(); playback.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        public void Dispose() => Stop();
    }
}
