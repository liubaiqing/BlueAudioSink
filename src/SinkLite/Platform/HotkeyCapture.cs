using System;
using System.Runtime.InteropServices;

namespace SinkLite.Platform
{
    /// <summary>Foreground-only, short-lived capture so an occupied chord cannot run another app.</summary>
    internal sealed class HotkeyCapture : IDisposable
    {
        public const int Message = Win32.WM_APP + 82;
        private readonly IntPtr window;
        private readonly HookProcedure procedure;
        private readonly bool[] down = new bool[256];
        private readonly bool[] modifiersDown = new bool[256];
        private IntPtr hook;
        private delegate IntPtr HookProcedure(int code, IntPtr wp, IntPtr lp);
        public HotkeyCapture(IntPtr window) { this.window=window; procedure=Callback; }
        [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int type, HookProcedure procedure, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wp,IntPtr lp);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        public bool Start()
        {
            Stop(); Array.Clear(down); Array.Clear(modifiersDown);
            foreach(int key in new[]{0xA0,0xA1,0xA2,0xA3,0xA4,0xA5,0x5B,0x5C}) modifiersDown[key]=GetAsyncKeyState(key)<0;
            hook=SetWindowsHookEx(13,procedure,Win32.GetModuleHandle(null),0);
            return hook!=IntPtr.Zero;
        }
        public void Stop() { if(hook!=IntPtr.Zero) { UnhookWindowsHookEx(hook); hook=IntPtr.Zero; } }
        private IntPtr Callback(int code,IntPtr wp,IntPtr lp)
        {
            if(code>=0 && hook!=IntPtr.Zero && GetForegroundWindow()==window)
            {
                int key=Marshal.ReadInt32(lp), message=wp.ToInt32();
                bool modifier=key==0x10 || key==0x11 || key==0x12 || (key>=0xA0 && key<=0xA5) || key==0x5B || key==0x5C;
                if(modifier) modifiersDown[key]=message==0x0100 || message==0x0104;
                if(key>=0 && key<256 && !modifier)
                {
                    if(message==0x0100 || message==0x0104)
                    {
                        if(!down[key])
                        {
                            down[key]=true;
                            int modifiers=(Held(0x11,0xA2,0xA3) ? 2 : 0) | (Held(0x12,0xA4,0xA5) ? 1 : 0) |
                                (Held(0x10,0xA0,0xA1) ? 4 : 0) | (modifiersDown[0x5B] || modifiersDown[0x5C] ? 8 : 0);
                            Win32.PostMessage(window,Message,new IntPtr(key),new IntPtr(modifiers));
                        }
                        return new IntPtr(1);
                    }
                    if((message==0x0101 || message==0x0105) && down[key]) { down[key]=false; return new IntPtr(1); }
                }
            }
            return CallNextHookEx(hook,code,wp,lp);
        }
        private bool Held(int key,int left,int right) => modifiersDown[key] || modifiersDown[left] || modifiersDown[right];
        public void Dispose() => Stop();
    }
}
