using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SinkLite.Platform;

namespace SinkLite.Core
{
    /// <summary>
    /// 播放输出：事件同步 WasapiOut（共享 50 ms / 独占 100 ms）+ PlayoutBuffer。
    ///
    /// 唯一缓冲由 WASAPI Read 消费，实际缺数在 Read 层计数并重缓冲。
    /// 所有方法都由接收器的播放线程串行调用，内部再加锁以兼容 UI 线程读取统计。
    /// </summary>
    internal sealed class AudioOutput : IDisposable
    {
        private const int BufferSeconds = 1;

        private readonly object sync = new object();
        private readonly Log log;
        private readonly PlayoutBuffer buffer;
        private readonly byte[] silence;
        private readonly byte[] fade;
        private IWavePlayer output;
        private IWasapiOutputDevice device;
        private readonly WifiOutputOptions options;
        private readonly Func<string,IWasapiOutputDevice> openDevice;
        private readonly PcmVolumeProvider volume;
        private string playbackError;
        private System.Threading.CancellationTokenSource playbackStop;
        public AudioOutputMode Mode { get; private set; }
        public string OutputFormat { get; private set; }
        public string FallbackReason { get; private set; }
        public string FailureReason { get; private set; }
        public string PlaybackError => System.Threading.Volatile.Read(ref playbackError);
        public int VolumePercent { get => volume.VolumePercent; set => volume.VolumePercent=value; }
        public int PlaybackLatencyMs => WasapiOutputDevice.BufferMilliseconds(Mode==AudioOutputMode.Exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared);
        public int MinimumStartBufferMs => Mode==AudioOutputMode.Exclusive ? PlaybackLatencyMs*2+20 : PlaybackLatencyMs;
        private bool disposed;
        public string OutputName { get; private set; }
        public bool IsPlaying { get { lock (sync) { return output != null && output.PlaybackState == PlaybackState.Playing; } } }

        public AudioOutput(Log log, WifiOutputOptions options = null, Func<string,IWasapiOutputDevice> openDevice = null)
        {
            this.log = log;
            this.options = options ?? new WifiOutputOptions();
            this.openDevice = openDevice ?? WasapiOutputDevice.Open;
            buffer = new PlayoutBuffer { UseMmcss = true };
            volume = new PcmVolumeProvider(buffer,this.options.VolumePercent);
            silence = new byte[WifiTransport.PayloadBytes];
            fade = new byte[WifiTransport.PayloadBytes];
        }

        public int BufferedMs
        {
            get
            {
                lock (sync)
                {
                    return buffer.BufferedMs;
                }
            }
        }

        public bool IsInitialized
        {
            get
            {
                lock (sync)
                {
                    return output != null;
                }
            }
        }

        /// <summary>
        /// 打开默认输出端点并完成 WasapiOut.Init。失败返回 false，由调用方记录并退避重试。
        /// 刻意不在 Init 之后立刻 Play：调用方要先预填缓冲，再开始播放。
        /// </summary>
        public bool EnsureInitialized()
        {
            lock(sync)
            {
                if(disposed)return false;
                if(output!=null)return true;
                FailureReason=null;FallbackReason=null;playbackError=null;
                if(options.Exclusive)
                {
                    try {InitializeMode(AudioOutputMode.Exclusive);return true;}
                    catch(Exception ex){ReleaseNative();FallbackReason=Explain(ex);log.Warn("Player.ExclusiveFallback",("reason",FallbackReason));}
                }
                try {InitializeMode(AudioOutputMode.Shared);return true;}
                catch(Exception ex){ReleaseNative();FailureReason="音频输出不可用："+Explain(ex);log.Error("Player.Fault",("code","CreateFailed"),("detail",FailureReason));return false;}
            }
        }

        private void InitializeMode(AudioOutputMode mode)
        {
            device=openDevice(options.EndpointId);
            IWaveProvider provider=volume;
            if(mode==AudioOutputMode.Exclusive)
            {
                WaveFormat format=null;
                foreach(int bits in new[]{16,24,32})
                {
                    foreach(WaveFormat candidate in new WaveFormat[]{new WaveFormat(48000,bits,2),new WaveFormatExtensible(48000,bits,2)})
                        if(device.SupportsExclusive(candidate)){format=candidate;break;}
                    if(format!=null)break;
                }
                if(format==null)throw new NotSupportedException("设备不支持 48kHz 立体声 PCM 独占输出");
                provider=new PcmBitDepthProvider(volume,format);
            }
            playbackStop=new System.Threading.CancellationTokenSource();
            if(mode==AudioOutputMode.Exclusive)provider=new PcmStartupGate(provider,buffer,playbackStop.Token);
            var player=device.CreatePlayer(mode==AudioOutputMode.Exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared);
            output=player; // Owned even if Init fails.
            player.Init(provider);
            player.PlaybackStopped+=(_,e)=>
            {
                buffer.ReleaseAudioThread();
                if(e.Exception!=null && ReferenceEquals(output,player))
                {
                    System.Threading.Volatile.Write(ref playbackError,"播放设备已中断："+Explain(e.Exception));
                    log.Error("Player.Fault",("code","PlaybackStopped"),("detail",e.Exception.Message));
                }
            };
            OutputName=device.Name;Mode=mode;
            var actual=player is WasapiOut native ? native.OutputWaveFormat : provider.WaveFormat;
            OutputFormat=actual.SampleRate+" Hz / "+actual.BitsPerSample+" bit / "+actual.Channels+" ch";
            log.Info("Player.Created",("mode",mode.ToString()),("format",OutputFormat),("eventSync",true),("latencyMs",PlaybackLatencyMs),("minimumStartMs",MinimumStartBufferMs),("bufferMs",BufferSeconds*1000));
        }

