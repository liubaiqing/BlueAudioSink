using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SinkLite.Core;
static class WasapiCadenceProbe
{
 sealed class Probe : IWaveProvider,IDisposable
 {
  readonly IWaveProvider source;readonly MemoryStream delivered;readonly bool registerMmcss;long before;IntPtr mmcss;int thread;
  public List<(double gap,int frames)> Reads=new(4096);
  public Probe(IWaveProvider source,int capacity,bool registerMmcss=true){this.source=source;this.registerMmcss=registerMmcss;delivered=new MemoryStream(capacity);}
  public WaveFormat WaveFormat=>source.WaveFormat;
  public int Read(byte[] b,int o,int n){if(registerMmcss && thread==0){thread=Environment.CurrentManagedThreadId;uint i=0;mmcss=AvSetMmThreadCharacteristics("Audio",ref i);if(mmcss!=IntPtr.Zero)AvSetMmThreadPriority(mmcss,1);}long now=Stopwatch.GetTimestamp();int read=source.Read(b,o,n);if(read>0){Reads.Add((before==0?0:(now-before)*1000.0/Stopwatch.Frequency,read/WaveFormat.BlockAlign));delivered.Write(b,o,read);before=now;}return read;}
  public string Hash=>Convert.ToHexString(SHA256.HashData(delivered.ToArray()));public long Bytes=>delivered.Length;
  public void ReleaseThread(){if(mmcss!=IntPtr.Zero&&thread==Environment.CurrentManagedThreadId){AvRevertMmThreadCharacteristics(mmcss);mmcss=IntPtr.Zero;}}
  public void Dispose()=>delivered.Dispose();
 }
 [DllImport("avrt.dll",CharSet=CharSet.Unicode)]static extern IntPtr AvSetMmThreadCharacteristics(string name,ref uint index);
 [DllImport("avrt.dll")]static extern bool AvSetMmThreadPriority(IntPtr task,int priority);
 [DllImport("avrt.dll")]static extern bool AvRevertMmThreadCharacteristics(IntPtr task);
 public static void SessionStatus()
 {
  using var e=new MMDeviceEnumerator();
  foreach(var role in new[]{Role.Console,Role.Multimedia,Role.Communications}){using var d=e.GetDefaultAudioEndpoint(DataFlow.Render,role);Console.WriteLine(JsonSerializer.Serialize(new{role=role.ToString(),device=d.FriendlyName,id=d.ID}));}
  foreach(var d in e.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))
  using(d){
   Console.WriteLine(JsonSerializer.Serialize(new{device=d.FriendlyName,id=d.ID,volume=d.AudioEndpointVolume.MasterVolumeLevelScalar,mute=d.AudioEndpointVolume.Mute,peak=d.AudioMeterInformation.MasterPeakValue}));
   var sessions=d.AudioSessionManager.Sessions;
   for(int i=0;i<sessions.Count;i++)using(var session=sessions[i]){
    var peaks=new List<float>();for(int j=0;j<10;j++){peaks.Add(session.AudioMeterInformation.MasterPeakValue);Thread.Sleep(30);}
    Console.WriteLine(JsonSerializer.Serialize(new{pid=session.GetProcessID,state=session.State.ToString(),volume=session.SimpleAudioVolume.Volume,mute=session.SimpleAudioVolume.Mute,peak=peaks.Max(),name=session.DisplayName}));
   }
  }
 }
 public static void Run(string file,string id)
 {
  var directory=Path.Combine(Environment.CurrentDirectory,"artifacts/exclusive-noise");Directory.CreateDirectory(directory);
  var pcm=File.ReadAllBytes(file);var results=new List<object>();
  foreach(var c in new[]{("shared-event-50",AudioClientShareMode.Shared,true,50),("exclusive-poll-50",AudioClientShareMode.Exclusive,false,50),("exclusive-event-50",AudioClientShareMode.Exclusive,true,50),("exclusive-event-100",AudioClientShareMode.Exclusive,true,100)})
  {
   using var e=new MMDeviceEnumerator();using var device=string.IsNullOrEmpty(id)?e.GetDefaultAudioEndpoint(DataFlow.Render,Role.Console):e.GetDevice(id);
   using var raw=new RawSourceWaveStream(new MemoryStream(pcm,false),new WaveFormat(48000,16,2));var gain=new PcmVolumeProvider(raw,65);using var probe=new Probe(gain,pcm.Length);
   using var player=new WasapiOut(device,c.Item2,c.Item3,c.Item4);using var ended=new ManualResetEventSlim();Exception error=null;
   player.PlaybackStopped+=(_,args)=>{probe.ReleaseThread();error=args.Exception;ended.Set();};
   var start=DateTime.Now;player.Init(probe);
   var ac=(AudioClient)typeof(WasapiOut).GetField("audioClient",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(player);
   double bufferMs=ac.BufferSize*1000.0/player.OutputWaveFormat.SampleRate;
   player.Play();if(!ended.Wait(15000))throw new Exception("Playback timed out");if(error!=null)throw error;
   var gaps=probe.Reads.Skip(1).Select(x=>x.gap).Order().ToArray();
   var result=new{name=c.Item1,start=start.ToString("O"),end=DateTime.Now.ToString("O"),bufferMs,reads=probe.Reads.Count,bytes=probe.Bytes,hash=probe.Hash,maxGap=gaps.DefaultIfEmpty().Max(),p99Gap=gaps.Length==0?0:gaps[(int)((gaps.Length-1)*0.99)],gapsAboveBuffer=gaps.Count(x=>x>bufferMs),format=player.OutputWaveFormat.ToString()};
   results.Add(result);Console.WriteLine(JsonSerializer.Serialize(result));
   File.WriteAllLines(Path.Combine(directory,c.Item1+"-reads.csv"),new[]{"gapMs,frames"}.Concat(probe.Reads.Select(x=>x.gap.ToString("F4",System.Globalization.CultureInfo.InvariantCulture)+","+x.frames)));
  }
  File.WriteAllText(Path.Combine(directory,"cadence-comparison.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
 }
 sealed class MeasuredPlayer : IWavePlayer
 {
  readonly WasapiOut inner;readonly MeasuredDevice owner;
  public MeasuredPlayer(WasapiOut inner,MeasuredDevice owner){this.inner=inner;this.owner=owner;}
  public event EventHandler<StoppedEventArgs> PlaybackStopped{add=>inner.PlaybackStopped+=value;remove=>inner.PlaybackStopped-=value;}
  public PlaybackState PlaybackState=>inner.PlaybackState;
  public WaveFormat OutputWaveFormat=>inner.OutputWaveFormat;
  public float Volume{get=>inner.Volume;set=>inner.Volume=value;}
  public void Init(IWaveProvider source){owner.Capture=new Probe(source,2_000_000,false);inner.Init(owner.Capture);var ac=(AudioClient)typeof(WasapiOut).GetField("audioClient",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(inner);owner.BufferMs=ac.BufferSize*1000.0/inner.OutputWaveFormat.SampleRate;}
  public void Play()=>inner.Play();public void Pause()=>inner.Pause();public void Stop()=>inner.Stop();
  public void Dispose(){inner.Dispose();owner.Capture?.Dispose();}
 }
 sealed class MeasuredDevice : IWasapiOutputDevice
 {
  readonly IWasapiOutputDevice product;readonly bool legacy;readonly string id;MMDevice extra;
  public Probe Capture;public double BufferMs;
  public MeasuredDevice(string id,bool legacy){this.id=id;this.legacy=legacy;product=WasapiOutputDevice.Open(id);}
  public string Name=>product.Name;
  public bool SupportsExclusive(WaveFormat format)=>product.SupportsExclusive(format);
  public IWavePlayer CreatePlayer(AudioClientShareMode mode){WasapiOut player;if(legacy){using var e=new MMDeviceEnumerator();extra=string.IsNullOrEmpty(id)?e.GetDefaultAudioEndpoint(DataFlow.Render,Role.Console):e.GetDevice(id);player=new WasapiOut(extra,mode,false,50);}else player=(WasapiOut)product.CreatePlayer(mode);return new MeasuredPlayer(player,this);}
  public void Dispose(){extra?.Dispose();product.Dispose();}
 }
 public static void OutputBenchmark(string file,string id)
 {
  var pcm=File.ReadAllBytes(file);var results=new List<object>();
  foreach(var c in new[]{("shared-event",false,false),("exclusive-poll-reference",true,true),("exclusive-event-fixed",true,false)})
  {
   MeasuredDevice device=null;using var log=new SinkLite.Platform.Log(Path.Combine(Environment.CurrentDirectory,"artifacts/exclusive-noise/benchmark-logs"));
   using var output=new AudioOutput(log,new WifiOutputOptions(c.Item2,65,id),key=>device=new MeasuredDevice(key,c.Item3));
   if(!output.EnsureInitialized())throw new Exception(output.FailureReason);
   output.TargetMs=80;int prefill=Math.Min(pcm.Length,160*192);output.Add(pcm,0,prefill);output.Play();
   var clock=Stopwatch.StartNew();int i=0;
   for(int at=prefill;at<pcm.Length;at+=960){i++;while(clock.Elapsed.TotalMilliseconds<i*5)Thread.Sleep(1);int n=Math.Min(960,pcm.Length-at);output.Add(pcm,at,n);}
   output.EndInput();if(!SpinWait.SpinUntil(()=>!output.IsPlaying,5000))throw new Exception("Output did not drain");if(output.PlaybackError!=null)throw new Exception(output.PlaybackError);
   var result=new{name=c.Item1,mode=output.Mode.ToString(),actualDeviceBufferMs=device.BufferMs,output.MinimumStartBufferMs,output.ActualUnderruns,output.PaddingFrames,output.Overflows,output.MaxReadGapMs,bytes=device.Capture.Bytes,hash=device.Capture.Hash};
   results.Add(result);Console.WriteLine(JsonSerializer.Serialize(result));
  }
  File.WriteAllText("artifacts/exclusive-noise/output-benchmark.json",JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
 }

}
