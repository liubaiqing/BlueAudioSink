using System;
using System.Runtime.InteropServices;
using SinkLite.Platform;

namespace SinkLite.UI
{
    internal sealed partial class MainWindow
    {
        private bool trackPlacement, sizingWindow, firstShow=true;
        [StructLayout(LayoutKind.Sequential)]
        private struct Placement
        {
            public int Length,Flags,ShowCommand;
            public Win32.POINT Minimum,Maximum;
            public Win32.RECT Normal;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { public int Size; public Win32.RECT Monitor,Work; public uint Flags; }
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window,ref Placement placement);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Win32.RECT rect,uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);

        private void RestoreWindowGeometry()
        {
            if(!WindowGeometry.Valid(pageSettings)) { pageSettings.WindowMaximized=false; trackPlacement=true; return; }
            var rect=new Win32.RECT {Left=pageSettings.WindowX,Top=pageSettings.WindowY,
                Right=pageSettings.WindowX+pageSettings.WindowWidth,Bottom=pageSettings.WindowY+pageSettings.WindowHeight};
            IntPtr monitor=MonitorFromRect(ref rect,2);
            var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
            if(GetMonitorInfo(monitor,ref info)) {
                int dpi=(int)Math.Round(scale*96);
                if(GetDpiForMonitor(monitor,0,out uint x,out _)==0 && x>=72 && x<=480) dpi=(int)x;
                var work=new WindowBounds(info.Work.Left,info.Work.Top,info.Work.Right-info.Work.Left,info.Work.Bottom-info.Work.Top);
                var bounds=WindowGeometry.Restore(pageSettings,work,dpi);
                SetWindowPos(hwnd,IntPtr.Zero,bounds.X,bounds.Y,bounds.Width,bounds.Height,0x14);
                uint actual=GetDpiForWindow(hwnd);
                if(actual>=72 && actual<=480 && Math.Abs(scale-actual/96.0)>.001) { scale=actual/96.0; RecreateFonts(); }
            }
            trackPlacement=true;
        }

        private void CaptureWindowGeometry()
        {
            if(!trackPlacement || sizingWindow || windowDisposed || Win32.IsIconic(hwnd)) return;
            var placement=new Placement {Length=Marshal.SizeOf<Placement>()};
            if(!GetWindowPlacement(hwnd,ref placement)) return;
            var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
            if(!GetMonitorInfo(MonitorFromWindow(hwnd,2),ref info)) return;
            // WINDOWPLACEMENT normal bounds use workspace coordinates for this non-tool window.
            int x=placement.Normal.Left+info.Work.Left-info.Monitor.Left;
            int y=placement.Normal.Top+info.Work.Top-info.Monitor.Top;
            int width=placement.Normal.Right-placement.Normal.Left,height=placement.Normal.Bottom-placement.Normal.Top;
            int dpi=(int)GetDpiForWindow(hwnd); bool maximized=placement.ShowCommand==3;
            var settings=pageSettings.Copy();
            if(settings.WindowX==x && settings.WindowY==y && settings.WindowWidth==width && settings.WindowHeight==height && settings.WindowDpi==dpi && settings.WindowMaximized==maximized) return;
            settings.WindowX=x; settings.WindowY=y; settings.WindowWidth=width; settings.WindowHeight=height;
            settings.WindowDpi=dpi; settings.WindowMaximized=maximized;
            if(WindowGeometry.Valid(settings)) QueueSave(settings);
        }
    }
}
