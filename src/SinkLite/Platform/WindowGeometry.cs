using System;

namespace SinkLite.Platform
{
    internal readonly record struct WindowBounds(int X,int Y,int Width,int Height);

    internal static class WindowGeometry
    {
        public static bool Valid(Settings s) => s.WindowWidth>0 && s.WindowWidth<=100000 &&
            s.WindowHeight>0 && s.WindowHeight<=100000 && Math.Abs((long)s.WindowX)<=1000000 && Math.Abs((long)s.WindowY)<=1000000;

        public static WindowBounds Restore(Settings s,WindowBounds work,int dpi)
        {
            int oldDpi=s.WindowDpi is >=72 and <=480 ? s.WindowDpi : 96;
            dpi=dpi is >=72 and <=480 ? dpi : 96;
            int width=Valid(s) ? (int)((long)s.WindowWidth*dpi/oldDpi) : 520*dpi/96;
            int height=Valid(s) ? (int)((long)s.WindowHeight*dpi/oldDpi) : 500*dpi/96;
            width=Math.Clamp(width,Math.Min(480*dpi/96,work.Width),work.Width);
            height=Math.Clamp(height,Math.Min(500*dpi/96,work.Height),work.Height);
            int x=Valid(s) ? s.WindowX : work.X+Math.Min(120*dpi/96,Math.Max(0,work.Width-width));
            int y=Valid(s) ? s.WindowY : work.Y+Math.Min(80*dpi/96,Math.Max(0,work.Height-height));
            return new WindowBounds(Math.Clamp(x,work.X,work.X+work.Width-width),Math.Clamp(y,work.Y,work.Y+work.Height-height),width,height);
        }
    }
}
