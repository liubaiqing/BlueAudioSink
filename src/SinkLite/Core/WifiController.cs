using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SinkLite.Platform;

namespace SinkLite.Core
{
    /// <summary>
    /// 供 UI 读取的 Wi-Fi 状态快照。状态复用 SinkState：
    ///   Idle=模式关闭；Busy=绑定/切换；Armed=端口就绪无会话；
    ///   LinkReady=预缓冲/重缓冲；Flowing=播放中（静音也算）；Faulted=端口/播放器故障。
    /// </summary>
    internal sealed class WifiSnapshot
    {
        public bool Enabled { get; set; }
        public AudioOutputMode OutputMode { get; set; }
        public string OutputFormat { get; set; }
        public int OutputBufferMs { get; set; }
        public int EffectivePrefillMs { get; set; }
        public string OutputFallbackReason { get; set; }
        public bool ExclusiveRequested { get; set; }
        public int VolumePercent { get; set; } = 100;

        public SinkState State { get; set; } = SinkState.Idle;

        public string Detail { get; set; } = "Wi-Fi 模式未启用";

        /// <summary>手机端点（IP:端口），未连接为 null。</summary>
        public string Remote { get; set; }

        public string DeviceName { get; set; }

        /// <summary>最近有效 PONG 的 RTT；无样本为 NaN。</summary>
        public double RttMs { get; set; } = double.NaN;

        /// <summary>当前抖动缓冲深度 D（毫秒）。</summary>
        public int BufferMs { get; set; }

        /// <summary>lastTarget（毫秒）。</summary>
        public int TargetMs { get; set; }

        /// <summary>估算采集到播放延迟（毫秒）；无效为 NaN。</summary>
        public double LatencyMs { get; set; } = double.NaN;

        /// <summary>最近 10 秒窗口丢包率百分比；窗口无包为 NaN。</summary>
        public double LossPercent { get; set; } = double.NaN;

        public long Received { get; set; }

        public long Missing { get; set; }

        public long Duplicates { get; set; }

        public long Late { get; set; }

        public long Resets { get; set; }

        public long Underruns { get; set; }

        public long Discontinuities { get; set; }

        public long Malformed { get; set; }

        public long Foreign { get; set; }

        public long Stale { get; set; }

        public long Unknown { get; set; }

        public long Dropped { get; set; }

        public double LastAudioAgeMs { get; set; } = double.NaN;
        public double ControlAgeMs { get; set; } = double.NaN;
        public bool HasAudibleSignal { get; set; }
        public bool OutputActive { get; set; }
        public bool Draining { get; set; }
        public bool Recovering { get; set; }
        public string OutputName { get; set; }
        public int Port { get; set; }

        public string LocalAddress { get; set; }
        public long NetworkGeneration { get; set; }
        public bool CompatibleTransport { get; set; }

        public DateTime ChangedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Wi-Fi 模式生命周期：绑定/释放 45678+45679、启停接收器与 OFFER 定时器。
    /// 具体的收发、会话、抖动缓冲全部在 WifiReceiver 内完成。
    /// </summary>
    internal sealed class WifiController : IDisposable
    {
        private readonly Log log;
        private readonly object sync = new object();
        private readonly int audioPort;
        private WifiOutputOptions outputOptions;
        private int bufferBaseMs;
        private int bufferMaxMs;
        private readonly string deviceName;
        private string localAddress;
        private DateTime localAddressRefreshAt;
        private WifiLanAddress[] lanAddresses;
        private string lanSignature;
        private long networkGeneration;
        private readonly Func<WifiLanAddress[]> networkProvider;

        private IWifiReceiver receiver;
        private readonly bool legacyTestTransport;
        private System.Threading.Timer offerTimer;
        private bool enabled;
        private string lastError;
        private DateTime lastOfferLog = DateTime.MinValue;
        private DateTime lastOfferErrorLog = DateTime.MinValue;