        public void Play()
        {
            lock(sync)
            {
                if(output==null || disposed)return;
                try {if(output.PlaybackState!=PlaybackState.Playing)output.Play();}
                catch(Exception ex)
                {
                    if(Mode==AudioOutputMode.Exclusive)
                    {
                        FallbackReason=Explain(ex);ReleaseNative();
                        log.Warn("Player.ExclusiveFallback",("reason",FallbackReason));
                        try {InitializeMode(AudioOutputMode.Shared);output.Play();return;}
                        catch(Exception fallback){ReleaseNative();playbackError="音频输出不可用："+Explain(fallback);}
                    }
                    else playbackError="音频输出不可用："+Explain(ex);
                    log.Error("Player.Fault",("code","PlayFailed"),("detail",playbackError));
                }
            }
        }

        private static string Explain(Exception ex)
        {
            switch(unchecked((uint)ex.HResult))
            {
                case 0x8889000A:return "播放设备正被其他程序占用";
                case 0x8889000E:return "Windows 或设备禁止独占输出";
                case 0x88890008:return "播放设备不支持此音频格式";
                case 0x88890004:return "播放设备已断开";
                default:return string.IsNullOrWhiteSpace(ex.Message) ? "无法打开播放设备" : ex.Message;
            }
        }

        private void ReleaseNative()
        {
            var player=output;var endpoint=device;
            var cancellation=playbackStop;playbackStop=null;
            output=null;device=null;Mode=AudioOutputMode.None;
            cancellation?.Cancel();
            try{player?.Stop();}catch{}
            try{player?.Dispose();}catch{}
            try{endpoint?.Dispose();}catch{}
            cancellation?.Dispose();
        }

        public void Add(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0)
            {
                return;
            }

            buffer.Add(data, offset, count);
        }

        public long PaddingFrames { get { return buffer.PaddingFrames; } }
        public long ActualUnderruns { get { return buffer.Underruns; } }
        public long Overflows { get { return buffer.Overflows; } }
        public bool MmcssActive { get { return buffer.MmcssActive; } }
        public double MaxReadGapMs { get { return buffer.MaxReadGapMs; } }
        public long InputFrames { get { return buffer.InputFrames; } }
        public long MaxInputZeroRunFrames { get { return buffer.MaxInputZeroRunFrames; } }
        public bool Recovering { get { return buffer.Recovering; } }
        public int TargetMs { set { buffer.TargetMs = Math.Max(value,MinimumStartBufferMs); } }
        public void EndInput() { buffer.EndInput(); }

        /// <summary>补一帧 5 ms 静音，用于单包缺失时避免爆音。</summary>
        public void AddSilence()
        {
            buffer.Add(silence, 0, silence.Length);
        }

        /// <summary>
        /// 断流恢复时对首个数据帧做 5 ms 线性淡入（0 → 1），避免从静音切回音乐时爆音。
        /// 一帧正好 5 ms；协议要求 PC 不置 flags.bit0，只记录断流并做淡入。
        /// </summary>
        public void AddFadeIn(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0) return;
            lock (sync)
            {
                int bytes = Math.Min(count, fade.Length);
                int frames = bytes / WifiTransport.BytesPerFrame;
                for (int frame = 0; frame < frames; frame++)
                {
                    for (int channel = 0; channel < 2; channel++)
                    {
                        int i = frame * 4 + channel * 2;
                        short sample = (short)(data[offset + i] | data[offset + i + 1] << 8);
                        short scaled = (short)(sample * (frame + 1) / frames);
                        fade[i] = (byte)scaled;
                        fade[i + 1] = (byte)(scaled >> 8);
                    }
                }
                buffer.Add(fade, 0, bytes);
                if (count > bytes) buffer.Add(data, offset + bytes, count - bytes);
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                try
                {
                    buffer.Clear();
                }
                catch
                {
                }
            }
        }

        /// <summary>停止并释放输出端点；缓冲一并清空。</summary>
        public void Stop()
        {
            lock(sync)
            {
                log.Info("Player.ReadStats",("paddingFrames",buffer.PaddingFrames),("underruns",buffer.Underruns),("overflows",buffer.Overflows));
                ReleaseNative();
                buffer.Clear();
                log.Info("Player.Released");
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
            }

            Stop();
        }
    }
}
