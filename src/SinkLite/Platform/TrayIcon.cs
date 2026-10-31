using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SinkLite.UI;

namespace SinkLite.Platform
{
    /// <summary>Owned and called exclusively by the window thread; never controls audio.</summary>
    internal sealed class TrayIcon : IDisposable
    {
        public const int CallbackMessage = Win32.WM_APP + 21;
        public const int RefreshMessage = Win32.WM_APP + 22;
        public const int RestoreMessage = Win32.WM_APP + 23;
        public readonly int TaskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        private readonly IntPtr window;
        private readonly Dictionary<TrayState, IntPtr> icons = new Dictionary<TrayState, IntPtr>();
        private Win32.NOTIFYICONDATA data;
        private string key;
        public bool Registered { get; private set; }

        public TrayIcon(IntPtr window)
        {
            this.window = window;
            data.cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>();
            data.hWnd = window;
            data.uID = 1;
            data.uCallbackMessage = CallbackMessage;
            data.uFlags = 1 | 2 | 4 | 0x80; // MESSAGE, ICON, TIP, SHOWTIP (version 4)
        }

        public bool Update(TrayPresentation view)
        {
            string next = view.State + "|" + view.Text;
            if (Registered && key == next) return true;
            if (!icons.TryGetValue(view.State, out IntPtr icon))
            {
                icon = CreateStatusIcon(view.State);
                if (icon == IntPtr.Zero) return false;
                icons.Add(view.State, icon);
            }
            data.hIcon = icon;
            data.szTip = "BlueAudioSink\n" + view.Text;
            if (data.szTip.Length > 127) data.szTip = data.szTip.Substring(0, 127);
            bool wasRegistered = Registered;
            if (!Win32.Shell_NotifyIcon(Registered ? 1 : 0, ref data))
            {
                // A failed modify may leave an existing Shell entry. Clear it before retrying ADD.
                Win32.Shell_NotifyIcon(2, ref data);
                Registered = false;
                key = null;
                return false;
            }
            Registered = true;
            key = next;
            if (!wasRegistered)
            {
                data.uVersion = 4;
                Win32.Shell_NotifyIcon(4, ref data); // NIM_SETVERSION
            }
            return true;
        }

        public void ShellRestarted() { Registered = false; key = null; }

        public int ShowMenu()
        {
            IntPtr menu = Win32.CreatePopupMenu();
            if (menu == IntPtr.Zero) return 0;
            try
            {
                Win32.AppendMenu(menu, 0, new IntPtr(1), "打开主界面");
                Win32.AppendMenu(menu, 0, new IntPtr(2), "彻底退出");
                Win32.GetCursorPos(out Win32.POINT point);
                Win32.SetForegroundWindow(window);
                return Win32.TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, window, IntPtr.Zero);
            }
            finally
            {
                Win32.DestroyMenu(menu);
                Win32.PostMessage(window, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public void Remove()
        {
            if (Registered) Win32.Shell_NotifyIcon(2, ref data);
            Registered = false;
            key = null;
        }

        private static IntPtr CreateStatusIcon(TrayState state)
        {
            // Supersample the shared blue/white audio mark and distinct status shapes.
            const int size = 32, samples = 4;
            byte[] pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int sy = 0; sy < samples; sy++)
                        for (int sx = 0; sx < samples; sx++)
                        {
                            double px = x + (sx + .5) / samples, py = y + (sy + .5) / samples;
                            int cr = 29, cg = 110, cb = 240;
                            bool bar = (px >= 8 && px < 11 && py >= 13 && py < 20) ||
                                (px >= 13 && px < 16 && py >= 7 && py < 25) ||
                                (px >= 18 && px < 21 && py >= 4 && py < 28) ||
                                (px >= 23 && px < 26 && py >= 12 && py < 22);
                            if (bar) cr = cg = cb = 255;
                            double dx = px - 24, dy = py - 24, d = dx * dx + dy * dy;
                            if (d < 65)
                            {
                                cr = cg = cb = 255;
                                bool shape = false;
                                switch (state)
                                {
                                    case TrayState.Waiting: cr = 95; cg = 108; cb = 125; shape = Math.Abs(dx) < 3.5 && Math.Abs(dy) < 3.5; break;
                                    case TrayState.Connected: cr = 0; cg = 110; cb = 220; shape = d < 25 && d > 9; break;
                                    case TrayState.Playing: cr = 0; cg = 145; cb = 65; shape = dx >= -3 && dx < 4 && Math.Abs(dy) < (4 - dx) * .75; break;
                                    case TrayState.Preparing: cr = 180; cg = 110; cb = 0; shape = Math.Abs(dy) < 4 && Math.Abs(dx) < Math.Abs(dy) + .7; break;
                                    case TrayState.Warning: cr = 190; cg = 115; cb = 0; shape = Math.Abs(dx) < 1.2 && ((dy > -4.5 && dy < 1) || (dy > 2 && dy < 4.5)); break;
                                    case TrayState.Faulted: cr = 210; cg = 40; cb = 45; shape = Math.Abs(dx) < 4.5 && Math.Abs(dy) < 4.5 && (Math.Abs(dx - dy) < 1.5 || Math.Abs(dx + dy) < 1.5); break;
                                }
                                if (!shape) cr = cg = cb = 255;
                            }
                            r += cr; g += cg; b += cb;
                        }
                    int i = (y * size + x) * 4;
                    pixels[i] = (byte)(b / 16); pixels[i + 1] = (byte)(g / 16); pixels[i + 2] = (byte)(r / 16); pixels[i + 3] = 255;
                }
            return Win32.CreateIcon(Win32.GetModuleHandle(null), size, size, 1, 32, new byte[size * 4], pixels);
        }

        public void Dispose()
        {
            Remove();
            foreach (IntPtr icon in icons.Values) Win32.DestroyIcon(icon);
            icons.Clear();
        }
    }
}