        public WifiController(Log log, int audioPort, int bufferBaseMs, int bufferMaxMs, WifiOutputOptions outputOptions = null, Func<WifiLanAddress[]> networkProvider = null, bool legacyTestTransport = false)
        {
            this.log = log;
            this.legacyTestTransport = legacyTestTransport;
            this.outputOptions = outputOptions ?? new WifiOutputOptions();
            this.audioPort = audioPort;
            this.bufferBaseMs = bufferBaseMs;
            this.bufferMaxMs = bufferMaxMs;
            deviceName = string.IsNullOrWhiteSpace(Environment.MachineName) ? "BlueAudioSink" : Environment.MachineName;
            this.networkProvider=networkProvider ?? WifiLanAddresses.Read;
            lanAddresses=this.networkProvider();
            lanSignature=WifiLanAddresses.Signature(lanAddresses);
            localAddress=WifiLanAddresses.Choose(lanAddresses);
        }

        public bool Enabled
        {
            get
            {
                lock (sync)
                {
                    return enabled;
                }
            }
        }

        public int Port
        {
            get { return audioPort; }
        }

        /// <summary>
        /// 绑定 45678/45679 并启动接收器。返回 false 表示端口不可用；
        /// 此时不会启动 OFFER/PING，调用方应保持蓝牙模式。
        /// </summary>
        public event Action RemoteVolumeChanged;

        public bool Start()
        {
            lock (sync)
            {
                if (enabled && receiver != null)
                {
                    return true;
                }

                lastError = null;
                IWifiReceiver created = legacyTestTransport ? new WifiReceiver(
                    log,
                    WifiTransport.ControlPort,
                    audioPort,
                    bufferBaseMs,
                    bufferMaxMs,
                    deviceName,outputOptions) : new Bas2.WifiReceiverFacade(log, WifiTransport.ControlPort, audioPort, bufferBaseMs, bufferMaxMs, deviceName, outputOptions);

                created.RemoteVolumeChanged += () => RemoteVolumeChanged?.Invoke();
                if (!created.Start())
                {
                    lastError = "端口 " + WifiTransport.ControlPort + "/" + audioPort + " 绑定失败";
                    created.Dispose();
                    return false;
                }

                receiver = created;
                enabled = true;
                offerTimer = new System.Threading.Timer(OfferTick, null, 2000, 2000);
                OfferTick(null);

                log.Info(
                    "Wifi.Bind",
                    ("controlPort", WifiTransport.ControlPort),
                    ("audioPort", audioPort),
                    ("bufferBaseMs", bufferBaseMs),
                    ("bufferMaxMs", bufferMaxMs));
                log.Info("Wifi.ModeOn", ("mode", "wifi"));
                return true;
            }
        }

        public void SetBufferBase(int milliseconds)
        {
            lock (sync)
            {
                bufferBaseMs = Math.Clamp(milliseconds, 40, 800);
                bufferMaxMs = Math.Max(bufferMaxMs, bufferBaseMs);
                if (receiver != null) receiver.SetBufferBase(bufferBaseMs);
            }
        }

        public void SetOutputOptions(WifiOutputOptions options)
        {lock(sync){outputOptions=options;receiver?.SetOutputOptions(options);}}

        public void RefreshOutput()
        {
            lock (sync) { if (receiver != null) receiver.RefreshOutput(); }
        }

        public IReadOnlyList<WifiPeer> ListPeers()
        {
            lock (sync) { return receiver == null ? Array.Empty<WifiPeer>() : receiver.ListPeers(); }
        }

        public bool SelectPeer(string id)
        {
            lock (sync) { return receiver != null && receiver.SelectPeer(id); }
        }

        public void PrepareReconnect(string endpoint, string name, long? generation = null)
        {
            if(IPEndPoint.TryParse(endpoint,out var peer)) lock(sync) {
                RefreshNetworkLocked();
                if((generation.HasValue && generation.Value!=networkGeneration) || !WifiLanAddresses.OnLink(lanAddresses,peer.Address)) {
                    log.Info("Wifi.ReconnectHintDiscarded",("reason","network changed"));return;
                }
                receiver?.PrepareReconnect(peer,name);
            }
        }

