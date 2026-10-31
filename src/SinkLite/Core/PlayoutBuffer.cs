using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace SinkLite.Core
{
    /// <summary>Single audio-clock-driven buffer. Read is the only consumer.</summary>
    internal sealed class PlayoutBuffer : IWaveProvider
    {
        private readonly object sync = new object();
        private readonly BufferedWaveProvider pcm;
        private bool recovering;
        private bool draining;
        private bool fadePending;
        private int targetMs = 80;
        private long inputZeroRunFrames;
        private long lastReadTicks;
        private long maxReadGapTicks;
        private IntPtr mmcssTask;
        private int renderThreadId;
        public bool UseMmcss { get; set; }
        public bool MmcssActive { get { return mmcssTask != IntPtr.Zero; } }
        public double MaxReadGapMs { get { return maxReadGapTicks * 1000.0 / Stopwatch.Frequency; } }
        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);
        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        public void ReleaseAudioThread()
        {
            lock (sync)
            {
                if (mmcssTask != IntPtr.Zero && renderThreadId == Environment.CurrentManagedThreadId)
                {
                    AvRevertMmThreadCharacteristics(mmcssTask);
                    mmcssTask = IntPtr.Zero;
                }
                renderThreadId = 0;
                lastReadTicks = 0;
            }
        }

        public PlayoutBuffer()
        {
            pcm = new BufferedWaveProvider(new WaveFormat(48000, 16, 2));
            pcm.BufferDuration = TimeSpan.FromSeconds(1);
            pcm.ReadFully = false;
            pcm.DiscardOnBufferOverflow = false;
        }

        public WaveFormat WaveFormat { get { return pcm.WaveFormat; } }
        public long PaddingFrames { get; private set; }
        public long Underruns { get; private set; }
        public long Overflows { get; private set; }
        public long InputFrames { get; private set; }
        public long MaxInputZeroRunFrames { get; private set; }
        public int BufferedMs { get { lock (sync) { return (int)pcm.BufferedDuration.TotalMilliseconds; } } }
        public bool Recovering { get { lock (sync) { return recovering; } } }
        public bool Draining { get { lock (sync) { return draining; } } }
        public int TargetMs { set { lock (sync) { targetMs = Math.Clamp(value, 10, 800); } } }

        public void Add(byte[] data, int offset, int count)
        {
            lock (sync)
            {
                // Overflow is an explicit fault, never silently discard incoming/old music.
                if (pcm.BufferedBytes + count > pcm.BufferLength)
                {
                    Overflows++;
                    throw new InvalidOperationException("Audio buffer exceeded one second; source/playback clock or device stalled.");
                }
                for (int i = offset; i + 3 < offset + count; i += 4)
                {
                    inputZeroRunFrames = (data[i] | data[i + 1] | data[i + 2] | data[i + 3]) == 0
                        ? inputZeroRunFrames + 1 : 0;
                    MaxInputZeroRunFrames = Math.Max(MaxInputZeroRunFrames, inputZeroRunFrames);
                }
                InputFrames += count / 4;
                pcm.AddSamples(data, offset, count);
            }
        }

        public void EndInput() { lock (sync) { draining = true; } }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (sync)
            {
                if (UseMmcss && renderThreadId == 0)
                {
                    renderThreadId = Environment.CurrentManagedThreadId;
                    uint index = 0;
                    mmcssTask = AvSetMmThreadCharacteristics("Audio", ref index);
                    if (mmcssTask != IntPtr.Zero) AvSetMmThreadPriority(mmcssTask, 1);
                }
                long now = Stopwatch.GetTimestamp();
                if (lastReadTicks != 0) maxReadGapTicks = Math.Max(maxReadGapTicks, now - lastReadTicks);
                lastReadTicks = now;
                int aligned = count - count % WaveFormat.BlockAlign;
                int threshold = Math.Max(targetMs * 192, aligned);
                if (recovering && !draining && pcm.BufferedBytes < threshold)
                {
                    Array.Clear(buffer, offset, count);
                    PaddingFrames += aligned / 4;
                    return count;
                }

                if (recovering)
                {
                    recovering = false;
                    fadePending = true;
                }
                int read = pcm.Read(buffer, offset, aligned);
                if (fadePending && read > 0)
                {
                    int frames = Math.Min(read / 4, 240);
                    for (int frame = 0; frame < frames; frame++)
                    {
                        // Both channels of a frame have exactly the same gain.
                        for (int channel = 0; channel < 2; channel++)
                        {
                            int i = offset + frame * 4 + channel * 2;
                            short sample = (short)(buffer[i] | buffer[i + 1] << 8);
                            short scaled = (short)(sample * (frame + 1) / frames);
                            buffer[i] = (byte)scaled;
                            buffer[i + 1] = (byte)(scaled >> 8);
                        }
                    }
                    fadePending = false;
                }
                if (read < aligned && !draining)
                {
                    Underruns++;
                    recovering = true;
                    // Fade the last real samples into the unavoidable gap.
                    int frames = Math.Min(read / 4, 240);
                    for (int frame = 0; frame < frames; frame++)
                    {
                        for (int channel = 0; channel < 2; channel++)
                        {
                            int i = offset + read - frames * 4 + frame * 4 + channel * 2;
                            short sample = (short)(buffer[i] | buffer[i + 1] << 8);
                            short scaled = (short)(sample * (frames - frame - 1) / frames);
                            buffer[i] = (byte)scaled;
                            buffer[i + 1] = (byte)(scaled >> 8);
                        }
                    }
                }
                if (draining) return read;
                Array.Clear(buffer, offset + read, count - read);
                PaddingFrames += (aligned - read) / 4;
                return count;
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                pcm.ClearBuffer();
                recovering = false;
                draining = false;
                fadePending = false;
                PaddingFrames = 0;
                Underruns = 0;
                Overflows = 0;
                lastReadTicks = 0;
                maxReadGapTicks = 0;
                InputFrames = 0;
                inputZeroRunFrames = 0;
                MaxInputZeroRunFrames = 0;
            }
        }
    }
}
