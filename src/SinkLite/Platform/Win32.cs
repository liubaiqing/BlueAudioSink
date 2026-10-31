using System;
using System.Runtime.InteropServices;

namespace SinkLite.Platform
{
    /// <summary>
    /// Win32 声明集中处。托盘与主窗口共用，避免重复定义。
    /// </summary>
    internal static class Win32
    {
        [DllImport("user32.dll")]
        public static extern bool DrawIconEx(IntPtr dc,int x,int y,IntPtr icon,int width,int height,uint step,IntPtr brush,uint flags);

        public const int WM_CREATE = 0x0001;
        public const int WM_DESTROY = 0x0002;
        public const int WM_CLOSE = 0x0010;
        public const int WM_PAINT = 0x000F;
        public const int WM_ERASEBKGND = 0x0014;
        public const int WM_SIZE = 0x0005;
        public const int WM_COMMAND = 0x0111;
        public const int WM_TIMER = 0x0113;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_LBUTTONDBLCLK = 0x0203;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_MOUSELEAVE = 0x02A3;
        public const int WM_SETCURSOR = 0x0020;
        public const int IDC_HAND = 32649;
        public const int TME_LEAVE = 0x00000002;
        public const int WM_APP = 0x8000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string message);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")]
        public static extern IntPtr CreateIcon(IntPtr instance, int width, int height,
            byte planes, byte bitsPerPixel, byte[] andBits, byte[] xorBits);

        public const int WS_OVERLAPPED = 0x00000000;
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_SYSMENU = 0x00080000;
        public const int WS_MINIMIZEBOX = 0x00020000;
        public const int WS_MAXIMIZEBOX = 0x00010000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CHILD = 0x40000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        public const int DT_LEFT = 0x00000000;
        public const int DT_WORDBREAK = 0x00000010;
        public const int DT_RIGHT = 0x00000002;
        public const int DT_CENTER = 0x00000001;
        public const int DT_VCENTER = 0x00000004;
        public const int DT_SINGLELINE = 0x00000020;
        public const int DT_END_ELLIPSIS = 0x00008000;
        public const int DT_NOCLIP = 0x00000100;

        public const int TRANSPARENT = 1;
        public const int PS_SOLID = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public int dwFlags;
            public IntPtr hwndTrack;
            public int dwHoverTime;
        }

        [DllImport("user32.dll")]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int ptX;
            public int ptY;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        [DllImport("user32.dll")] public static extern short GetKeyState(int key);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] public static extern bool CloseClipboard();
        [DllImport("user32.dll")] public static extern bool EmptyClipboard();
        [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr memory);

        public delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hWnd, int min, int max);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string className, string title);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBox(IntPtr window, string text, string caption, uint type);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

        [DllImport("user32.dll")]
        public static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

        [DllImport("user32.dll")]
        public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, int elapse, IntPtr proc);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr id);

        [DllImport("user32.dll")]
        public static extern int GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll")]
        public static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(
            IntPtr dest, int x, int y, int width, int height,
            IntPtr source, int srcX, int srcY, int rop);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        public const int SRCCOPY = 0x00CC0020;

        /// <summary>Per-Monitor V2 DPI 感知上下文。</summary>
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr instance, int cursor);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void DestroyIcon(IntPtr icon);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreateIconFromResourceEx(
            byte[] resourceBits, uint resourceSize, bool isIcon, uint version,
            int desiredWidth, int desiredHeight, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr menu, int flags, IntPtr id, string text);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenu(IntPtr menu, int flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBox(IntPtr hWnd, string text, string caption, int type);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);

        // GDI
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateSolidBrush(int color);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreatePen(int style, int width, int color);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern IntPtr GetStockObject(int index);

        public const int HOLLOW_BRUSH = 5;

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern bool Rectangle(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        public static extern bool FrameRect(IntPtr hdc, ref RECT rect, IntPtr brush);

        [DllImport("gdi32.dll")]
        public static extern int SetBkColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll")]
        public static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        public static extern int SetTextColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll")]
        public static extern int SetBkMode(IntPtr hdc, int mode);

        // 必须显式声明 Unicode 字符集：否则 P/Invoke 会按 ANSI 调用，
        // 字体名与文本会被错误转换，表现为界面文字发糊。
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(
            int height, int width, int escapement, int orientation, int weight,
            uint italic, uint underline, uint strikeOut, uint charSet,
            uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
            [MarshalAs(UnmanagedType.LPWStr)] string faceName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")]
        public static extern int DrawText(IntPtr hdc, [MarshalAs(UnmanagedType.LPWStr)] string text, int length, ref RECT rect, int format);

        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;
        public const int IDC_ARROW = 32512;
        public const int IDI_APPLICATION = 32512;

        public static int Rgb(int r, int g, int b)
        {
            return (b << 16) | (g << 8) | r;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }
    }
}
