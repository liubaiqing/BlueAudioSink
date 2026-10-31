using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SinkLite.UI
{
    /// <summary>Small cached, premultiplied-alpha shapes; text and the window remain native GDI.</summary>
    internal sealed class SmoothShapes : IDisposable
    {
        private readonly record struct Key(int Width, int Height, double Radius, int Stroke, int Color);
        private readonly Dictionary<Key, IntPtr> cache = new Dictionary<Key, IntPtr>();
        private IntPtr dc;
        public int Count => cache.Count;
        public long PixelBytes { get; private set; }
        public long Builds { get; private set; }
        public long Hits { get; private set; }
        private const int MaxBytes = 8 * 1024 * 1024;

        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
        {
            public uint Size; public int Width, Height; public ushort Planes, Bits;
            public uint Compression, Image; public int X, Y; public uint Colors, Important;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BrushInfo
        { public uint Style, Color; public IntPtr Hatch; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend
        { public byte Operation, Flags, Opacity, Alpha; }
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr obj, int size, out BrushInfo info);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("msimg32.dll")] private static extern bool AlphaBlend(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, int sw, int sh, Blend blend);

        public static int BrushColor(IntPtr brush)
        { return GetObject(brush, Marshal.SizeOf<BrushInfo>(), out var info) == 0 ? -1 : (int)info.Color; }

        public bool Draw(IntPtr target, int x, int y, int width, int height, double radius, int stroke, int color)
        {
            if (width <= 0 || height <= 0) return true;
            if (color < 0 || (long)width * height * 4 > MaxBytes) return false;
            if (dc == IntPtr.Zero) dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            var key = new Key(width, height, Math.Clamp(radius, 0, Math.Min(width, height) / 2.0), stroke, color);
            if (!cache.TryGetValue(key, out var bitmap))
            {
                int bytes = width * height * 4;
                if (cache.Count >= 128 || PixelBytes + bytes > MaxBytes) Clear();
                var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
                bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero) return false;
                try
                {
                    byte[] pixels = new byte[bytes];
                    for (int py = 0; py < height; py++) for (int px = 0; px < width; px++)
                    {
                        double d = Distance(px + .5, py + .5, key);
                        int covered;
                        if (d >= 1 || (stroke > 0 && d <= -stroke - 1)) covered = 0;
                        else if (d <= -1 && (stroke == 0 || d >= -stroke + 1)) covered = 16;
                        else
                        {
                            covered = 0;
                            for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
                            {
                                double sample = Distance(px + (sx + .5) / 4, py + (sy + .5) / 4, key);
                                if (sample <= 0 && (stroke == 0 || sample >= -stroke)) covered++;
                            }
                        }
                        int alpha = (covered * 255 + 8) / 16, at = (py * width + px) * 4;
                        pixels[at] = (byte)(((color >> 16 & 255) * alpha + 127) / 255);
                        pixels[at + 1] = (byte)(((color >> 8 & 255) * alpha + 127) / 255);
                        pixels[at + 2] = (byte)(((color & 255) * alpha + 127) / 255);
                        pixels[at + 3] = (byte)alpha;
                    }
                    Marshal.Copy(pixels, 0, bits, bytes);
                    cache.Add(key, bitmap); PixelBytes += bytes; Builds++;
                }
                catch { DeleteObject(bitmap); throw; }
            }
            else Hits++;
            var previous = SelectObject(dc, bitmap);
            try { return AlphaBlend(target, x, y, width, height, dc, 0, 0, width, height, new Blend { Opacity = 255, Alpha = 1 }); }
            finally { SelectObject(dc, previous); }
        }

        private static double Distance(double x, double y, Key key)
        {
            double qx = Math.Abs(x - key.Width / 2.0) - (key.Width / 2.0 - key.Radius);
            double qy = Math.Abs(y - key.Height / 2.0) - (key.Height / 2.0 - key.Radius);
            double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
            return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - key.Radius;
        }

        public void Clear()
        { foreach (var bitmap in cache.Values) DeleteObject(bitmap); cache.Clear(); PixelBytes = 0; }
        public void Dispose()
        { Clear(); if (dc != IntPtr.Zero) { DeleteDC(dc); dc = IntPtr.Zero; } }
    }
}
