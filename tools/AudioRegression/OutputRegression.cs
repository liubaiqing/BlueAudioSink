using NAudio.CoreAudioApi;
using NAudio.Wave;
using SinkLite.Core;
using SinkLite.Platform;
static class OutputRegression
{
 static void Assert(bool v,string s){if(!v)throw new Exception(s);}
 sealed class Bytes : IWaveProvider
 {
  public WaveFormat WaveFormat=>new WaveFormat(48000,16,2);readonly byte[] data;int at;
  public Bytes(byte[] data){this.data=data;}
  public int Read(byte[] b,int o,int n){n=Math.Min(n,data.Length-at);Array.Copy(data,at,b,o,n);at+=n;return n;}
 }
 sealed class FakePlayer : IWavePlayer
 {
  public bool Disposed,FailInit,FailPlay;public IWaveProvider Source;
  public PlaybackState PlaybackState{get;private set;}
  public float Volume{get;set;}=1;
  public WaveFormat OutputWaveFormat=>Source.WaveFormat;
  public event EventHandler<StoppedEventArgs> PlaybackStopped;
  public void Init(IWaveProvider p){Source=p;if(FailInit)throw new InvalidOperationException("exclusive denied");}
  public void Play(){if(FailPlay)throw new InvalidOperationException("start failed");PlaybackState=PlaybackState.Playing;}
  public void Stop(){PlaybackState=PlaybackState.Stopped;PlaybackStopped?.Invoke(this,new StoppedEventArgs());}
  public void Pause()=>PlaybackState=PlaybackState.Paused;
  public void Dispose()=>Disposed=true;
 }
 sealed class FakeDevice : IWasapiOutputDevice
 {
  public string Name=>"isolated test endpoint";public bool Disposed;public int Bits=16;public bool DenyExclusive,FailStart,FailShared;public FakePlayer Player;public AudioClientShareMode Mode;
  public bool SupportsExclusive(WaveFormat f)=>Bits==f.BitsPerSample;
  public IWavePlayer CreatePlayer(AudioClientShareMode mode){Mode=mode;return Player=new FakePlayer{FailInit=mode==AudioClientShareMode.Exclusive?DenyExclusive:FailShared,FailPlay=mode==AudioClientShareMode.Exclusive && FailStart};}
  public void Dispose()=>Disposed=true;
 }
 static byte[] Pcm(){var values=new short[]{short.MinValue,-30123,-1,0,1,12345,short.MaxValue,4567};return values.SelectMany(BitConverter.GetBytes).ToArray();}
 public static void Run(Log log,Action<string,Action> Test)
 {
  Test("Exclusive startup covers two immediate device blocks plus headroom",()=>{var b=new PlayoutBuffer();b.Add(new byte[80*192],0,80*192);using var stop=new CancellationTokenSource();var gate=new PcmStartupGate(b,b,stop.Token);var feed=Task.Run(()=>{Thread.Sleep(30);b.Add(new byte[140*192],0,140*192);});var data=new byte[100*192];gate.Read(data,0,data.Length);gate.Read(data,0,data.Length);feed.Wait();Assert(b.Underruns==0&&b.PaddingFrames==0&&b.BufferedMs==20,"startup starved before device clock started");});
  Test("Exclusive startup cancellation consumes no queued audio",()=>{var b=new PlayoutBuffer();b.Add(new byte[80*192],0,80*192);using var stop=new CancellationTokenSource();stop.Cancel();var gate=new PcmStartupGate(b,b,stop.Token);Assert(gate.Read(new byte[100*192],0,100*192)==0&&b.BufferedMs==80,"cancelled startup read PCM");});
  Test("Exclusive startup drains a short finite clip without invented padding",()=>{var b=new PlayoutBuffer();b.Add(new byte[20*192],0,20*192);b.EndInput();var gate=new PcmStartupGate(b,b,CancellationToken.None);Assert(gate.Read(new byte[100*192],0,100*192)==20*192&&b.PaddingFrames==0&&b.Underruns==0,"short clip treated as starvation");});
  Test("Default output stays shared and enabling exclusive always mutes first",()=>{var p=new Settings();Assert(!p.WifiExclusive&&p.WifiVolume==100,"changed old user default");p.SetWifiExclusive(true);Assert(p.WifiExclusive&&p.WifiVolume==0,"exclusive starts loud");p.WifiVolume=35;p.SetWifiExclusive(false);Assert(!p.WifiExclusive&&p.WifiVolume==35,"disabling raised volume");});
  Test("Output gain unity is bit exact and zero remains silent",()=>{var p=Pcm();foreach(int gain in new[]{0,100}){var v=new PcmVolumeProvider(new Bytes(p),gain);var b=new byte[p.Length];Assert(v.Read(b,0,b.Length)==b.Length,"read length");Assert(gain==0?b.All(x=>x==0):b.SequenceEqual(p),"gain mismatch");}});
  Test("Software volume reaches mute smoothly without changing frame count",()=>{var data=Enumerable.Repeat((short)16000,960).SelectMany(BitConverter.GetBytes).ToArray();var v=new PcmVolumeProvider(new Bytes(data),100);v.VolumePercent=0;var b=new byte[data.Length];Assert(v.Read(b,0,b.Length)==data.Length,"length");Assert(BitConverter.ToInt16(b,0)>0 && b.Skip(240*4).All(x=>x==0),"mute ramp");});
  Test("PCM 24/32-bit extension preserves signed samples and frame count",()=>{var p=Pcm();foreach(int bits in new[]{24,32}){var v=new PcmBitDepthProvider(new Bytes(p),new WaveFormatExtensible(48000,bits,2));var b=new byte[p.Length/2*(bits/8)];Assert(v.Read(b,0,b.Length)==b.Length,"converted length");for(int i=0;i<p.Length/2;i++){int o=i*(bits/8);Assert(b[o+bits/8-2]==p[i*2]&&b[o+bits/8-1]==p[i*2+1],"signed PCM changed");Assert(b.Skip(o).Take(bits/8-2).All(x=>x==0),"nonzero padding");}}});
  Test("Exclusive output binds explicit endpoint and releases both resources",()=>{var d=new FakeDevice{Bits=24};string seen=null;using(var o=new AudioOutput(log,new WifiOutputOptions(true,0,"chosen-endpoint"),id=>{seen=id;return d;})){Assert(o.EnsureInitialized()&&o.Mode==AudioOutputMode.Exclusive,"exclusive not active");Assert(seen=="chosen-endpoint"&&o.OutputFormat.Contains("24 bit"),"wrong device/format");o.Add(Pcm(),0,Pcm().Length);o.EndInput();o.Play();Assert(o.IsPlaying,"not playing");var b=new byte[24];d.Player.Source.Read(b,0,b.Length);Assert(b.All(x=>x==0),"initial mute bypassed");}Assert(d.Disposed&&d.Player.Disposed,"resources retained");});
  Test("Denied exclusive falls back once to shared with actual state and volume",()=>{var devices=new List<FakeDevice>();using var o=new AudioOutput(log,new WifiOutputOptions(true,0),_=>{var d=new FakeDevice{DenyExclusive=true};devices.Add(d);return d;});Assert(o.EnsureInitialized()&&o.Mode==AudioOutputMode.Shared,"fallback missing");Assert(devices.Count==2&&devices[0].Disposed&&devices[0].Player.Disposed,"exclusive resource leak or repeated retry");Assert(!string.IsNullOrEmpty(o.FallbackReason)&&o.VolumePercent==0,"fallback state/mute lost");o.EnsureInitialized();Assert(devices.Count==2,"retry on every read");});
  Test("Unsupported exclusive format uses shared without changing sample rate",()=>{var ds=new List<FakeDevice>();using var o=new AudioOutput(log,new WifiOutputOptions(true,35),_=>{var d=new FakeDevice{Bits=0};ds.Add(d);return d;});Assert(o.EnsureInitialized()&&o.Mode==AudioOutputMode.Shared,"unsupported fallback");Assert(ds[1].Player.Source.WaveFormat.SampleRate==48000,"unexpected resample before shared backend");});
  Test("Exclusive start failure falls back without losing output buffer",()=>{var ds=new List<FakeDevice>();using var o=new AudioOutput(log,new WifiOutputOptions(true,0),_=>{var d=new FakeDevice{FailStart=true};ds.Add(d);return d;});Assert(o.EnsureInitialized(),"init");var p=Pcm();o.Add(p,0,p.Length);o.Play();Assert(o.Mode==AudioOutputMode.Shared&&o.IsPlaying&&o.PlaybackError==null&&ds.Count==2,"start fallback");Assert(o.InputFrames==p.Length/4,"buffer discarded");});
  Test("Both outputs failing leaves actionable error and no native resources",()=>{var ds=new List<FakeDevice>();using var o=new AudioOutput(log,new WifiOutputOptions(true),_=>{var d=new FakeDevice{DenyExclusive=true,FailShared=true};ds.Add(d);return d;});Assert(!o.EnsureInitialized()&&o.Mode==AudioOutputMode.None&&!string.IsNullOrEmpty(o.FailureReason),"failure hidden");Assert(ds.Count==2&&ds.All(d=>d.Disposed&&d.Player.Disposed),"failed resources held");});
  Test("Live volume updates never reopen an active endpoint",()=>{int opens=0;var d=new FakeDevice();using var o=new AudioOutput(log,new WifiOutputOptions(true,0),_=>{opens++;return d;});Assert(o.EnsureInitialized(),"init");o.VolumePercent=35;Assert(o.EnsureInitialized()&&opens==1&&o.VolumePercent==35,"volume recreated stream");});
 }
 public static void NativeFlow(Log log,string endpoint)
 {
  using var rx=new WifiReceiver(log,45678,45679,80,200,"BlueAudioSink output verification",new WifiOutputOptions(false,0,endpoint));
  Assert(rx.Start(),"Product UDP ports unavailable");
  void WaitMode(AudioOutputMode mode){Assert(SpinWait.SpinUntil(()=>rx.GetSnapshot().OutputMode==mode && rx.GetSnapshot().OutputActive,10000),"Output mode did not become active: "+mode+" / "+rx.GetSnapshot().Detail);var s=rx.GetSnapshot();Assert(!s.HasAudibleSignal,"Software mute not reflected in playback status");Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{mode=s.OutputMode.ToString(),s.OutputFormat,s.VolumePercent,s.OutputFallbackReason,s.LastAudioAgeMs}));}
  WaitMode(AudioOutputMode.Shared);
  rx.SetOutputOptions(new WifiOutputOptions(true,0,endpoint));WaitMode(AudioOutputMode.Exclusive);
  Thread.Sleep(150);
  rx.SetOutputOptions(new WifiOutputOptions(false,0,endpoint));WaitMode(AudioOutputMode.Shared);
  rx.Stop();Assert(rx.GetSnapshot().OutputMode==AudioOutputMode.None,"Receiver retained its output");
  Console.WriteLine("PASS real Android stream switched shared/exclusive/shared and released output");
 }
 public static void HardwareProbe(Log log,string endpoint)
 {
  using var first=new AudioOutput(log,new WifiOutputOptions(true,0,endpoint));
  Assert(first.EnsureInitialized(),first.FailureReason);
  Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{first.OutputName,mode=first.Mode.ToString(),first.OutputFormat,first.FallbackReason}));
  var pcm=new byte[48000*4/2];first.Add(pcm,0,pcm.Length);first.EndInput();first.Play();Thread.Sleep(150);
  Assert(first.IsPlaying || first.BufferedMs==0,"Hardware did not start");
  if(first.Mode==AudioOutputMode.Exclusive){using var second=new AudioOutput(log,new WifiOutputOptions(true,0,endpoint));Assert(!second.EnsureInitialized(),"Second client unexpectedly acquired exclusive endpoint");Console.WriteLine("PASS real exclusive ownership rejects second client");}
  first.Stop();using var shared=new AudioOutput(log,new WifiOutputOptions(false,0,endpoint));Assert(shared.EnsureInitialized(),shared.FailureReason);Console.WriteLine("PASS output released and shared reopen succeeds");
 }
}