        private void RefreshNetworkLocked(bool force = false)
        {
            if(!force && DateTime.UtcNow<localAddressRefreshAt) return;
            localAddressRefreshAt=DateTime.UtcNow.AddSeconds(2);
            var current=networkProvider();
            string signature=WifiLanAddresses.Signature(current);
            if(signature!=lanSignature) {
                networkGeneration++;
                receiver?.ResetNetworkPeers();
                log.Info("Wifi.LanChanged", ("generation", networkGeneration), ("ipv4Candidates", current.Length));
            }
            lanAddresses=current;lanSignature=signature;
            localAddress=WifiLanAddresses.Choose(current);
        }

        public bool RefreshDiscovery()
        {
            lock (sync) { RefreshNetworkLocked(true); return receiver != null && receiver.SendOffer(); }
        }

        public void Stop()
        {
            Stop("off");
        }

        public void Stop(string mode)
        {
            IWifiReceiver current;
            System.Threading.Timer timer;

            lock (sync)
            {
                if (!enabled && receiver == null)
                {
                    return;
                }

                enabled = false;
                current = receiver;
                receiver = null;
                timer = offerTimer;
                offerTimer = null;
            }

            if (timer != null)
            {
                timer.Dispose();
            }

            if (current != null)
            {
                current.Stop();
                current.Dispose();
            }

            log.Info("Wifi.ModeOff", ("mode", mode));
        }

        private void OfferTick(object state)
        {
            IWifiReceiver current;
            lock (sync)
            {
                RefreshNetworkLocked();
                if (!enabled)
                {
                    return;
                }
                current = receiver;
            }

            if (current == null)
            {
                return;
            }

            if (current.SendOffer())
            {
                if (DateTime.Now - lastOfferLog > TimeSpan.FromSeconds(30))
                {
                    lastOfferLog = DateTime.Now;
                    log.Info("Wifi.OfferSent", ("name", deviceName), ("audioPort", audioPort));
                }
            }
            else if (DateTime.Now - lastOfferErrorLog > TimeSpan.FromSeconds(30))
            {
                lastOfferErrorLog = DateTime.Now;
                log.Warn("Wifi.Fault", ("code", "OfferFailed"), ("detail", "OFFER 广播失败"));
            }
        }

        public WifiSnapshot Current
        {
            get
            {
                IWifiReceiver rx;
                string error;
                lock (sync)
                {
                    RefreshNetworkLocked();
                    rx = receiver;
                    error = lastError;
                }

                if (rx != null)
                {
                    WifiSnapshot snapshot = rx.GetSnapshot();
                    snapshot.Enabled = true;
                    snapshot.Port = audioPort;
                    lock (sync)
                    {
                        bool fresh=(snapshot.ControlAgeMs<=5000 || snapshot.LastAudioAgeMs<=5000);
                        snapshot.LocalAddress=WifiLanAddresses.Choose(lanAddresses,fresh ? snapshot.Remote : null);
                        snapshot.NetworkGeneration=networkGeneration;
                    }
                    return snapshot;
                }

                WifiSnapshot disabled = new WifiSnapshot();
                disabled.Enabled = false;
                lock(sync){disabled.ExclusiveRequested=outputOptions.Exclusive;disabled.VolumePercent=outputOptions.VolumePercent;disabled.TargetMs=bufferBaseMs;}
                disabled.State = string.IsNullOrEmpty(error) ? SinkState.Idle : SinkState.Faulted;
                disabled.Detail = string.IsNullOrEmpty(error) ? "Wi-Fi 模式未启用" : error;
                disabled.Port = audioPort;
                disabled.LocalAddress = localAddress;
                disabled.NetworkGeneration = networkGeneration;
                return disabled;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
