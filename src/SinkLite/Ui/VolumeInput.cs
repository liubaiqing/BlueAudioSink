using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SinkLite.Platform;

namespace SinkLite.UI
{
    /// <summary>Transient native edit: normal selection, clipboard, IME and caret support.</summary>
    internal sealed class VolumeInput : IDisposable
    {
        private IntPtr window, parent, previousProcedure;
        private readonly Win32.WndProcDelegate procedure;
        private readonly Action<int> accepted;
        private readonly Action invalid;
        public bool Active => window != IntPtr.Zero;
        public VolumeInput(Action<int> accepted, Action invalid)
        { this.accepted = accepted; this.invalid = invalid; procedure = WindowProc; }

        [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetProcedure(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);

        public bool Begin(IntPtr parent, Win32.RECT rect, IntPtr font, int value)
        {
            Cancel(); this.parent = parent;
            window = Win32.CreateWindowEx(0, "EDIT", value.ToString(CultureInfo.InvariantCulture),
                Win32.WS_CHILD | Win32.WS_VISIBLE | 0x00800000 | 0x2000 | 0x0080 | 0x0001,
                rect.Left, rect.Top, rect.Right-rect.Left, rect.Bottom-rect.Top,
                parent, new IntPtr(364), Win32.GetModuleHandle(null), IntPtr.Zero);
            if(window == IntPtr.Zero) return false;
            previousProcedure = SetProcedure(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
            if(previousProcedure == IntPtr.Zero) { Win32.DestroyWindow(window); window = IntPtr.Zero; return false; }
            Win32.SendMessage(window, 0x0030, font, new IntPtr(1)); // WM_SETFONT
            Win32.SendMessage(window, 0x00C5, new IntPtr(8), IntPtr.Zero); // EM_LIMITTEXT, validate rather than truncate to 100.
            SetFocus(window);
            Win32.SendMessage(window, 0x00B1, IntPtr.Zero, new IntPtr(-1));
            return true;
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp)
        {
            if(msg == 0x0087) return new IntPtr(4); // DLGC_WANTALLKEYS
            if(msg == 0x0100)
            {
                int key = wp.ToInt32();
                if(key == 0x0D) { Finish(true, true); if(!Active) SetFocus(parent); return IntPtr.Zero; }
                if(key == 0x1B) { Cancel(); SetFocus(parent); return IntPtr.Zero; }
                if(key == 0x09) { Finish(true, false); SetFocus(parent); Win32.PostMessage(parent, 0x0100, wp, lp); return IntPtr.Zero; }
            }
            if(msg == 0x0102 && (wp.ToInt32() == 13 || wp.ToInt32() == 27 || wp.ToInt32() == 9)) return IntPtr.Zero;
            if(msg == 0x0008) { Finish(true, false); return IntPtr.Zero; }
            return CallWindowProc(previousProcedure, hwnd, msg, wp, lp);
        }

        private void Finish(bool commit, bool keepInvalid)
        {
            if(!Active) return;
            int value = 0; bool valid = false;
            if(commit)
            {
                var text = new StringBuilder(32); GetWindowText(window, text, text.Capacity);
                valid = int.TryParse(text.ToString().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= 100;
                if(!valid && keepInvalid) { invalid(); Win32.SendMessage(window, 0x00B1, IntPtr.Zero, new IntPtr(-1)); return; }
            }
            var old = window; window = IntPtr.Zero;
            SetProcedure(old, -4, previousProcedure); Win32.DestroyWindow(old);
            if(commit) { if(valid) accepted(value); else invalid(); }
        }
        public void CommitOnBlur() { if(Active) SetFocus(parent); }
        public void Cancel() => Finish(false, false);
        public void Dispose() => Cancel();
    }
}
