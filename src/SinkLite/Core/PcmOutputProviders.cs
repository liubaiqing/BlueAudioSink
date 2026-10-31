using System;
using System.Threading;
using NAudio.Wave;
namespace SinkLite.Core
{
    /// <summary>Output-only gain. Unity is bit exact; network PCM and the single consumer clock are unchanged.</summary>
    internal sealed class PcmVolumeProvider : IWaveProvider
    {
        private readonly IWaveProvider source;
        private int percent;
        private double gain;
        private readonly double step;
        public PcmVolumeProvider(IWaveProvider source,int volumePercent)
        {
            if(source.WaveFormat.BitsPerSample!=16 || source.WaveFormat.Encoding!=WaveFormatEncoding.Pcm)throw new ArgumentException("16-bit PCM required");
            this.source=source;VolumePercent=volumePercent;gain=percent/100.0;step=1.0/(source.WaveFormat.SampleRate*0.005);
        }
        public WaveFormat WaveFormat=>source.WaveFormat;
        public int VolumePercent {get=>Volatile.Read(ref percent);set=>Volatile.Write(ref percent,Math.Clamp(value,0,100));}
        public int Read(byte[] buffer,int offset,int count)
        {
            int read=source.Read(buffer,offset,count);double target=VolumePercent/100.0;
            if(gain==1 && target==1)return read;
            if(gain==0 && target==0){Array.Clear(buffer,offset,read);return read;}
            int frameSize=WaveFormat.BlockAlign;
            for(int frame=offset;frame<offset+read;frame+=frameSize)
            {
                gain=target>gain ? Math.Min(target,gain+step) : Math.Max(target,gain-step);
                for(int i=frame;i<Math.Min(frame+frameSize,offset+read);i+=2)
                {
                    short sample=(short)(buffer[i]|buffer[i+1]<<8);
                    short scaled=(short)Math.Round(sample*gain);
                    buffer[i]=(byte)scaled;buffer[i+1]=(byte)(scaled>>8);
                }
            }
            return read;
        }
    }
    /// <summary>Lossless 16→24/32 PCM extension, with no resampling or second clock.</summary>
    internal sealed class PcmBitDepthProvider : IWaveProvider
    {
        private readonly IWaveProvider source;
        private byte[] input=Array.Empty<byte>();
        public WaveFormat WaveFormat {get;}
        public PcmBitDepthProvider(IWaveProvider source,WaveFormat format)
        {
            if(source.WaveFormat.BitsPerSample!=16 || format.SampleRate!=source.WaveFormat.SampleRate || format.Channels!=source.WaveFormat.Channels || (format.BitsPerSample!=16 && format.BitsPerSample!=24 && format.BitsPerSample!=32))throw new ArgumentException("Only lossless PCM depth extension is supported");
            this.source=source;WaveFormat=format;
        }
        public int Read(byte[] buffer,int offset,int count)
        {
            if(WaveFormat.BitsPerSample==16)return source.Read(buffer,offset,count);
            int frames=count/WaveFormat.BlockAlign,needed=frames*source.WaveFormat.BlockAlign;
            if(input.Length<needed)input=new byte[needed];
            int read=source.Read(input,0,needed),bytes=WaveFormat.BitsPerSample/8,pad=bytes-2;
            for(int i=0,j=offset;i<read;i+=2,j+=bytes)
            {for(int k=0;k<pad;k++)buffer[j+k]=0;buffer[j+pad]=input[i];buffer[j+pad+1]=input[i+1];}
            return read/2*bytes;
        }
    }
    /// <summary>Only waits during the initial FillBuffer, before WASAPI starts the device clock.</summary>
    internal sealed class PcmStartupGate : IWaveProvider
    {
        private readonly IWaveProvider source;
        private readonly PlayoutBuffer buffer;
        private readonly CancellationToken stop;
        private bool first=true;
        public PcmStartupGate(IWaveProvider source,PlayoutBuffer buffer,CancellationToken stop)
        {this.source=source;this.buffer=buffer;this.stop=stop;}
        public WaveFormat WaveFormat=>source.WaveFormat;
        public int Read(byte[] data,int offset,int count)
        {
            if(stop.IsCancellationRequested)return 0;
            if(first)
            {
                first=false;
                int required=(int)Math.Ceiling(count*2000.0/WaveFormat.AverageBytesPerSecond)+20;
                long deadline=System.Diagnostics.Stopwatch.GetTimestamp()+System.Diagnostics.Stopwatch.Frequency/2;
                while(!stop.IsCancellationRequested && !buffer.Draining && buffer.BufferedMs<required && System.Diagnostics.Stopwatch.GetTimestamp()<deadline)
                    Thread.Sleep(2);
            }
            return stop.IsCancellationRequested ? 0 : source.Read(data,offset,count);
        }
    }

}
