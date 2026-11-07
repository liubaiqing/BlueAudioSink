using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite.UI
{
    /// <summary>
    /// 主窗口：Win32 + GDI 手绘，可选原生通知区域常驻。
    ///
    /// 设计：
    ///   - 主页面：只读连接状态和设备流向、连接/断开操作；
    ///   - 设置页面：统一播放输出、连接设备切换和通用偏好；
    ///   - 输出下拉为就地展开的自绘列表；
    ///   - 按钮具备 常态 / 悬停 / 按下 三态；
    ///   - 关闭窗口按偏好退出或缩小到托盘。
    /// </summary>
    internal sealed partial class MainWindow : IDisposable
    {
        private const int IdTimer = 1;
        private const int IdHelpTimer = 2;
        private const int AreaBufferHelp = 350;
        private const int AreaFirewallHelp = 351;
        private const int MaxDeviceRows = 5;
        private const int MaxDropdownRows = 6;

        private const int PadX = 16;
        private const int DeviceRowHeight = 34;
        private const int DropdownRowHeight = 32;

        private const int AreaNone = -1;
        private const int AreaConnect = 1;
        private const int AreaDisconnect = 2;
        private const int AreaRefresh = 3;
        private const int AreaSettings = 4;
        private const int AreaSettingsBack = 6;
        private const int AreaOutput = 5;
        private const int AreaDeviceBase = 100;
        private const int AreaDropdownBase = 200;
        private const int AreaDropdownLimit = 300;
        private const int AreaAutoStart = 301;
        private const int AreaAutoConnect = 302;
        private const int AreaCloseToTray = 304;
        private const int AreaModeBluetooth = 310;
        private const int AreaModeWifi = 311;
        private const int AreaDetails = 312;
        private const int AreaCopyAddress = 313;
        private const int AreaRetryWifi = 314;
        private const int AreaWifiEnable = 315, AreaWifiDisconnect = 316;
        private const int AreaBuffer80 = 320;
        private const int AreaBuffer120 = 321;
        private const int AreaBuffer160 = 322;
        private const int AreaSettingsGeneral = 330;
        private const int AreaSettingsDevices = 331;
        private const int AreaPreviousPage = 332;
        private const int AreaNextPage = 333;
        private const int AreaDeviceBluetooth = 334;
        private const int AreaDeviceWifi = 335;
        private const int AreaSettingsNetwork = 340;
        private const int AreaFirewallEnable = 341;
        private const int AreaFirewallDisable = 342;
        private const int AreaFirewallCheck = 343;
        private const int AreaExclusive = 360;
        private const int AreaVolumeDown = 361;
        private const int AreaVolumeUp = 362;
        private const int AreaVolumeValue = 364;
        private VolumeInput volumeInput;
        private const int AreaOutputStatus = 363;

        private readonly SinkController controller;
        private readonly Log log;
        private readonly WifiController wifi;

        private IntPtr hwnd;
        private TrayIcon tray;
        private bool hiddenToTray;
        private string trayStatusKey;
        private IntPtr appIcon, smallAppIcon;
        private Win32.WndProcDelegate wndProc;
        private IntPtr fontTitle;
        private IntPtr fontGear;
        private IntPtr fontValue;
        private IntPtr fontHeading;
        private IntPtr fontBody;
        private IntPtr fontSmall;
        private IntPtr fontCaption;
        private IntPtr brushBack;
        private IntPtr brushCard;
        private IntPtr brushBorder;
        private IntPtr brushAccent;
        private IntPtr brushAccentHover;
        private IntPtr brushAccentPress;
        private IntPtr brushBtn;
        private IntPtr brushBtnHover;
        private IntPtr brushBtnPress;
        private IntPtr brushRowHover;
        private IntPtr brushRowSelected;
        private IntPtr brushSwitchOff;
        private IntPtr brushSwitchOffHover;
        private IntPtr brushTooltip;
        private int helpArea = AreaNone, helpX, helpY;
        private bool helpVisible;

        private double scale = 1.0;
        private bool mouseInside;
        private bool settingsMode;
        private bool wifiMode;
        private bool browsingWifiDevices;
        private bool wifiDetails;
        private bool modeSwitching;
        private int focusArea = AreaNone;
        private bool keyboardFocus;
        private Settings pageSettings;
        private bool autoStartEnabled;
        private string notice;
        private DateTime noticeUntil;

        private bool deviceSettings;
        private bool networkSettings;
        private FirewallStatus firewallStatus = new FirewallStatus();
        private Task<FirewallResult> firewallTask;
        private bool firewallChanging;
        private string firewallMessage;
        private int devicePage;
        private sealed class DeviceChoice
        {
            public string Id;
            public string Name;
            public string Status;
            public bool Selected;
        }
        private List<DeviceChoice> connectionDevices = new List<DeviceChoice>();
        private string deviceListKey = "";
        private int hoverArea = AreaNone;
        private int pressedArea = AreaNone;
        private bool dropdownOpen;

        private IReadOnlyList<EndpointInfo> cachedOutputs = new List<EndpointInfo>();
        private string cachedPreferredName = "选择输出设备";
        private DateTime lastOutputRefresh = DateTime.MinValue;
        private string lastPaintKey = string.Empty;
        private Task<IReadOnlyList<EndpointInfo>> outputRefreshTask;
        private Func<IReadOnlyList<EndpointInfo>> readOutputs;
        private const int OutputRefreshMessage = 0x8062;
        private volatile bool windowDisposed;

        private Win32.RECT dropdownRect;
        private int dropdownRows;

        public MainWindow(SinkController controller, Log log, bool startWifi)
        {
            this.controller = controller;
            this.log = log;
            readOutputs = controller.ListPhysicalOutputs;

            Settings settings = Settings.Load();
            pageSettings = settings;
            settingsWriter.Completed += SettingsWritten;
            wifi = new WifiController(log, settings.WifiPort, settings.BufferBaseMs, settings.BufferMaxMs, new WifiOutputOptions(settings.WifiExclusive,settings.WifiVolume,settings.PreferredRenderId));
            wifi.RemoteVolumeChanged += () => Win32.PostMessage(hwnd, 0x8051, IntPtr.Zero, IntPtr.Zero);
            wifiMode = startWifi || string.Equals(settings.Mode, "wifi", StringComparison.OrdinalIgnoreCase);
        }

        private int S(int value)
        {
            return (int)Math.Round(value * scale);
        }

        public void Create()
        {
            try
            {
                Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch
            {
            }

            uint dpi = Win32.GetDpiForSystem();
            if (dpi < 72 || dpi > 480)
            {
                dpi = 96;
            }
            scale = dpi / 96.0;

            darkTheme = IsDarkTheme(pageSettings.Theme);
            CreateThemeBrushes();
            RecreateFonts();

            wndProc = WindowProc;
            IntPtr instance = Win32.GetModuleHandle(null);

            Win32.WNDCLASSEX wc = new Win32.WNDCLASSEX();
            wc.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.WNDCLASSEX));
            wc.lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wndProc);
            wc.hInstance = instance;
            wc.hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW);
            wc.hIcon = appIcon = LoadAppIcon(S(32));
            wc.hIconSm = smallAppIcon = LoadAppIcon(S(16));
            wc.lpszClassName = "BlueAudioSinkMainWindow";
            ushort registered = Win32.RegisterClassEx(ref wc);
            int registrationError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (registered == 0 && registrationError != 1410)
                throw new System.ComponentModel.Win32Exception(registrationError, "注册主窗口失败");

            hwnd = Win32.CreateWindowEx(
                0,
                "BlueAudioSinkMainWindow",
                "BlueAudioSink",
                Win32.WS_CAPTION | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX | 0x00040000 | 0x02000000,
                S(120), S(80), S(520), S(500),
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                UiError("Ui.CreateFailed", ("win32Error", error));
                throw new System.ComponentModel.Win32Exception(error, "创建主窗口失败");
            }
            ApplyCaptionTheme();
            UiLog("Ui.WindowHandle", ("hwnd", hwnd.ToInt64()));
            Win32.SendMessage(hwnd, 0x0080, new IntPtr(1), wc.hIcon); // WM_SETICON, ICON_BIG
            Win32.SendMessage(hwnd, 0x0080, IntPtr.Zero, wc.hIconSm); // ICON_SMALL
            Win32.SetTimer(hwnd, new IntPtr(IdTimer), 1000, IntPtr.Zero);
            hotkeys = new GlobalHotkeys(new NativeHotkeyRegistry(hwnd), pageSettings.Hotkeys);
            for(int i=0;i<4;i++) if(!hotkeys[i].Empty && !hotkeys.Active(i)) { ShowNotice("部分快捷键不可用，请到快捷键设置中修改",6); break; }
            tray = new TrayIcon(hwnd);
            UpdateTray();
            controller.SnapshotChanged += OnSnapshotChanged;
            UiLog("Ui.Created", ("dpi", (int)dpi), ("scale", scale), ("mode", wifiMode ? "wifi" : "bluetooth"));


        }

        public void ApplyAutoConnect(bool explicitRequest = false)
        {
            if(!pageSettings.AutoConnect && !explicitRequest) return;
            if(wifiMode) EnableWifi(false);
            else controller.RequestConnect(string.IsNullOrEmpty(pageSettings.DeviceId) ? null : pageSettings.DeviceId);
            UiLog("Ui.AutoConnectRequested", ("mode",wifiMode ? "wifi" : "bluetooth"),("explicit",explicitRequest));
        }

        /// <summary>从嵌入资源加载窗口图标，避免额外文件依赖。</summary>
        private static IntPtr LoadAppIcon(int size)
        {
            try
            {
                using (Stream stream = typeof(MainWindow).Assembly.GetManifestResourceStream("app.ico"))
                {
                    if (stream == null)
                    {
                        return IntPtr.Zero;
                    }

                    byte[] data = new byte[stream.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = stream.Read(data, read, data.Length - read);
                        if (n <= 0)
                        {
                            break;
                        }
                        read += n;
                    }

                    // CreateIconFromResourceEx expects a single RT_ICON image, not the ICO directory.
                    if (read < 6 || BitConverter.ToUInt16(data, 2) != 1) return IntPtr.Zero;
                    int count = BitConverter.ToUInt16(data, 4);
                    int bestOffset = 0, bestLength = 0, bestDistance = int.MaxValue;
                    for (int i = 0; i < count && 6 + (i + 1) * 16 <= read; i++)
                    {
                        int entry = 6 + i * 16;
                        int width = data[entry] == 0 ? 256 : data[entry];
                        int height = data[entry + 1] == 0 ? 256 : data[entry + 1];
                        uint length = BitConverter.ToUInt32(data, entry + 8);
                        uint offset = BitConverter.ToUInt32(data, entry + 12);
                        int distance = Math.Abs(width - size) + Math.Abs(height - size);
                        if (length > 0 && (ulong)offset + length <= (ulong)read && distance < bestDistance)
                        {
                            bestOffset = (int)offset; bestLength = (int)length; bestDistance = distance;
                        }
                    }
                    if (bestLength == 0) return IntPtr.Zero;
                    byte[] image = new byte[bestLength];
                    Buffer.BlockCopy(data, bestOffset, image, 0, bestLength);
                    return Win32.CreateIconFromResourceEx(image, (uint)image.Length, true, 0x00030000u, size, size, 0u);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private void RecreateFonts()
        {
            smoothShapes?.Clear();
            Win32.DeleteObject(fontTitle);
            Win32.DeleteObject(fontValue);
            Win32.DeleteObject(fontHeading);
            Win32.DeleteObject(fontBody);
            Win32.DeleteObject(fontSmall);
            Win32.DeleteObject(fontCaption);
            Win32.DeleteObject(fontGear);
            fontGear = Win32.CreateFontW(-S(20), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe MDL2 Assets");
            fontValue = Win32.CreateFontW(-S(24), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontHeading = Win32.CreateFontW(-S(14), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontTitle = Win32.CreateFontW(-S(20), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontBody = Win32.CreateFontW(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontSmall = Win32.CreateFontW(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontCaption = Win32.CreateFontW(-S(13), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        }

        public void ShowWindow()
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            hiddenToTray = false;
            if (Win32.IsIconic(hwnd))
            {
                Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            }
            else
            {
                Win32.ShowWindow(hwnd, Win32.SW_SHOW);
            }
            // The first ShowWindow call can obey a launcher's SW_HIDE startup flag.
            // A window-only application must explicitly show its UI after that call.
            Win32.ShowWindow(hwnd, Win32.SW_SHOW);
            Win32.SetForegroundWindow(hwnd);
            RefreshOutputCache(true);
            RequestRepaint(true);
        }

        private void OnSnapshotChanged(object sender, SinkSnapshot snapshot)
        {
            Win32.PostMessage(hwnd, TrayIcon.RefreshMessage, IntPtr.Zero, IntPtr.Zero);
        }

        private bool UpdateTray()
        {
            if (tray == null) return false;
            if (!pageSettings.CloseToTray) { tray.Remove(); trayStatusKey = null; return false; }
            TrayPresentation view = TrayPresentation.Create(wifiMode, modeSwitching, controller.Current, wifi.Current);
            bool ready = tray.Update(view);
            string key = ready ? view.State + "|" + view.Text : "unavailable";
            if (trayStatusKey != key)
            {
                trayStatusKey = key;
                UiLog("Ui.TrayStatus", ("registered", ready), ("state", view.State.ToString()), ("text", view.Text));
            }
            if (!ready && hiddenToTray)
            {
                ShowNotice("托盘暂不可用，已恢复主界面");
                ShowWindow();
            }
            return ready;
        }

        private void RequestRepaint(bool force)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // Input changes already know that pixels changed. Do not poll connection state
            // (or build its diagnostic key) in the immediate hover feedback path.
            if (!force)
            {
                string key = BuildPaintKey();
                if (key == lastPaintKey) return;
                lastPaintKey = key;
            }
            else lastPaintKey = string.Empty;
            Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        private string BuildPaintKey()
        {
            SinkSnapshot s = controller.Current;
            WifiSnapshot w = wifi.Current;
            return string.Concat(
                (int)s.State, "|", s.Detail ?? "", "|", s.DeviceName ?? "", "|",
                s.Peak.ToString("0.000"), "|", s.Hint ?? "", "|",
                deviceSettings, "|", networkSettings, "|", hotkeySettings, "|", aboutSettings, "|", logSettings, "|", log.WriteFailed, "|", recordingHotkey, "|", firewallTask != null, "|", firewallMessage, "|", firewallStatus.Available, "|", firewallStatus.RuleReady, "|", devicePage, "|", deviceListKey, "|", hoverArea, "|", pressedArea, "|",
                dropdownOpen ? 1 : 0, "|", settingsMode ? 1 : 0, "|",
                browsingWifiDevices, "|", controller.Devices.Count, "|", cachedOutputs.Count, "|", cachedPreferredName, "|",
                wifiMode ? 1 : 0, "|", (int)w.State, "|", w.Enabled ? 1 : 0, "|",
                w.Detail ?? "", "|", w.Remote ?? "", "|", w.DeviceName ?? "", "|",
                w.LocalAddress ?? "", "|", w.NetworkGeneration, "|",
                double.IsNaN(w.RttMs) ? "-1" : Math.Round(w.RttMs).ToString(), "|",
                w.BufferMs, "|", w.TargetMs, "|",
                double.IsNaN(w.LatencyMs) ? "-1" : Math.Round(w.LatencyMs).ToString(), "|",
                double.IsNaN(w.LossPercent) ? "-1" : Math.Round(w.LossPercent, 1).ToString(), "|",
                w.Received, "|", w.Missing, "|", w.Discontinuities, "|", w.Underruns, "|",
                w.HasAudibleSignal, "|", w.OutputActive, "|", w.Recovering, "|", w.Draining, "|",
                w.OutputName, "|", w.OutputMode, "|", w.OutputFormat, "|", w.OutputFallbackReason, "|", w.VolumePercent, "|", (int)(w.LastAudioAgeMs / 1000), "|", (int)(w.ControlAgeMs / 1000), "|",
                wifiDetails, "|", focusArea, "|", keyboardFocus, "|", modeSwitching, "|",
                DateTime.Now < noticeUntil ? notice : "");
        }

        private void OnTimer()
        {
            UpdateTray();
            RefreshOutputCache(false);
            RefreshConnectionDevices();
            if (firewallTask != null && firewallTask.IsCompleted)
            {
                try
                {
                    FirewallResult result = firewallTask.GetAwaiter().GetResult();
                    if (result.Status != null)
                    {
                        firewallStatus = result.Status;
                        UiLog("Wifi.FirewallStatus", ("available", firewallStatus.Available),
                            ("ruleReady", firewallStatus.RuleReady), ("privateActive", firewallStatus.PrivateActive),
                            ("inboundBlocked", firewallStatus.InboundBlocked), ("modifyState", firewallStatus.ModifyState));
                    }
                    firewallMessage = result.Message;
                }
                catch { firewallMessage = "操作失败，请重新检测。"; }
                firewallTask = null;
                RequestRepaint(true);
            }
            RequestRepaint(false);
        }

        private void BeginFirewallOperation(int area)
        {
            if (firewallTask != null) return;
            int port = wifi.Port;
            IntPtr owner = hwnd;
            firewallChanging = area != AreaFirewallCheck;
            UiLog("Ui.FirewallRequested", ("operation", area == AreaFirewallCheck ? "query" : area == AreaFirewallEnable ? "enable" : "disable"), ("port", port));
            firewallMessage = null;
            firewallTask = area == AreaFirewallCheck
                ? Task.Run(() => new FirewallResult { Status = WifiFirewall.Query(port) })
                : Task.Run(() => WifiFirewall.RequestChange(area == AreaFirewallEnable, port, owner));
            RequestRepaint(true);
        }

        private void RefreshConnectionDevices()
        {
            var choices = new List<DeviceChoice>();
            if (browsingWifiDevices)
            {
                foreach (WifiPeer peer in wifi.ListPeers())
                    choices.Add(new DeviceChoice { Id = peer.Id, Name = peer.Name, Selected = peer.Selected || peer.Active,
                        Status = peer.Selected ? "已选择" : peer.Active ? "当前设备" : peer.Recent ? "近期活动" : "未确认在线" });
            }
            else
            {
                SinkSnapshot snapshot = controller.Current;
                foreach (SinkDevice device in controller.Devices)
                {
                    bool active = device.Id == snapshot.DeviceId;
                    bool connected = active && (snapshot.State == SinkState.LinkReady || snapshot.State == SinkState.Flowing);
                    bool preferred = device.Id == pageSettings.DeviceId;
                    choices.Add(new DeviceChoice { Id = device.Id, Name = device.Name, Selected = preferred,
                        Status = active && snapshot.State == SinkState.Busy ? "连接中" : connected ? "已连接" : active && snapshot.State == SinkState.Armed ? "等待音频" : preferred ? "首选设备" : "已配对" });
                }
            }
            connectionDevices = choices;
            devicePage = Math.Clamp(devicePage, 0, Math.Max(0, (choices.Count - 1) / MaxDeviceRows));
            deviceListKey = string.Join("|", choices.ConvertAll(d => d.Id + d.Name + d.Status + d.Selected));
        }

        private void RefreshOutputCache(bool force)
        {
            if (outputRefreshTask != null)
            {
                if (!outputRefreshTask.IsCompleted) return;
                try { cachedOutputs = outputRefreshTask.GetAwaiter().GetResult(); }
                catch { /* Keep the last complete inventory on a transient device error. */ }
                outputRefreshTask = null;
                UpdatePreferredOutputName();
                RequestRepaint(true);
            }
            if (!force && DateTime.Now - lastOutputRefresh < TimeSpan.FromSeconds(10))
            {
                return;
            }

            lastOutputRefresh = DateTime.Now;
            IntPtr target = hwnd;
            outputRefreshTask = Task.Run(() => readOutputs());
            _ = outputRefreshTask.ContinueWith(completed => {
                _ = completed.Exception;
                if (!windowDisposed) Win32.PostMessage(target, OutputRefreshMessage, IntPtr.Zero, IntPtr.Zero);
            }, TaskScheduler.Default);
        }

        private void UpdatePreferredOutputName()
        {
            string preferred = null;
            foreach (EndpointInfo o in cachedOutputs)
            {
                if (o.Id == controller.PreferredRenderId)
                {
                    preferred = o.Name;
                    break;
                }
            }
            cachedPreferredName = preferred ?? "跟随电脑默认输出";
        }

        // ===== 布局 =====

        private struct Metrics
        {
            public int Width;
            public int Height;
            public int Left;
            public int ContentWidth;
            public int NavLeft;
            public int NavWidth;
            public int DeviceListTop;
            public int VisibleRows;
            public int OutputLabelTop;
            public int ButtonTop;
            public int ButtonWidth;
            public int ButtonGap;
            public Win32.RECT ConnectRect;
            public Win32.RECT DisconnectRect;
            public Win32.RECT RefreshRect;
            public Win32.RECT SettingsRect;
            public Win32.RECT SettingsBackRect;
            public Win32.RECT OutputRect;
            public Win32.RECT ModeBluetoothRect;
            public Win32.RECT ModeWifiRect;
            public Win32.RECT DetailsRect;
            public Win32.RECT CopyAddressRect;
            public Win32.RECT Buffer80Rect;
            public Win32.RECT Buffer120Rect;
            public Win32.RECT Buffer160Rect;
        }

        private Metrics ComputeMetrics()
        {
            Win32.RECT client;
            Win32.GetClientRect(hwnd, out client);
            int width = client.Right - client.Left, height = client.Bottom - client.Top;
            Metrics m = new Metrics();
            m.Width = width; m.Height = height;
            m.ContentWidth = Math.Max(S(300), Math.Min(width - S(PadX) * 2, S(680)));
            m.Left = (width - m.ContentWidth) / 2;
            m.NavLeft = m.Left; m.NavWidth = m.ContentWidth;
            int right = m.Left + m.ContentWidth;
            int middle = m.NavLeft + m.NavWidth / 2;
            m.SettingsRect = Rect(right - S(44), S(60), right, S(104));
            m.SettingsBackRect = Rect(m.NavLeft, S(10), m.NavLeft + S(76), S(42));
            m.ModeBluetoothRect = Rect(m.NavLeft, S(10), middle, S(42));
            m.ModeWifiRect = Rect(middle, S(10), right, S(42));
            if (settingsMode) { m.Left += S(104); m.ContentWidth -= S(104); right = m.Left + m.ContentWidth; }
            m.DeviceListTop = S(120);
            m.VisibleRows = Math.Min(MaxDeviceRows, Math.Max(0, connectionDevices.Count - devicePage * MaxDeviceRows));
            m.ButtonTop = S(wifiMode && !settingsMode ? 328 : 246);
            m.ButtonGap = S(8);
            m.ButtonWidth = (m.ContentWidth - m.ButtonGap) / 2;
            int x = m.Left;
            m.ConnectRect = Rect(x, m.ButtonTop, x + m.ButtonWidth, m.ButtonTop + S(34));
            x = m.ConnectRect.Right + m.ButtonGap;
            m.DisconnectRect = Rect(x, m.ButtonTop, x + m.ButtonWidth, m.ButtonTop + S(34));
            m.RefreshRect = Rect(right - S(76), S(66), right, S(96));
            int chipWidth = (m.ContentWidth - S(112)) / 3;
            m.Buffer80Rect = Rect(m.Left + S(100), S(358), m.Left + S(100) + chipWidth, S(390));
            m.Buffer120Rect = Rect(m.Buffer80Rect.Right + S(6), S(358), m.Buffer80Rect.Right + S(6) + chipWidth, S(390));
            m.Buffer160Rect = Rect(m.Buffer120Rect.Right + S(6), S(358), right, S(390));
            m.DetailsRect = Rect(right - S(84), S(370), right, S(398));
            m.CopyAddressRect = Rect(m.Left + S(174), S(370), m.Left + S(222), S(398));
            m.OutputLabelTop = S(76);
            m.OutputRect = Rect(m.Left + S(12), S(98), right - S(12), S(132));
            return m;
        }

        private Win32.RECT SettingsControlRect(Metrics m, int area)
        {
            int right = m.Left + m.ContentWidth;
            switch (area)
            {
                case AreaSettingsGeneral: return Rect(m.NavLeft, S(68), m.NavLeft + S(88), S(106));
                case AreaSettingsDevices: return Rect(m.NavLeft, S(114), m.NavLeft + S(88), S(152));
                case AreaSettingsNetwork: return Rect(m.NavLeft, S(160), m.NavLeft + S(88), S(198));
                case AreaSettingsHotkeys: return Rect(m.NavLeft, S(206), m.NavLeft + S(88), S(244));
                case AreaSettingsLogs: return Rect(m.NavLeft, S(252), m.NavLeft + S(88), S(290));
                case AreaSettingsAbout: return Rect(m.NavLeft, S(298), m.NavLeft + S(88), S(336));
                case AreaFirewallEnable: return Rect(m.Left, S(174), m.Left + (m.ContentWidth - S(8)) / 2, S(208));
                case AreaFirewallDisable: return Rect(m.Left + (m.ContentWidth + S(8)) / 2, S(174), right, S(208));
                case AreaFirewallCheck: return Rect(m.Left, S(220), m.Left + S(104), S(252));
                case AreaDeviceBluetooth: return Rect(m.Left, S(66), m.Left + S(88), S(96));
                case AreaDeviceWifi: return Rect(m.Left + S(100), S(66), m.Left + S(188), S(96));
                case AreaPreviousPage: return Rect(right - S(142), m.Height - S(68), right - S(76), m.Height - S(38));
                default: return Rect(right - S(66), m.Height - S(68), right, m.Height - S(38));
            }
        }

        private Win32.RECT PreferenceRect(Metrics m, int area)
        {
            int top = S(area == AreaExclusive ? 138 : area == AreaAutoStart ? 246 : area == AreaCloseToTray ? 280 : 314);
            return Rect(m.Left + S(8), top, m.Left + m.ContentWidth - S(8), top + S(32));
        }

        private Win32.RECT VolumeRect(Metrics m, bool up)
        {
            int right=m.Left+m.ContentWidth-S(12);
            return up ? Rect(right-S(32),S(178),right,S(208)) : Rect(right-S(72),S(178),right-S(40),S(208));
        }

        private Win32.RECT VolumeValueRect(Metrics m) => Rect(m.Left + S(98), S(178), m.Left + S(156), S(208));

        private void SetWifiVolume(int value)
        {
            var preferences = pageSettings.Copy();
            preferences.WifiVolume = value;
            QueueSave(preferences); ApplyOutputPreferences(); RequestRepaint(true);
        }

        private void EditWifiVolume()
        {
            volumeInput?.Dispose();
            volumeInput = new VolumeInput(SetWifiVolume, () => { ShowNotice("请输入 0–100 的整数"); RequestRepaint(true); });
            if (!volumeInput.Begin(hwnd, VolumeValueRect(ComputeMetrics()), fontBody, pageSettings.WifiVolume))
                ShowNotice("无法打开音量输入框，请重试");
        }

        private void ApplyOutputPreferences()
        {
            wifi.SetOutputOptions(new WifiOutputOptions(pageSettings.WifiExclusive,pageSettings.WifiVolume,pageSettings.PreferredRenderId));
        }

        private string OutputStatusText()
        {
            if(!wifiMode)return "蓝牙输出由 Windows 管理";
            var status=wifi?.Current;
            if(status?.OutputMode==AudioOutputMode.Exclusive)return "当前：独占 · "+status.OutputFormat;
            if(status?.OutputMode==AudioOutputMode.Shared)return string.IsNullOrEmpty(status.OutputFallbackReason) ? "当前：共享 · "+status.OutputFormat : "当前：共享（独占不可用）";
            if(status?.State==SinkState.Faulted)return status.Detail;
            return pageSettings.WifiExclusive ? "等待音频 · 优先独占" : "等待音频 · 共享输出";
        }

        private void ComputeDropdown(Metrics m)
        {
            dropdownRows = Math.Max(1, Math.Min(MaxDropdownRows, cachedOutputs.Count));
            if (dropdownRows == 0)
            {
                dropdownRect = Rect(m.OutputRect.Left, m.OutputRect.Bottom, m.OutputRect.Right, m.OutputRect.Bottom);
                return;
            }

            int height = dropdownRows * S(DropdownRowHeight) + S(8);
            int top = m.OutputRect.Bottom + S(4);
            if (top + height > m.Height - S(8))
            {
                top = Math.Max(S(8), m.OutputRect.Top - S(4) - height);
            }

            dropdownRect = Rect(m.OutputRect.Left, top, m.OutputRect.Right, top + height);
        }

        private int HitTest(int x, int y)
        {
            Metrics m = ComputeMetrics();
            if (modeSwitching) return AreaNone;
            if (settingsMode)
            {
                if (Contains(m.SettingsBackRect, x, y)) return AreaSettingsBack;
            }
            else
            {
                if (Contains(m.ModeBluetoothRect, x, y)) return AreaModeBluetooth;
                if (Contains(m.ModeWifiRect, x, y)) return AreaModeWifi;
                if (Contains(m.SettingsRect, x, y)) return AreaSettings;
            }
            if (dropdownOpen)
            {
                ComputeDropdown(m);
                if (Contains(dropdownRect, x, y))
                {
                    int i = (y - dropdownRect.Top - S(4)) / S(DropdownRowHeight);
                    return i >= 0 && i < dropdownRows ? AreaDropdownBase + i : AreaNone;
                }
            }
            if (settingsMode)
            {
                foreach (int tab in new[] { AreaSettingsGeneral, AreaSettingsDevices, AreaSettingsNetwork, AreaSettingsHotkeys, AreaSettingsLogs, AreaSettingsAbout })
                    if (Contains(SettingsControlRect(m, tab), x, y)) return tab;
                if (logSettings)
                {
                    foreach (int action in LogActions)
                        if (Contains(LogControlRect(m, action), x, y)) return action;
                    return AreaNone;
                }
                if(aboutSettings)
                {
                    if(Contains(AboutButtonRect(m,false),x,y)||Contains(ProjectAddressRect(m),x,y)) return AreaProjectPage;
                    if(Contains(AboutButtonRect(m,true),x,y)) return AreaLicensePage;
                    return AreaNone;
                }
                if(hotkeySettings)
                {
                    for(int i=0;i<4;i++)
                    {
                        if(Contains(HotkeyRect(m,i,false),x,y)) return AreaHotkeyBind+i;
                        if(Contains(HotkeyRect(m,i,true),x,y)) return IsEnabled(AreaHotkeyClear+i) ? AreaHotkeyClear+i : AreaNone;
                    }
                    return AreaNone;
                }
                if (networkSettings)
                {
                    foreach (int button in new[] { AreaFirewallEnable, AreaFirewallDisable, AreaFirewallCheck })
                        if (Contains(SettingsControlRect(m, button), x, y)) return IsEnabled(button) ? button : AreaNone;
                    return AreaNone;
                }
                if (deviceSettings)
                {
                    foreach (int button in new[] { AreaDeviceBluetooth, AreaDeviceWifi, AreaPreviousPage, AreaNextPage })
                        if (Contains(SettingsControlRect(m, button), x, y)) return IsEnabled(button) ? button : AreaNone;
                    if (Contains(m.RefreshRect, x, y)) return IsEnabled(AreaRefresh) ? AreaRefresh : AreaNone;
                    if (x >= m.Left && x < m.Left + m.ContentWidth && y >= m.DeviceListTop && y < m.DeviceListTop + m.VisibleRows * S(DeviceRowHeight))
                    {
                        int row = (y - m.DeviceListTop) / S(DeviceRowHeight);
                        return IsEnabled(AreaDeviceBase + row) ? AreaDeviceBase + row : AreaNone;
                    }
                    return AreaNone;
                }
                if (Contains(m.OutputRect, x, y)) return IsEnabled(AreaOutput) ? AreaOutput : AreaNone;
                if (Contains(PreferenceRect(m, AreaExclusive), x, y)) return IsEnabled(AreaExclusive) ? AreaExclusive : AreaNone;
                if (Contains(VolumeValueRect(m), x, y)) return IsEnabled(AreaVolumeValue) ? AreaVolumeValue : AreaNone;
                if (Contains(VolumeRect(m, false), x, y)) return IsEnabled(AreaVolumeDown) ? AreaVolumeDown : AreaNone;
                if (Contains(VolumeRect(m, true), x, y)) return IsEnabled(AreaVolumeUp) ? AreaVolumeUp : AreaNone;
                if (Contains(PreferenceRect(m, AreaAutoStart), x, y)) return AreaAutoStart;
                if (Contains(PreferenceRect(m, AreaCloseToTray), x, y)) return AreaCloseToTray;
                if (Contains(PreferenceRect(m, AreaAutoConnect), x, y)) return AreaAutoConnect;
                foreach (int area in ThemeAreas)
                    if (Contains(ThemeOptionRect(m, area), x, y)) return area;
                if (Contains(m.Buffer80Rect, x, y)) return AreaBuffer80;
                if (Contains(m.Buffer120Rect, x, y)) return AreaBuffer120;
                if (Contains(m.Buffer160Rect, x, y)) return AreaBuffer160;
                return AreaNone;
            }
            if (wifiMode)
            {
                if(Contains(m.ConnectRect,x,y)) return IsEnabled(AreaWifiEnable) ? AreaWifiEnable : AreaNone;
                if(Contains(m.DisconnectRect,x,y)) return IsEnabled(AreaWifiDisconnect) ? AreaWifiDisconnect : AreaNone;
                if (wifi.Current.State == SinkState.Faulted && Contains(RetryRect(m), x, y)) return AreaRetryWifi;
                if (Contains(m.DetailsRect, x, y)) return AreaDetails;
                if (Contains(m.CopyAddressRect, x, y)) return AreaCopyAddress;
                return AreaNone;
            }
            if (Contains(m.ConnectRect, x, y)) return IsEnabled(AreaConnect) ? AreaConnect : AreaNone;
            if (Contains(m.DisconnectRect, x, y)) return IsEnabled(AreaDisconnect) ? AreaDisconnect : AreaNone;
            return AreaNone;
        }


        private bool IsEnabled(int area)
        {
            if(area>=AreaLogDays && area<=AreaLogClear) return !logBusy;
            if(area>=AreaHotkeyBind && area<AreaHotkeyBind+4) return true;
            if(area>=AreaHotkeyClear && area<AreaHotkeyClear+4) return !(hotkeys==null ? pageSettings.Hotkeys[area-AreaHotkeyClear] : hotkeys[area-AreaHotkeyClear]).Empty;
            if (modeSwitching) return false;
            if (area == AreaSettingsBack) return settingsMode;
            if (area == AreaModeBluetooth || area == AreaModeWifi || area == AreaSettings) return !settingsMode;
            if(area==AreaWifiEnable) return wifiMode && !wifi.Enabled;
            if(area==AreaWifiDisconnect) return wifiMode && wifi.Enabled;
            if(area==AreaCopyAddress) return !string.IsNullOrEmpty(wifi.Current.LocalAddress);
            if (area == AreaExclusive || area == AreaVolumeValue) return wifiMode;
            if (area == AreaVolumeDown) return wifiMode && pageSettings.WifiVolume > 0;
            if (area == AreaVolumeUp) return wifiMode && pageSettings.WifiVolume < 100;
            if (area == AreaOutput) return settingsMode && !deviceSettings && !networkSettings && !hotkeySettings && !aboutSettings && !logSettings && cachedOutputs.Count > 0;
            if (area == AreaFirewallEnable || area == AreaFirewallCheck) return firewallTask == null;
            if (area == AreaFirewallDisable) return firewallTask == null && firewallStatus.Available && firewallStatus.RuleExists;
            if (area == AreaPreviousPage) return connectionDevices.Count > MaxDeviceRows && devicePage > 0;
            if (area == AreaNextPage) return connectionDevices.Count > MaxDeviceRows && (devicePage + 1) * MaxDeviceRows < connectionDevices.Count;
            if (area == AreaRefresh) return !browsingWifiDevices || (wifiMode && wifi.Enabled);
            if (area >= AreaDeviceBase && area < AreaDropdownBase)
                return browsingWifiDevices == wifiMode &&
                    (browsingWifiDevices ? wifi.Enabled : controller.Current.State != SinkState.Busy);
            if (area == AreaConnect) return controller.Devices.Count > 0 && controller.Current.State != SinkState.Busy;
            if (area == AreaDisconnect) return controller.Current.State != SinkState.Idle && controller.Current.State != SinkState.Faulted;
            return true;
        }

        // ===== 输入 =====

        private void OnMouseMove(int x, int y)
        {
            if (!mouseInside)
            {
                mouseInside = true;
                Win32.TRACKMOUSEEVENT tme = new Win32.TRACKMOUSEEVENT();
                tme.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.TRACKMOUSEEVENT));
                tme.dwFlags = Win32.TME_LEAVE;
                tme.hwndTrack = hwnd;
                Win32.TrackMouseEvent(ref tme);
            }

            int area = HitTest(x, y);
            int help = HelpHitTest(x, y, area);
            if (help != helpArea)
            {
                HideHelp(); helpArea = help; helpX = x; helpY = y;
                if (!string.IsNullOrEmpty(HelpText(help))) Win32.SetTimer(hwnd, new IntPtr(IdHelpTimer), 550, IntPtr.Zero);
            }
            if (area != hoverArea)
            {
                hoverArea = area;
                RequestRepaint(true);
                // WM_PAINT normally waits behind queued mouse input. Present this state now,
                // while avoiding redraws for movement inside the same hit region.
                Win32.UpdateWindow(hwnd);
            }
        }

        private void OnMouseLeave()
        {
            HideHelp();
            mouseInside = false;
            if (hoverArea != AreaNone)
            {
                hoverArea = AreaNone;
                RequestRepaint(true);
                Win32.UpdateWindow(hwnd);
            }
        }

        private void OnMouseDown(int x, int y)
        {
            volumeInput?.CommitOnBlur();
            HideHelp();
            pressedArea = HitTest(x, y);
            focusArea = pressedArea;
            keyboardFocus = false;
            RequestRepaint(true);
        }

        private void OnMouseUp(int x, int y)
        {
            int releaseArea = HitTest(x, y);
            int area = pressedArea;
            pressedArea = AreaNone;

            if (area != AreaNone && area == releaseArea)
            {
                Activate(area);
            }
            else if (dropdownOpen)
            {
                dropdownOpen = false;
            }

            RequestRepaint(true);
        }

        private void Activate(int area)
        {
            HideHelp();
            if(area>=AreaHotkeyBind && area<AreaHotkeyBind+4) { BeginHotkeyRecording(area-AreaHotkeyBind); RequestRepaint(true); return; }
            if(area>=AreaHotkeyClear && area<AreaHotkeyClear+4) { SaveHotkey(area-AreaHotkeyClear,default); return; }
            StopHotkeyRecording();
            if (area >= AreaDropdownBase && area < AreaDropdownLimit)
            {
                int index = area - AreaDropdownBase;
                if (index >= 0 && index < cachedOutputs.Count)
                {
                    EndpointInfo pick = cachedOutputs[index];
                    bool changed=false;
                    RunConnectionOperation(()=>{
                        changed=controller.SetPreferredRender(pick.Id,pick.Name,false);
                        return changed ? "播放设备已更新" : "播放设备切换失败，请重试";
                    },()=>{
                        if(!changed) return;
                        var preferences=pageSettings.Copy(); preferences.PreferredRenderId=pick.Id;
                        if(wifiMode && preferences.WifiExclusive) preferences.WifiVolume=0;
                        QueueSave(preferences); ApplyOutputPreferences(); cachedPreferredName=pick.Name;
                    });
                    lastOutputRefresh = DateTime.MinValue;
                    UiLog("Ui.OutputPicked", ("endpoint", pick.Name));
                }
                dropdownOpen = false;
                focusArea = AreaOutput;
                return;
            }

            switch (area)
            {
                case AreaThemeSystem:
                case AreaThemeLight:
                case AreaThemeDark:
                    SaveTheme(area);
                    return;
                case AreaSettingsGeneral:
                case AreaSettingsDevices:
                case AreaSettingsNetwork:
                case AreaSettingsHotkeys:
                case AreaSettingsLogs:
                case AreaSettingsAbout:
                    logSettings = area == AreaSettingsLogs;
                    aboutSettings = area == AreaSettingsAbout;
                    hotkeySettings = area == AreaSettingsHotkeys;
                    deviceSettings = area == AreaSettingsDevices;
                    networkSettings = area == AreaSettingsNetwork;
                    if (networkSettings) BeginFirewallOperation(AreaFirewallCheck);
                    dropdownOpen = false;
                    RefreshConnectionDevices();
                    return;
                case AreaProjectPage:
                case AreaLicensePage:
                    OpenAboutLink(area==AreaLicensePage);
                    return;
                case AreaFirewallEnable:
                case AreaFirewallDisable:
                case AreaFirewallCheck:
                    BeginFirewallOperation(area);
                    return;
                case AreaDeviceBluetooth:
                case AreaDeviceWifi:
                    if (browsingWifiDevices == (area == AreaDeviceWifi)) return;
                    browsingWifiDevices = area == AreaDeviceWifi;
                    devicePage = 0;
                    RefreshConnectionDevices();
                    return;
                case AreaPreviousPage:
                case AreaNextPage:
                    devicePage += area == AreaNextPage ? 1 : -1;
                    RefreshConnectionDevices();
                    return;
                case AreaModeBluetooth:
                    SetMode(false);
                    return;

                case AreaModeWifi:
                    SetMode(true);
                    return;

                case AreaOutput:
                    dropdownOpen = !dropdownOpen;
                    RefreshOutputCache(true);
                    if (keyboardFocus && dropdownOpen && cachedOutputs.Count > 0) focusArea = AreaDropdownBase;
                    return;

                case AreaSettings:
                    if (!settingsMode)
                    {
                        browsingWifiDevices = wifiMode;
                        devicePage = 0;
                    }
                    settingsMode = true;
                    pageSettings = pageSettings.Copy();
                    _ = Task.Run(()=>AutoStart.IsEnabled()).ContinueWith(task=>{ if(task.Status==TaskStatus.RanToCompletion) PostUi(()=>{ autoStartEnabled=task.Result; RequestRepaint(true); }); });
                    if (networkSettings) BeginFirewallOperation(AreaFirewallCheck);
                    RefreshConnectionDevices();
                    focusArea = keyboardFocus ? (aboutSettings ? AreaSettingsAbout : logSettings ? AreaSettingsLogs : hotkeySettings ? AreaSettingsHotkeys : networkSettings ? AreaSettingsNetwork : deviceSettings ? AreaSettingsDevices : AreaSettingsGeneral) : AreaSettings;
                    dropdownOpen = false;
                    UiLog("Ui.SettingsOpened");
                    return;

                case AreaSettingsBack:
                    ReturnFromSettings();
                    return;

                case AreaAutoStart:
                {
                    bool enabled = !autoStartEnabled;
                    bool actual=autoStartEnabled;
                    RunConnectionOperation(()=>{ AutoStart.SetEnabled(enabled); actual=AutoStart.IsEnabled(); return actual==enabled ? "设置已保存" : "设置未能保存，请重试"; },()=>autoStartEnabled=actual);
                    return;
                }

                case AreaCloseToTray:
                {
                    Settings settings = pageSettings.Copy();
                    settings.CloseToTray = !settings.CloseToTray;
                    QueueSave(settings);
                    bool registered = UpdateTray();
                    ShowNotice(settings.CloseToTray ? (registered ? "关闭窗口后将在托盘继续运行" : "托盘暂不可用，关闭时将保留窗口") : "关闭窗口时将彻底退出");
                    UiLog("Ui.CloseToTrayToggled", ("enabled", settings.CloseToTray));
                    break;
                }
                case AreaAutoConnect:
                {
                    Settings settings = pageSettings.Copy();
                    settings.AutoConnect = !settings.AutoConnect;
                    QueueSave(settings);
                    ShowNotice("自动连接偏好已应用");
                    UiLog("Ui.AutoConnectToggled", ("enabled", settings.AutoConnect));
                    return;
                }

                case AreaWifiEnable:
                    EnableWifi(false);
                    return;
                case AreaWifiDisconnect:
                    DisconnectWifi("disconnect");
                    return;
                case AreaRetryWifi:
                    EnableWifi(true);
                    return;
                case AreaDetails:
                    wifiDetails = !wifiDetails;
                    return;
                case AreaCopyAddress:
                    ShowNotice(CopyAddress(wifi.Current.LocalAddress) ? "电脑地址已复制" : "复制失败，请重试");
                    return;
                case AreaBuffer80:
                case AreaBuffer120:
                case AreaBuffer160:
                {
                    int value = area == AreaBuffer80 ? 80 : area == AreaBuffer120 ? 120 : 160;
                    Settings preferences = pageSettings.Copy();
                    preferences.BufferBaseMs = value;
                    preferences.BufferMaxMs = Math.Max(value, preferences.BufferMaxMs);
                    QueueSave(preferences);
                    wifi.SetBufferBase(value);
                    ShowNotice("Wi-Fi 缓冲偏好已应用");
                    return;
                }

                case AreaExclusive:
                {
                    var preferences=pageSettings.Copy();
                    preferences.SetWifiExclusive(!preferences.WifiExclusive);
                    QueueSave(preferences);ApplyOutputPreferences();
                    ShowNotice(preferences.WifiExclusive ? "独占已请求，音量已静音；请手动调高" : "已请求共享；其他软件无声请重选输出设备", preferences.WifiExclusive ? 3 : 6);
                    return;
                }
                case AreaVolumeValue:
                    if(wifiMode) EditWifiVolume();
                    return;
                case AreaVolumeDown:
                case AreaVolumeUp:
                {
                    SetWifiVolume(Math.Clamp(pageSettings.Copy().WifiVolume+(area==AreaVolumeUp?5:-5),0,100));
                    return;
                }

                case AreaLogDays:
                case AreaLogChoose:
                case AreaLogDefault:
                case AreaLogOpen:
                case AreaLogClear:
                    ActivateLogControl(area);
                    return;

                case AreaConnect:
                {
                    string deviceId = string.IsNullOrEmpty(pageSettings.DeviceId) ? null : pageSettings.DeviceId;
                    controller.RequestConnect(deviceId);
                    UiLog("Ui.ConnectClicked", ("device", deviceId ?? "(默认)"));
                    return;
                }

                case AreaDisconnect:
                    controller.RequestDisconnect();
                    UiLog("Ui.DisconnectClicked");
                    return;

                case AreaRefresh:
                    if (!IsEnabled(area)) return;
                    if (browsingWifiDevices) { ShowNotice("正在刷新设备…"); RunConnectionOperation(()=>wifi.RefreshDiscovery() ? "已广播电脑信息，请在手机端选择这台电脑" : "广播失败，请检查 Wi-Fi 连接"); }
                    else { controller.RequestRefreshDevices(); ShowNotice("正在刷新已配对设备"); }
                    RefreshConnectionDevices();
                    lastOutputRefresh = DateTime.MinValue;
                    return;
            }

            if (area >= AreaDeviceBase && area < AreaDropdownBase)
            {
                if (!IsEnabled(area)) return;
                int index = devicePage * MaxDeviceRows + area - AreaDeviceBase;
                if (index >= 0 && index < connectionDevices.Count)
                {
                    DeviceChoice pick = connectionDevices[index];
                    if (wifiMode)
                    {
                        ShowNotice(wifi.SelectPeer(pick.Id) ? "已选择手机，请在手机端开始发送" : "设备不可用，请刷新后重试");
                    }
                    else
                    {
                        Settings preferences = pageSettings.Copy();
                        preferences.DeviceId = pick.Id;
                        QueueSave(preferences);
                        controller.RequestConnect(pick.Id);
                        ShowNotice("正在连接 " + pick.Name);
                    }
                    RefreshConnectionDevices();
                    UiLog("Ui.DevicePicked", ("mode", wifiMode ? "wifi" : "bluetooth"), ("device", pick.Name));
                }
            }
        }

        /// <summary>
        /// 切换蓝牙 / Wi-Fi。两种模式互斥：进入 Wi-Fi 先断开蓝牙，回到蓝牙先停止 Wi-Fi。
        /// </summary>
        private void SetMode(bool useWifi)
        {
            if(modeSwitching) return;
            if(wifiMode==useWifi) { SaveMode(useWifi ? "wifi" : "bluetooth"); return; }
            wifiMode=useWifi; SaveMode(useWifi ? "wifi" : "bluetooth"); dropdownOpen=false;
            var preferences=pageSettings.Copy();
            RunConnectionOperation(()=>{
                wifi.Stop("mode switch"); controller.RequestDisconnect();
                if(!preferences.AutoConnect) return null;
                if(useWifi) return wifi.Start() ? "Wi-Fi 已启用，等待手机响应" : "Wi-Fi 无法启用，请检查端口占用";
                controller.RequestConnect(string.IsNullOrEmpty(preferences.DeviceId) ? null : preferences.DeviceId);
                return "正在连接蓝牙手机";
            });
        }

        private void SaveMode(string mode)
        {
            try
            {
                Settings settings = pageSettings.Copy();
                if (string.Equals(settings.Mode, mode, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                settings.Mode = mode;
                QueueSave(settings);
            }
            catch
            {
            }
        }

        private void OnKeyDown(int vk)
        {
            if(CaptureHotkey(vk,CurrentModifiers())) return;
            if (modeSwitching) return;
            if (vk == 0x70 && keyboardFocus && !string.IsNullOrEmpty(HelpText(focusArea)))
            {
                HideHelp(); helpArea = focusArea;
                var m = ComputeMetrics(); helpX = m.Left; helpY = S(98);
                helpVisible = true; RequestRepaint(true); return;
            }
            if (vk != 0x1B) HideHelp();
            if (vk == 0x1B)
            {
                if (helpVisible) { HideHelp(); return; }
                if (dropdownOpen) { dropdownOpen = false; focusArea = AreaOutput; }
                else if (settingsMode) ReturnFromSettings();
                else if (wifiDetails) { wifiDetails = false; focusArea = AreaDetails; }
                RequestRepaint(true); return;
            }
            if (dropdownOpen && (vk == 0x26 || vk == 0x28))
            {
                int index = focusArea - AreaDropdownBase;
                int direction = vk == 0x26 ? -1 : 1;
                if (index < 0 || index >= dropdownRows) index = direction > 0 ? -1 : 0;
                focusArea = AreaDropdownBase + (index + direction + dropdownRows) % dropdownRows;
                keyboardFocus = true; RequestRepaint(true); return;
            }
            if (vk == 0x09)
            {
                List<int> areas = settingsMode
                    ? new List<int> { AreaSettingsBack }
                    : new List<int> { AreaModeBluetooth, AreaModeWifi, AreaSettings };
                if (dropdownOpen)
                {
                    areas.Clear();
                    for (int i = 0; i < dropdownRows; i++) areas.Add(AreaDropdownBase + i);
                }
                else if (settingsMode)
                {
                    areas.AddRange(new[] { AreaSettingsGeneral, AreaSettingsDevices, AreaSettingsNetwork, AreaSettingsHotkeys, AreaSettingsLogs, AreaSettingsAbout });
                    if (logSettings) areas.AddRange(LogActions);
                    else if(aboutSettings) areas.AddRange(new[]{AreaProjectPage,AreaLicensePage});
                    else if(hotkeySettings) { for(int i=0;i<4;i++) { areas.Add(AreaHotkeyBind+i); areas.Add(AreaHotkeyClear+i); } }
                    else if (networkSettings) areas.AddRange(new[] { AreaFirewallEnable, AreaFirewallDisable, AreaFirewallCheck });
                    else if (deviceSettings)
                    {
                        areas.AddRange(new[] { AreaDeviceBluetooth, AreaDeviceWifi });
                        for (int i = 0; i < ComputeMetrics().VisibleRows; i++) areas.Add(AreaDeviceBase + i);
                        areas.AddRange(new[] { AreaRefresh, AreaPreviousPage, AreaNextPage });
                    }
                    else
                    {
                        areas.AddRange(new[] { AreaOutput, AreaExclusive, AreaVolumeValue, AreaVolumeDown, AreaVolumeUp, AreaAutoStart, AreaCloseToTray, AreaAutoConnect, AreaBuffer80, AreaBuffer120, AreaBuffer160 });
                        areas.AddRange(ThemeAreas);
                    }
                }
                else if (wifiMode)
                {
                    if (wifi.Current.State == SinkState.Faulted) areas.Add(AreaRetryWifi);
                    areas.AddRange(new[] { AreaWifiEnable, AreaWifiDisconnect, AreaCopyAddress, AreaDetails });
                }
                else
                {
                    areas.AddRange(new[] { AreaConnect, AreaDisconnect });
                }
                areas.RemoveAll(a => !IsEnabled(a));
                int direction = Win32.GetKeyState(0x10) < 0 ? -1 : 1;
                int current = areas.IndexOf(focusArea);
                focusArea = areas[current < 0 ? (direction > 0 ? 0 : areas.Count - 1) : (current + direction + areas.Count) % areas.Count];
                keyboardFocus = true; RequestRepaint(true); return;
            }
            if ((vk == 0x0D || vk == 0x20) && keyboardFocus && IsEnabled(focusArea))
            {
                Activate(focusArea); RequestRepaint(true);
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (tray != null && tray.TaskbarCreated != 0 && msg == tray.TaskbarCreated)
            {
                tray.ShellRestarted();
                UpdateTray();
                return IntPtr.Zero;
            }
            // 仅鼠标消息的 lParam 是坐标；其他消息可能是 64 位指针，直接 ToInt32 会溢出崩溃
            int x = 0;
            int y = 0;
            if (msg == Win32.WM_MOUSEMOVE || msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_LBUTTONUP)
            {
                long lp = lParam.ToInt64();
                x = (short)(lp & 0xFFFF);
                y = (short)((lp >> 16) & 0xFFFF);
            }

            switch (msg)
            {
                case UiWorkMessage:
                    while(uiWork.TryDequeue(out var work)) work();
                    return IntPtr.Zero;
                case OutputRefreshMessage:
                    RefreshOutputCache(false);
                    return IntPtr.Zero;
                case 0x001A: // WM_SETTINGCHANGE
                    if (pageSettings.Theme == AppTheme.System && hwnd != IntPtr.Zero) ApplyWindowTheme();
                    break;
                case 0x0133: // WM_CTLCOLOREDIT
                    Win32.SetTextColor(wParam, TextColor);
                    Win32.SetBkColor(wParam, SurfaceColor);
                    return brushCard;
                case HotkeyCapture.Message:
                    CaptureHotkey(wParam.ToInt32(),lParam.ToInt32());
                    return IntPtr.Zero;
                case 0x0312: // WM_HOTKEY, also received while hidden or another app has focus.
                    int modifiers=(int)(lParam.ToInt64() & 0xFFFF), key=(int)((lParam.ToInt64() >> 16) & 0xFFFF);
                    if(recordingHotkey>=0) CaptureHotkey(key,modifiers & 15);
                    else if(hotkeys != null && hotkeys.Matches(wParam.ToInt32(),modifiers,key)) ExecuteHotkey(wParam.ToInt32()-GlobalHotkeys.FirstId);
                    return IntPtr.Zero;
                case 0x0104: // WM_SYSKEYDOWN: Alt combinations during recording.
                    if(recordingHotkey>=0) { CaptureHotkey(wParam.ToInt32(),CurrentModifiers()); return IntPtr.Zero; }
                    break;
                case 0x0106: // No menu accelerators on the shortcut settings page.
                    if(hotkeySettings && settingsMode) return IntPtr.Zero;
                    break;
                case 0x8051:
                    volumeInput?.Cancel();
                    var volumeSettings = pageSettings.Copy();
                    volumeSettings.WifiVolume = wifi.Current.VolumePercent;
                    QueueSave(volumeSettings);
                    ApplyOutputPreferences(); RequestRepaint(true);
                    return IntPtr.Zero;
                case Win32.WM_SETCURSOR:
                    if ((lParam.ToInt64() & 0xFFFF) == 1)
                    {
                        Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, hoverArea == AreaNone ? Win32.IDC_ARROW : Win32.IDC_HAND));
                        return new IntPtr(1);
                    }
                    break;
                case 0x0024: // WM_GETMINMAXINFO: native minimum track size, in current DPI.
                    Win32.MINMAXINFO limits = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
                    limits.ptMinTrackSize.X = S(480); limits.ptMinTrackSize.Y = S(500);
                    System.Runtime.InteropServices.Marshal.StructureToPtr(limits, lParam, false);
                    return IntPtr.Zero;
                case Win32.WM_PAINT:
                    Paint(hWnd);
                    return IntPtr.Zero;

                case Win32.WM_ERASEBKGND:
                    return new IntPtr(1);

                case Win32.WM_SIZE:
                    volumeInput?.Cancel();
                    HideHelp();
                    RequestRepaint(true);
                    return IntPtr.Zero;

                case 0x02E0: // WM_DPICHANGED
                    volumeInput?.Cancel();
                    HideHelp();
                    uint dpi = (uint)(wParam.ToInt32() & 0xFFFF);
                    if (dpi >= 72 && dpi <= 480)
                    {
                        scale = dpi / 96.0;
                        RecreateFonts();
                    }
                    RequestRepaint(true);
                    return IntPtr.Zero;

                case Win32.WM_TIMER:
                    if (wParam.ToInt64() == IdHelpTimer)
                    {
                        Win32.KillTimer(hWnd, new IntPtr(IdHelpTimer));
                        if (mouseInside && !dropdownOpen && !string.IsNullOrEmpty(HelpText(helpArea)))
                        { helpVisible = true; RequestRepaint(true); }
                        return IntPtr.Zero;
                    }
                    OnTimer();
                    return IntPtr.Zero;

                case 0x0006: // WM_ACTIVATE
                    if ((wParam.ToInt64() & 0xffff) == 0) { HideHelp(); if(recordingHotkey>=0) { StopHotkeyRecording(); RequestRepaint(true); } }
                    break;

                case Win32.WM_MOUSEMOVE:
                    OnMouseMove(x, y);
                    return IntPtr.Zero;

                case Win32.WM_MOUSELEAVE:
                    OnMouseLeave();
                    return IntPtr.Zero;

                case Win32.WM_LBUTTONDOWN:
                    OnMouseDown(x, y);
                    return IntPtr.Zero;

                case Win32.WM_LBUTTONUP:
                    OnMouseUp(x, y);
                    return IntPtr.Zero;

                case Win32.WM_KEYDOWN:
                    OnKeyDown(wParam.ToInt32());
                    return IntPtr.Zero;

                case TrayIcon.RefreshMessage:
                    UpdateTray();
                    RequestRepaint(false);
                    return IntPtr.Zero;

                case TrayIcon.RestoreMessage:
                    ShowWindow();
                    return IntPtr.Zero;

                case TrayIcon.CallbackMessage:
                    int trayEvent = unchecked((int)lParam.ToInt64()) & 0xffff;
                    if (trayEvent == 0x007B || trayEvent == Win32.WM_RBUTTONUP)
                    {
                        int command = tray.ShowMenu();
                        if (command == 1) ShowWindow();
                        else if (command == 2) Win32.DestroyWindow(hWnd);
                    }
                    else if (trayEvent == 0x0400 || trayEvent == 0x0401 || trayEvent == Win32.WM_LBUTTONUP)
                        ShowWindow(); // NIN_SELECT, NIN_KEYSELECT; legacy fallback
                    return IntPtr.Zero;

                case 0x0011: // WM_QUERYENDSESSION: permit logout/shutdown
                    return new IntPtr(1);
                case 0x0016: // WM_ENDSESSION
                    if (wParam != IntPtr.Zero) Win32.DestroyWindow(hWnd);
                    return IntPtr.Zero;

                case Win32.WM_CLOSE:
                    if (pageSettings.CloseToTray)
                    {
                        if (UpdateTray())
                        {
                            dropdownOpen = false;
                            HideHelp();
                            hoverArea = pressedArea = AreaNone;
                            StopHotkeyRecording();
                            volumeInput?.Cancel();
                            hiddenToTray = true;
                            Win32.ShowWindow(hWnd, Win32.SW_HIDE);
                        }
                        else ShowNotice("托盘暂不可用，窗口已保留，请重试或关闭常驻选项");
                        return IntPtr.Zero;
                    }
                    break;

                case Win32.WM_DESTROY:
                    hotkeyCapture?.Dispose();
                    hotkeys?.Dispose();
                    volumeInput?.Cancel();
                    Win32.KillTimer(hWnd, new IntPtr(IdHelpTimer));
                    tray?.Remove();
                    Win32.KillTimer(hWnd, new IntPtr(IdTimer));
                    Win32.PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        // ===== 绘制 =====

        private void Paint(IntPtr hWnd)
        {
            Win32.PAINTSTRUCT ps;
            IntPtr hdcScreen = Win32.BeginPaint(hWnd, out ps);
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hold = IntPtr.Zero;

            try
            {
                Win32.RECT client;
                Win32.GetClientRect(hWnd, out client);
                int width = Math.Max(client.Right - client.Left, 1);
                int height = Math.Max(client.Bottom - client.Top, 1);

                hdcMem = Win32.CreateCompatibleDC(hdcScreen);
                hBitmap = Win32.CreateCompatibleBitmap(hdcScreen, width, height);
                hold = Win32.SelectObject(hdcMem, hBitmap);

                Win32.SetBkMode(hdcMem, Win32.TRANSPARENT);
                Win32.RECT full = Rect(0, 0, width, height);
                Win32.FillRect(hdcMem, ref full, brushBack);

                Metrics m = ComputeMetrics();

                if (settingsMode)
                {
                    DrawSettings(hdcMem, m);
                }
                else if (wifiMode)
                {
                    WifiSnapshot wifiSnapshot = wifi.Current;
                    DrawHeader(hdcMem, m, wifiSnapshot);
                    DrawWifiPage(hdcMem, m, wifiSnapshot);
                }
                else
                {
                    SinkSnapshot snapshot = controller.Current;
                    DrawHeader(hdcMem, m, snapshot);
                    DrawBluetoothPage(hdcMem, m, snapshot);
                }

                DrawModeTabs(hdcMem, m);
                if (settingsMode && dropdownOpen) DrawDropdown(hdcMem);
                DrawKeyboardFocus(hdcMem, m);
                if (DateTime.Now < noticeUntil)
                    Text(hdcMem, notice, fontBody, AccentTextColor, Rect(m.Left, m.Height - S(24), m.Left + m.ContentWidth, m.Height - S(4)));
                DrawHelp(hdcMem, m);
                Win32.BitBlt(hdcScreen, 0, 0, width, height, hdcMem, 0, 0, Win32.SRCCOPY);
            }
            finally
            {
                if (hold != IntPtr.Zero)
                {
                    Win32.SelectObject(hdcMem, hold);
                }
                if (hBitmap != IntPtr.Zero)
                {
                    Win32.DeleteObject(hBitmap);
                }
                if (hdcMem != IntPtr.Zero)
                {
                    Win32.DeleteDC(hdcMem);
                }
                Win32.EndPaint(hWnd, ref ps);
            }
        }

        private void DrawHeader(IntPtr hdc, Metrics m, SinkSnapshot snapshot)
        {
            string title = DescribeState(snapshot.State);
            string detail = string.IsNullOrEmpty(snapshot.DeviceName) ? "前往设置选择手机，再点击连接。" : snapshot.DeviceName;
            if (snapshot.State == SinkState.Idle) detail = "点击连接；更换手机请前往设置。";
            else if (snapshot.State == SinkState.Armed) detail = "连接已就绪，请在手机上播放音频。";
            else if (snapshot.State == SinkState.LinkReady) detail = "已建立音频链路，正在等待声音。";
            else if (snapshot.State == SinkState.Faulted) detail = snapshot.Detail ?? "请重试连接。";
            ConnectionPresentation view = new ConnectionPresentation
            {
                Title = title, Description = detail,
                Tone = snapshot.State == SinkState.Flowing ? ConnectionTone.Active :
                    snapshot.State == SinkState.Faulted ? ConnectionTone.Error :
                    snapshot.State == SinkState.Busy || snapshot.State == SinkState.Backoff ? ConnectionTone.Warning : ConnectionTone.Ready,
                PhoneReady = snapshot.State == SinkState.Armed || snapshot.State == SinkState.LinkReady || snapshot.State == SinkState.Flowing,
                StreamReady = snapshot.State == SinkState.LinkReady || snapshot.State == SinkState.Flowing,
                OutputReady = snapshot.State == SinkState.Flowing
            };
            DrawStatusCard(hdc, m, view);
        }

        private void DrawHeader(IntPtr hdc, Metrics m, WifiSnapshot snapshot)
        {
            DrawStatusCard(hdc, m, ConnectionPresentation.Wifi(snapshot));
        }

        private void DrawStatusCard(IntPtr hdc, Metrics m, ConnectionPresentation view)
        {
            if (modeSwitching) { view.Title = "正在切换模式"; view.Description = "正在准备连接，请稍候。"; view.Tone = ConnectionTone.Ready; }
            int right = m.Left + m.ContentWidth;
            Dot(hdc, m.Left + S(4), S(78), S(8), ToneColor(view.Tone));
            bool retry = wifiMode && !settingsMode && wifi.Current.State == SinkState.Faulted;
            int titleRight = retry ? RetryRect(m).Left : m.SettingsRect.Left;
            Text(hdc, view.Title, fontTitle, TextColor, Rect(m.Left + S(20), S(66), titleRight - S(8), S(94)));
            if (retry) DrawButton(hdc, RetryRect(m), "重试", AreaRetryWifi, false);
            Text(hdc, view.Description, fontBody, SecondaryColor, Rect(m.Left + S(4), S(98), right - S(4), S(120)));
            int col = (m.ContentWidth - S(8)) / 3;
            Stage(hdc, m.Left + S(4), S(124), col, view.PhoneReady, "手机已连接", "等待手机");
            Stage(hdc, m.Left + S(4) + col, S(124), col, view.StreamReady, "音频已接收", "等待音频");
            Stage(hdc, m.Left + S(4) + col * 2, S(124), col, view.OutputReady, "输出已就绪", "等待播放");
        }

        private void Stage(IntPtr hdc, int left, int top, int width, bool ready, string active, string waiting)
        {
            Dot(hdc, left, top + S(5), S(5), ready ? AccentTextColor : DisabledColor);
            Text(hdc, ready ? active : waiting, fontSmall, ready ? SecondaryColor : SecondaryColor,
                Rect(left + S(12), top, left + width - S(4), top + S(18)));
        }

        private void ReturnFromSettings()
        {
            dropdownOpen = false;
            settingsMode = false;
            focusArea = AreaSettings;
        }

        private void DrawModeTabs(IntPtr hdc, Metrics m)
        {
            if (settingsMode)
            {
                DrawInlineTab(hdc, m.SettingsBackRect, "‹ 返回", false, AreaSettingsBack);
            }
            else
            {
                DrawInlineTab(hdc, m.ModeBluetoothRect, "蓝牙", !wifiMode, AreaModeBluetooth, 38);
                DrawInlineTab(hdc, m.ModeWifiRect, "Wi-Fi", wifiMode, AreaModeWifi, 38);
                var gear = m.SettingsRect;
                if (hoverArea == AreaSettings || pressedArea == AreaSettings)
                    RoundFill(hdc, gear, S(8), pressedArea == AreaSettings ? brushBtnPress : brushBtnHover);
                Text(hdc, "\uE713", fontGear, TextColor, gear, true);
            }
            Win32.RECT line = Rect(m.NavLeft, S(52), m.NavLeft + m.NavWidth, S(53));
            Win32.FillRect(hdc, ref line, brushBorder);
        }

        private void DrawWifiPage(IntPtr hdc, Metrics m, WifiSnapshot snapshot)
        {
            int left = m.Left, right = left + m.ContentWidth, half = m.ContentWidth / 2;
            Win32.RECT devices = Rect(left, S(154), right, S(230));
            RoundFill(hdc, devices, S(8), brushCard); RoundBorder(hdc, devices, S(8), brushBorder);
            Text(hdc, "发送手机", fontSmall, SecondaryColor, Rect(left + S(12), S(162), left + half - S(8), S(180)));
            string phone = string.IsNullOrEmpty(snapshot.DeviceName) ? (string.IsNullOrEmpty(snapshot.Remote) ? "尚未连接" : "Android 手机") : snapshot.DeviceName;
            Text(hdc, phone, fontHeading, TextColor, Rect(left + S(12), S(184), left + half - S(8), S(208)));
            string address = snapshot.Remote;
            int colon = string.IsNullOrEmpty(address) ? -1 : address.LastIndexOf(':');
            if (colon > 0) address = address.Substring(0, colon);
            Text(hdc, address ?? "等待手机发送端", fontSmall, SecondaryColor, Rect(left + S(12), S(208), left + half - S(8), S(226)));
            Text(hdc, "播放到", fontSmall, SecondaryColor, Rect(left + half + S(8), S(162), right - S(12), S(180)));
            string destination = snapshot.OutputName ?? cachedPreferredName;
            Win32.RECT destinationRect = Rect(left + half + S(8), S(184), right - S(12), S(226));
            Win32.SelectObject(hdc, fontBody); Win32.SetTextColor(hdc, TextColor);
            Win32.DrawText(hdc, destination, -1, ref destinationRect, Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_END_ELLIPSIS);
            Win32.RECT stats = Rect(left, S(242), right, S(316));
            RoundFill(hdc, stats, S(8), brushCard); RoundBorder(hdc, stats, S(8), brushBorder);
            int col = m.ContentWidth / 3;
            MetricTile(hdc, Rect(left, S(242), left + col, S(316)), "网络延迟", FormatMs(snapshot.RttMs), "往返时间", false);
            MetricTile(hdc, Rect(left + col, S(242), left + col * 2, S(316)), "音频缓冲", snapshot.BufferMs + " ms", "预缓冲 " + snapshot.TargetMs + " ms", false);
            MetricTile(hdc, Rect(left + col * 2, S(242), right, S(316)), "丢包率", FormatPercent(snapshot.LossPercent), "最近 10 秒", snapshot.LossPercent > 0.5);
            for (int i = 1; i < 3; i++)
            {
                Win32.RECT divider = Rect(left + col * i, S(254), left + col * i + 1, S(304));
                Win32.FillRect(hdc, ref divider, brushBorder);
            }
            DrawButton(hdc,m.ConnectRect,"启用",AreaWifiEnable,!snapshot.Enabled);
            DrawButton(hdc,m.DisconnectRect,"断开",AreaWifiDisconnect,false);
            Text(hdc, "电脑地址  " + (snapshot.LocalAddress ?? "—"), fontSmall, SecondaryColor, Rect(left, S(370), m.CopyAddressRect.Left - S(6), S(398)));
            DrawButton(hdc, m.CopyAddressRect, "复制", AreaCopyAddress, false);
            DrawButton(hdc, m.DetailsRect, wifiDetails ? "收起详情" : "连接详情", AreaDetails, false);
            if (wifiDetails)
            {
                Win32.RECT panel = Rect(left, S(402), right, S(432));
                RoundFill(hdc, panel, S(8), brushCard); RoundBorder(hdc, panel, S(8), brushBorder);
                Text(hdc, "手机端点 " + (snapshot.Remote ?? "未连接") + "  ·  端口 " + snapshot.Port,
                    fontSmall, SecondaryColor, Rect(left + S(8), panel.Top + S(1), right - S(8), panel.Top + S(15)));
                Text(hdc, "收到 " + snapshot.Received + "  ·  缺失 " + snapshot.Missing + "  ·  迟到 " + snapshot.Late + "  ·  欠载 " + snapshot.Underruns + " 次",
                    fontSmall, SecondaryColor, Rect(left + S(8), panel.Top + S(15), right - S(8), panel.Top + S(29)));
            }
        }

        private void MetricTile(IntPtr hdc, Win32.RECT rect, string label, string value, string hint, bool warning)
        {
            Text(hdc, label, fontSmall, SecondaryColor, Rect(rect.Left + S(12), rect.Top + S(6), rect.Right - S(8), rect.Top + S(24)));
            Text(hdc, value, fontValue, warning ? WarningColor : TextColor, Rect(rect.Left + S(12), rect.Top + S(24), rect.Right - S(8), rect.Top + S(56)));
            Text(hdc, hint, fontSmall, SecondaryColor, Rect(rect.Left + S(12), rect.Top + S(56), rect.Right - S(8), rect.Bottom - S(2)));
        }

        private static string FormatMs(double value)
        {
            if (double.IsNaN(value) || value < 0)
            {
                return "—";
            }
            return value.ToString("0") + " ms";
        }

        private static string FormatPercent(double value)
        {
            if (double.IsNaN(value))
            {
                return "—";
            }
            return value.ToString("0.0") + " %";
        }

        private void DrawBluetoothPage(IntPtr hdc, Metrics m, SinkSnapshot snapshot)
        {
            int right = m.Left + m.ContentWidth, half = m.ContentWidth / 2;
            Win32.RECT card = Rect(m.Left, S(154), right, S(230));
            RoundFill(hdc, card, S(8), brushCard); RoundBorder(hdc, card, S(8), brushBorder);
            Text(hdc, "发送手机", fontSmall, SecondaryColor, Rect(m.Left + S(12), S(162), m.Left + half, S(180)));
            Text(hdc, snapshot.DeviceName ?? "尚未选择", fontHeading, TextColor, Rect(m.Left + S(12), S(184), m.Left + half - S(8), S(208)));
            Text(hdc, "播放到", fontSmall, SecondaryColor, Rect(m.Left + half + S(8), S(162), right - S(12), S(180)));
            Text(hdc, cachedPreferredName, fontBody, TextColor, Rect(m.Left + half + S(8), S(184), right - S(12), S(208)));
            DrawButton(hdc, m.ConnectRect, snapshot.State == SinkState.Flowing ? "重新连接" : "连接", AreaConnect, true);
            DrawButton(hdc, m.DisconnectRect, "断开", AreaDisconnect, false);
            Text(hdc, "更换手机或播放设备，请前往设置。", fontSmall, SecondaryColor, Rect(m.Left, S(292), right, S(316)));
        }

        private void DrawDeviceSettings(IntPtr hdc, Metrics m)
        {
            DrawInlineTab(hdc, SettingsControlRect(m, AreaDeviceBluetooth), "蓝牙手机", !browsingWifiDevices, AreaDeviceBluetooth);
            DrawInlineTab(hdc, SettingsControlRect(m, AreaDeviceWifi), "Wi-Fi 手机", browsingWifiDevices, AreaDeviceWifi);
            DrawButton(hdc, m.RefreshRect, "刷新", AreaRefresh, false);
            int right = m.Left + m.ContentWidth;
            string hint = browsingWifiDevices != wifiMode
                ? $"仅查看 · 返回主页切换到{(browsingWifiDevices ? " Wi-Fi" : "蓝牙")}后可选设备"
                : browsingWifiDevices && !wifi.Enabled ? "请先在 Wi-Fi 主页启用接收" : null;
            if (hint != null)
                Text(hdc, hint, fontSmall, SecondaryColor, Rect(m.Left, m.RefreshRect.Bottom, right, m.DeviceListTop));
            for (int rowIndex = 0; rowIndex < m.VisibleRows; rowIndex++)
            {
                DeviceChoice choice = connectionDevices[devicePage * MaxDeviceRows + rowIndex];
                int top = m.DeviceListTop + rowIndex * S(DeviceRowHeight);
                Win32.RECT row = Rect(m.Left, top, right, top + S(DeviceRowHeight) - S(2));
                RoundFill(hdc, row, S(6), choice.Selected ? brushRowSelected : hoverArea == AreaDeviceBase + rowIndex ? brushBtnHover : brushCard);
                Text(hdc, choice.Name, fontBody, TextColor, Rect(row.Left + S(10), row.Top, row.Right - S(78), browsingWifiDevices ? row.Top + S(18) : row.Bottom));
                if (browsingWifiDevices) Text(hdc, choice.Id, fontSmall, SecondaryColor, Rect(row.Left + S(10), row.Top + S(18), row.Right - S(78), row.Bottom));
                Text(hdc, choice.Status, fontSmall, choice.Selected ? AccentTextColor : SecondaryColor, Rect(row.Right - S(74), row.Top, row.Right - S(6), row.Bottom), true);
            }
            if (connectionDevices.Count == 0)
                Text(hdc, browsingWifiDevices ? "暂无在线手机" : "暂无已配对手机", fontBody, SecondaryColor, Rect(m.Left, m.DeviceListTop, right, m.DeviceListTop + S(36)), true);
            if (connectionDevices.Count > MaxDeviceRows)
            {
                Text(hdc, (devicePage + 1) + " / " + ((connectionDevices.Count + MaxDeviceRows - 1) / MaxDeviceRows), fontSmall, SecondaryColor, Rect(m.Left, m.Height - S(68), SettingsControlRect(m, AreaPreviousPage).Left - S(8), m.Height - S(38)));
                DrawButton(hdc, SettingsControlRect(m, AreaPreviousPage), "上一页", AreaPreviousPage, false);
                DrawButton(hdc, SettingsControlRect(m, AreaNextPage), "下一页", AreaNextPage, false);
            }
        }

        private void DrawInlineTab(IntPtr hdc, Win32.RECT rect, string label, bool selected, int area, int underlineWidth = 0)
        {
            if (hoverArea == area) RoundFill(hdc, rect, S(4), brushBtnHover);
            Text(hdc, label, selected ? fontCaption : fontSmall, selected ? AccentTextColor : SecondaryColor, rect, true);
            if (selected)
            {
                int inset = underlineWidth > 0 ? Math.Max(0, (rect.Right - rect.Left - S(underlineWidth)) / 2) : S(12);
                var line = Rect(rect.Left + inset, rect.Bottom - S(2), rect.Right - inset, rect.Bottom);
                Win32.FillRect(hdc, ref line, brushAccent);
            }
        }

        private void DrawOutputSection(IntPtr hdc, Metrics m)
        {
            DrawCaption(hdc, "播放设备", m.Left + S(12), m.OutputLabelTop);

            bool hover = IsEnabled(AreaOutput) && hoverArea == AreaOutput;
            bool pressed = IsEnabled(AreaOutput) && pressedArea == AreaOutput;
            RoundFill(hdc, m.OutputRect, S(8), pressed ? brushBtnPress : (hover ? brushBtnHover : brushCard));
            RoundBorder(hdc, m.OutputRect, S(8), hover ? brushAccent : brushBorder);

            Win32.SelectObject(hdc, fontBody);
            Win32.SetTextColor(hdc, TextColor);
            Win32.RECT textRect = Rect(m.OutputRect.Left + S(12), m.OutputRect.Top,
                m.OutputRect.Right - S(30), m.OutputRect.Bottom);
            Win32.DrawText(hdc, cachedPreferredName, -1, ref textRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, SecondaryColor);
            Win32.RECT arrowRect = Rect(m.OutputRect.Right - S(26), m.OutputRect.Top,
                m.OutputRect.Right - S(10), m.OutputRect.Bottom);
            Win32.DrawText(hdc, dropdownOpen ? "▴" : "▾", -1, ref arrowRect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        }

        private void DrawDropdown(IntPtr hdc)
        {
            ComputeDropdown(ComputeMetrics());
            if (cachedOutputs.Count == 0)
            {
                return;
            }

            RoundFill(hdc, dropdownRect, S(8), brushCard);
            RoundBorder(hdc, dropdownRect, S(8), brushBorder);

            for (int i = 0; i < dropdownRows; i++)
            {
                int top = dropdownRect.Top + S(4) + i * S(DropdownRowHeight);
                Win32.RECT row = Rect(dropdownRect.Left + S(4), top,
                    dropdownRect.Right - S(4), top + S(DropdownRowHeight));

                bool isCurrent = cachedOutputs[i].Name == cachedPreferredName;
                bool isHover = hoverArea == AreaDropdownBase + i;

                if (isHover || isCurrent)
                {
                    RoundFill(hdc, row, S(6), isCurrent ? brushRowSelected : brushRowHover);
                }

                Win32.SelectObject(hdc, fontBody);
                Win32.SetTextColor(hdc, isCurrent ? AccentTextColor : TextColor);
                Win32.RECT textRect = Rect(row.Left + S(10), row.Top, row.Right - S(10), row.Bottom);
                Win32.DrawText(hdc, cachedOutputs[i].Name, -1, ref textRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
            }
        }

        /// <summary>
        /// 设置页。开关类功能用经典划钮（左=关，右=开，开启时槽变主色），
        /// 不再做成“可点进下一级”的列表项。
        /// </summary>
        private void DrawSettings(IntPtr hdc, Metrics m)
        {
            foreach (int area in new[] { AreaSettingsGeneral, AreaSettingsDevices, AreaSettingsNetwork, AreaSettingsHotkeys, AreaSettingsLogs, AreaSettingsAbout })
            {
                bool selected = area == (aboutSettings ? AreaSettingsAbout : logSettings ? AreaSettingsLogs : hotkeySettings ? AreaSettingsHotkeys : networkSettings ? AreaSettingsNetwork : deviceSettings ? AreaSettingsDevices : AreaSettingsGeneral);
                var rect = SettingsControlRect(m, area);
                if (selected || hoverArea == area) RoundFill(hdc, rect, S(6), selected ? brushRowSelected : brushBtnHover);
                if (selected) { var mark = Rect(rect.Left, rect.Top + S(10), rect.Left + S(3), rect.Bottom - S(10)); Win32.FillRect(hdc, ref mark, brushAccent); }
                Text(hdc, area == AreaSettingsGeneral ? "常规" : area == AreaSettingsDevices ? "连接设备" : area == AreaSettingsHotkeys ? "快捷键" : area == AreaSettingsAbout ? "关于" : area == AreaSettingsLogs ? "连接日志" : "网络", selected ? fontHeading : fontBody, selected ? AccentTextColor : SecondaryColor, rect, true);
            }
            var divider = Rect(m.Left - S(9), S(68), m.Left - S(8), m.Height - S(38));
            Win32.FillRect(hdc, ref divider, brushBorder);
            if (logSettings) { DrawLogSettings(hdc, m); return; }
            if(aboutSettings) { DrawAbout(hdc,m); return; }
            if(hotkeySettings) { DrawHotkeys(hdc,m); return; }
            if (networkSettings) { DrawNetworkSettings(hdc, m); return; }
            if (deviceSettings) { DrawDeviceSettings(hdc, m); return; }
            var general = Rect(m.Left, S(68), m.Left + m.ContentWidth, S(238));
            RoundFill(hdc, general, S(8), brushCard); RoundBorder(hdc, general, S(8), brushBorder);
            DrawOutputSection(hdc, m);
            DrawPreference(hdc,m,AreaExclusive,"WASAPI 独占输出",pageSettings.WifiExclusive);
            Text(hdc,"Wi-Fi 音量",fontBody,wifiMode?TextColor:DisabledColor,Rect(m.Left+S(12),S(178),VolumeValueRect(m).Left,S(208)));
            if(hoverArea==AreaVolumeValue && wifiMode) RoundFill(hdc,VolumeValueRect(m),S(6),brushBtnHover);
            Text(hdc,pageSettings.WifiVolume+"%",fontBody,wifiMode?AccentTextColor:DisabledColor,VolumeValueRect(m),true);
            DrawButton(hdc,VolumeRect(m,false),"−",AreaVolumeDown,false);
            DrawButton(hdc,VolumeRect(m,true),"+",AreaVolumeUp,false);
            var audio=wifi?.Current;
            Text(hdc,OutputStatusText(),fontSmall,!string.IsNullOrEmpty(audio?.OutputFallbackReason)?WarningColor:SecondaryColor,Rect(m.Left+S(12),S(212),m.Left+m.ContentWidth-S(28),S(232)));
            DrawHelpMark(hdc,m.Left+m.ContentWidth-S(16),S(222));
            var connection = Rect(m.Left, S(242), m.Left + m.ContentWidth, S(350));
            RoundFill(hdc, connection, S(8), brushCard); RoundBorder(hdc, connection, S(8), brushBorder);
            DrawPreference(hdc, m, AreaAutoStart,"开机启动",autoStartEnabled);
            DrawPreference(hdc, m, AreaCloseToTray,"关闭时缩小到托盘",pageSettings.CloseToTray);
            DrawPreference(hdc, m, AreaAutoConnect, "自动连接", pageSettings.AutoConnect);
            Text(hdc, "Wi-Fi 缓冲", fontSmall, TextColor, Rect(m.Left + S(8), S(358), m.Left + S(82), S(390)));
            DrawHelpMark(hdc, m.Left + S(88), S(374));
            DrawInlineTab(hdc, m.Buffer80Rect, "80 ms", pageSettings.BufferBaseMs == 80, AreaBuffer80);
            DrawInlineTab(hdc, m.Buffer120Rect, "120 ms", pageSettings.BufferBaseMs == 120, AreaBuffer120);
            DrawInlineTab(hdc, m.Buffer160Rect, "160 ms", pageSettings.BufferBaseMs == 160, AreaBuffer160);
            DrawThemeSettings(hdc, m);
        }

        private void DrawNetworkSettings(IntPtr hdc, Metrics m)
        {
            int right = m.Left + m.ContentWidth;
            var card = Rect(m.Left, S(76), right, S(158));
            RoundFill(hdc, card, S(8), brushCard); RoundBorder(hdc, card, S(8), brushBorder);
            Text(hdc, "Wi-Fi 防火墙", fontHeading, TextColor, Rect(m.Left + S(12), S(86), right - S(12), S(114)));
            DrawHelpMark(hdc, m.Left + S(132), S(100));
            string state = firewallTask != null ? firewallChanging ? "等待 Windows 授权与配置…" : "正在检测…"
                : !firewallStatus.Available ? "无法读取防火墙状态" : firewallStatus.RuleReady ? "已开启局域网放行"
                : firewallStatus.RuleExists ? "放行规则需要修复" : "尚未添加放行规则";
            Text(hdc, state, fontSmall, firewallStatus.RuleReady ? SuccessColor : SecondaryColor, Rect(m.Left + S(12), S(118), right - S(12), S(146)));
            DrawButton(hdc, SettingsControlRect(m, AreaFirewallEnable), firewallStatus.RuleReady ? "重新配置" : "尝试开启放行", AreaFirewallEnable, true);
            DrawButton(hdc, SettingsControlRect(m, AreaFirewallDisable), "移除本程序规则", AreaFirewallDisable, false);
            DrawButton(hdc, SettingsControlRect(m, AreaFirewallCheck), "重新检测", AreaFirewallCheck, false);
            string hint = null;
            if (firewallTask == null && !firewallStatus.Available) hint = firewallStatus.Error;
            else if (firewallTask == null && firewallStatus.RuleExists)
            {
                if (firewallStatus.InboundBlocked || firewallStatus.ModifyState != 0) hint = "系统策略限制入站连接，请联系管理员";
                else if (!firewallStatus.PrivateActive) hint = "当前网络不适用此规则";
            }
            if (!string.IsNullOrEmpty(hint)) DrawWrappedText(hdc, hint, fontSmall, WarningColor, Rect(m.Left, S(270), right, S(318)));
            if (!string.IsNullOrEmpty(firewallMessage)) DrawWrappedText(hdc, firewallMessage, fontBody, AccentTextColor, Rect(m.Left, S(328), right, S(390)));
        }

        private void DrawPreference(IntPtr hdc, Metrics m, int area, string label, bool value)
        {
            var row = PreferenceRect(m, area);
            if (hoverArea == area || pressedArea == area) RoundFill(hdc, row, S(6), pressedArea == area ? brushBtnPress : brushBtnHover);
            bool enabled=IsEnabled(area);
            Text(hdc, label, fontBody, enabled ? TextColor : DisabledColor, Rect(row.Left + S(4), row.Top, row.Right - S(62), row.Bottom));
            DrawSwitch(hdc, row.Right - S(8), (row.Top + row.Bottom) / 2, value, hoverArea == area,enabled);
        }

        private void DrawHelpMark(IntPtr hdc, int x, int y)
        {
            Dot(hdc, x - S(7), y - S(7), S(14), HelpBackgroundColor);
            Text(hdc, "?", fontSmall, HelpTextColor, Rect(x - S(7), y - S(9), x + S(7), y + S(8)), true);
        }

        private void DrawWrappedText(IntPtr hdc, string text, IntPtr font, int color, Win32.RECT rect)
        {
            Win32.SelectObject(hdc, font); Win32.SetTextColor(hdc, color);
            Win32.DrawText(hdc, text, -1, ref rect, Win32.DT_LEFT | Win32.DT_WORDBREAK);
        }

        private void HideHelp()
        {
            bool repaint = helpVisible;
            helpVisible = false; helpArea = AreaNone;
            if (hwnd != IntPtr.Zero) Win32.KillTimer(hwnd, new IntPtr(IdHelpTimer));
            if (repaint) RequestRepaint(true);
        }

        private int HelpHitTest(int x, int y, int area)
        {
            if (area == AreaSettings) return area;
            if (!settingsMode || dropdownOpen || modeSwitching) return AreaNone;
            var m = ComputeMetrics();
            if (aboutSettings || logSettings) return area;
            if(hotkeySettings)
            {
                for(int i=0;i<4;i++) if(Contains(Rect(m.Left,S(100+i*76),m.Left+m.ContentWidth,S(170+i*76)),x,y)) return AreaHotkeyBind+i;
                return AreaNone;
            }
            if (networkSettings)
            {
                foreach (int button in new[] { AreaFirewallEnable, AreaFirewallDisable, AreaFirewallCheck })
                    if (Contains(SettingsControlRect(m, button), x, y)) return button;
                if (Contains(Rect(m.Left, S(76), m.Left + m.ContentWidth, S(158)), x, y)) return AreaFirewallHelp;
            }
            else if (!deviceSettings && Contains(Rect(m.Left + S(8), S(354), m.Left + S(98), S(394)), x, y)) return AreaBufferHelp;
            if(!deviceSettings && !networkSettings)
            {
                if(Contains(PreferenceRect(m,AreaExclusive),x,y))return AreaExclusive;
                if(Contains(Rect(m.Left+S(8),S(178),VolumeRect(m,false).Left,S(208)),x,y))return AreaVolumeValue;
                if(Contains(VolumeRect(m,false),x,y))return AreaVolumeDown;
                if(Contains(VolumeRect(m,true),x,y))return AreaVolumeUp;
                if(Contains(Rect(m.Left,S(212),m.Left+m.ContentWidth,S(232)),x,y))return AreaOutputStatus;
            }
            return area;
        }

        private string HelpText(int area)
        {
            if (area == AreaSettings) return "设置";
            if (!settingsMode) return null;
            if(area>=AreaHotkeyBind && area<AreaHotkeyBind+4)
                return "点击录入 Ctrl/Alt + 字母、数字、方向键或功能键（不含 F12），可加 Shift；Esc 取消，Delete 清除。占用冲突请自行更换\n" +
                    (area==AreaHotkeyBind ? "停止蓝牙与 Wi-Fi 接收，保留设备和音量偏好" : area==AreaHotkeyBind+1 ? "只重新建立当前模式的电脑端连接。手机仍在发送时会自动恢复接收；若手机已停止发送，请在手机端点击「开始发送」" : "Wi-Fi 软件音量每次调节 5%，手机同步；不改系统音量，蓝牙模式不生效");
            switch (area)
            {
                case AreaProjectPage: return "在默认浏览器打开项目 GitHub 主页";
                case AreaLicensePage: return "在默认浏览器查看完整 MIT 开源协议";
                case AreaExclusive: return "仅 Wi-Fi 模式生效。独占会占用所选播放设备，其他软件可能无声或自动切换输出。关闭独占后，若其他软件仍无声，请在该软件重新选择原播放设备\n开启时先静音，请调高 Wi-Fi 音量；系统合成器不能调节独占音量";
                case AreaVolumeValue:
                case AreaVolumeDown:
                case AreaVolumeUp: return "手机和电脑共用同一 Wi-Fi 音量值；软件内部按比例乘以音频采样值，不改动 Windows 系统音量或应用音量合成器设置\n点击数字可输入 0–100 的整数，± 每次调节 5%；共享时仍受系统音量影响，独占时绕过共享混音";
                case AreaOutputStatus: return OutputStatusText()+"\n设备事件同步；输出缓冲 "+(wifi?.Current.OutputBufferMs ?? 0)+"ms，有效预填 "+(wifi?.Current.EffectivePrefillMs ?? 0)+"ms\n"+(wifi?.Current.OutputFallbackReason ?? "实际输出模式以此处为准，设备不可用时会显示原因");
                case AreaOutput: return "两种模式共用此播放设备\n" + cachedPreferredName;
                case AreaAutoStart: return "登录 Windows 后自动打开 BlueAudioSink";
                case AreaCloseToTray: return "关闭窗口后保持连接和播放。右键托盘选择“彻底退出”结束程序";
                case AreaAutoConnect: return "启动或切换模式时，自动启用当前蓝牙/Wi-Fi模式的电脑端连接；手机仍需自行开始发送音频";
                case AreaBufferHelp:
                case AreaBuffer80:
                case AreaBuffer120:
                case AreaBuffer160: return "Wi-Fi 开始播放和断流恢复时的预缓冲。较高数值更抗网络抖动；独占生效时至少预填 220ms，开始播放会稍晚";
                case AreaDeviceBluetooth: return "查看 Windows 已配对的蓝牙手机，保留当前连接";
                case AreaDeviceWifi: return "查看 Wi-Fi 手机列表，保留当前连接";
                case AreaRefresh: return browsingWifiDevices ? "广播发现局域网手机；请在手机发送端选择这台电脑" : "重新读取 Windows 已配对的蓝牙手机";
                case AreaFirewallHelp:
                case AreaFirewallEnable: return "Wi-Fi 正常收音无需开启；无法收音时再尝试。仅为当前程序放行专用和公用网络、同一子网的 TCP/UDP 端口，不关闭防火墙";
                case AreaFirewallDisable: return "仅移除本程序创建的放行规则，不影响其他规则";
                case AreaFirewallCheck: return "重新读取防火墙状态，不修改规则";
            }
            if (area >= AreaDeviceBase && area < AreaDropdownBase)
            {
                int index = devicePage * MaxDeviceRows + area - AreaDeviceBase;
                if (index >= 0 && index < connectionDevices.Count)
                    return connectionDevices[index].Name + "\n" + (wifiMode ? "选择后锁定此手机，仍需在手机端开始发送" : "点击连接并记住这台手机，同一时间接收一台");
            }
            return null;
        }

        private void DrawHelp(IntPtr hdc, Metrics m)
        {
            if (!helpVisible) return;
            string text = HelpText(helpArea);
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length > 256) text = text.Substring(0, 256);
            const int calculateRect = 0x00000400;
            int paddingX = S(8), paddingY = S(5);
            int maxWidth = Math.Min(S(280), m.Width - S(32));
            var measured = Rect(0, 0, 0, 0);
            Win32.SelectObject(hdc, fontSmall);
            Win32.DrawText(hdc, text, -1, ref measured, calculateRect);
            int width = Math.Min(maxWidth, measured.Right + 2 * paddingX);
            measured = Rect(0, 0, width - 2 * paddingX, 0);
            Win32.DrawText(hdc, text, -1, ref measured, Win32.DT_WORDBREAK | calculateRect);
            int height = Math.Min(m.Height - S(24), measured.Bottom + 2 * paddingY);
            int left = Math.Clamp(helpX + S(10), S(12), m.Width - width - S(12));
            int top = helpY + S(22);
            if (top + height > m.Height - S(12)) top = Math.Max(S(12), helpY - height - S(12));
            var box = Rect(left, top, left + width, top + height);
            RoundFill(hdc, box, S(8), brushTooltip);
            DrawWrappedText(hdc, text, fontSmall, Win32.Rgb(255, 255, 255), Rect(left + paddingX, top + paddingY, box.Right - paddingX, box.Bottom - paddingY));
        }

        /// <summary>经典划钮：关闭时滑块在左且槽为灰色，开启时滑块在右且槽为主色。</summary>
        private void DrawSwitch(IntPtr hdc, int right, int centerY, bool on, bool hover, bool enabled=true)
        {
            int trackWidth = S(44);
            int trackHeight = S(24);
            int knobSize = S(18);
            int padding = S(3);

            int left = right - trackWidth;
            int top = centerY - trackHeight / 2;

            Win32.RECT track = Rect(left, top, left + trackWidth, top + trackHeight);
            IntPtr trackBrush;
            if (!enabled) trackBrush = brushSwitchOff;
            else if (on)
            {
                trackBrush = hover ? brushAccentHover : brushAccent;
            }
            else
            {
                trackBrush = hover ? brushSwitchOffHover : brushSwitchOff;
            }

            RoundFill(hdc, track, trackHeight / 2, trackBrush);

            int knobLeft = on
                ? left + trackWidth - padding - knobSize
                : left + padding;
            Win32.RECT knob = Rect(knobLeft, top + padding,
                knobLeft + knobSize, top + padding + knobSize);

            Dot(hdc, knob.Left, knob.Top, knobSize, Win32.Rgb(255, 255, 255));
        }

        private void DrawButton(IntPtr hdc, Win32.RECT rect, string text, int area, bool primary)
        {
            bool enabled = IsEnabled(area);
            bool hover = enabled && hoverArea == area;
            bool pressed = enabled && pressedArea == area;

            IntPtr fill;
            int textColor;

            if (!enabled)
            {
                fill = brushBack; textColor = DisabledColor;
            }
            else if (primary)
            {
                fill = pressed ? brushAccentPress : (hover ? brushAccentHover : brushAccent);
                textColor = Win32.Rgb(255, 255, 255);
            }
            else
            {
                fill = pressed ? brushBtnPress : (hover ? brushBtnHover : brushBtn);
                textColor = TextColor;
            }

            RoundFill(hdc, rect, S(8), fill);
            if (!primary)
            {
                RoundBorder(hdc, rect, S(8), hover ? brushAccent : brushBorder);
            }

            Win32.SelectObject(hdc, fontBody);
            Win32.SetTextColor(hdc, textColor);
            Win32.RECT textRect = rect;
            if (pressed)
            {
                textRect.Top += S(1);
                textRect.Bottom += S(1);
            }
            Win32.DrawText(hdc, text, -1, ref textRect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
        }

        private void DrawCaption(IntPtr hdc, string text, int x, int top)
        {
            Win32.SelectObject(hdc, fontCaption);
            Win32.SetTextColor(hdc, SecondaryColor);
            Win32.RECT rect = Rect(x, top, x + S(200), top + S(18));
            Win32.DrawText(hdc, text, -1, ref rect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        }

        private SmoothShapes smoothShapes;
        private bool SmoothShape(IntPtr hdc, Win32.RECT rect, double radius, int stroke, int color)
        {
            smoothShapes ??= new SmoothShapes();
            return smoothShapes.Draw(hdc, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, radius, stroke, color);
        }

        private void RoundFill(IntPtr hdc, Win32.RECT rect, int radius, IntPtr brush)
        {
            if (SmoothShape(hdc, rect, radius / 2.0, 0, SmoothShapes.BrushColor(brush))) return;
            IntPtr old = Win32.SelectObject(hdc, brush);
            IntPtr pen = Win32.GetStockObject(8); // NULL_PEN, never delete stock objects.
            IntPtr oldPen = Win32.SelectObject(hdc, pen);
            Win32.RoundRect(hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
            Win32.SelectObject(hdc, oldPen);
            Win32.SelectObject(hdc, old);
        }

        private void RoundBorder(IntPtr hdc, Win32.RECT rect, int radius, IntPtr brush)
        {
            int color = brush == brushAccent ? AccentTextColor : SmoothShapes.BrushColor(brush);
            int stroke = Math.Max(1, S(1));
            if (SmoothShape(hdc, rect, radius / 2.0, stroke, color)) return;
            IntPtr pen = Win32.CreatePen(Win32.PS_SOLID, stroke, color);
            IntPtr oldPen = Win32.SelectObject(hdc, pen);
            IntPtr oldBrush = Win32.SelectObject(hdc, Win32.GetStockObject(Win32.HOLLOW_BRUSH));
            Win32.RoundRect(hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
            Win32.SelectObject(hdc, oldBrush);
            Win32.SelectObject(hdc, oldPen);
            Win32.DeleteObject(pen);
        }

        private void Text(IntPtr hdc, string value, IntPtr font, int color, Win32.RECT rect, bool center = false)
        {
            Win32.SelectObject(hdc, font); Win32.SetTextColor(hdc, color);
            Win32.DrawText(hdc, value ?? "—", -1, ref rect, (center ? Win32.DT_CENTER : Win32.DT_LEFT) | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
        }

        private void Dot(IntPtr hdc, int x, int y, int size, int color)
        {
            if (SmoothShape(hdc, Rect(x, y, x + size, y + size), size / 2.0, 0, color)) return;
            IntPtr brush = Win32.CreateSolidBrush(color), pen = Win32.GetStockObject(8);
            IntPtr oldBrush = Win32.SelectObject(hdc, brush), oldPen = Win32.SelectObject(hdc, pen);
            Win32.Ellipse(hdc, x, y, x + size, y + size);
            Win32.SelectObject(hdc, oldBrush); Win32.SelectObject(hdc, oldPen); Win32.DeleteObject(brush);
        }

        private int ToneColor(ConnectionTone tone)
        {
            switch (tone)
            {
                case ConnectionTone.Active: return SuccessColor;
                case ConnectionTone.Ready: return AccentTextColor;
                case ConnectionTone.Warning: return WarningColor;
                case ConnectionTone.Error: return ErrorColor;
                default: return DisabledColor;
            }
        }

        private void ShowNotice(string message, int seconds = 3) { notice = message; noticeUntil = DateTime.Now.AddSeconds(seconds); }

        private bool CopyAddress(string value)
        {
            if (string.IsNullOrEmpty(value) || !Win32.OpenClipboard(hwnd)) return false;
            IntPtr memory = IntPtr.Zero;
            try
            {
                char[] text = (value + "\0").ToCharArray();
                memory = Win32.GlobalAlloc(2, new UIntPtr((uint)(text.Length * 2)));
                if (memory == IntPtr.Zero) return false;
                IntPtr data = Win32.GlobalLock(memory);
                if (data == IntPtr.Zero) return false;
                System.Runtime.InteropServices.Marshal.Copy(text, 0, data, text.Length);
                Win32.GlobalUnlock(memory);
                if (!Win32.EmptyClipboard() || Win32.SetClipboardData(13, memory) == IntPtr.Zero) return false;
                memory = IntPtr.Zero; return true;
            }
            finally { if (memory != IntPtr.Zero) Win32.GlobalFree(memory); Win32.CloseClipboard(); }
        }

        private Win32.RECT RetryRect(Metrics m)
        { return Rect(m.SettingsRect.Left - S(80), S(66), m.SettingsRect.Left - S(8), S(96)); }

        private void DrawKeyboardFocus(IntPtr hdc, Metrics m)
        {
            if (!keyboardFocus || focusArea == AreaNone) return;
            Win32.RECT r;
            switch (focusArea)
            {
                case AreaSettingsGeneral:
                case AreaSettingsLogs:
                case AreaSettingsDevices:
                case AreaSettingsNetwork:
                case AreaSettingsHotkeys:
                case AreaSettingsAbout:
                case AreaFirewallEnable:
                case AreaFirewallDisable:
                case AreaFirewallCheck:
                case AreaPreviousPage:
                case AreaNextPage:
                case AreaDeviceBluetooth:
                case AreaDeviceWifi: r = SettingsControlRect(m, focusArea); break;
                case AreaLogDays:
                case AreaLogChoose:
                case AreaLogDefault:
                case AreaLogOpen:
                case AreaLogClear: r = LogControlRect(m, focusArea); break;
                case AreaProjectPage: r = AboutButtonRect(m,false); break;
                case AreaLicensePage: r = AboutButtonRect(m,true); break;
                case AreaModeBluetooth: r = m.ModeBluetoothRect; break;
                case AreaModeWifi: r = m.ModeWifiRect; break;
                case AreaSettings: r = m.SettingsRect; break;
                case AreaSettingsBack: r = m.SettingsBackRect; break;
                case AreaOutput: r = m.OutputRect; break;
                case AreaWifiEnable:
                case AreaConnect: r = m.ConnectRect; break;
                case AreaWifiDisconnect:
                case AreaDisconnect: r = m.DisconnectRect; break;
                case AreaRefresh: r = m.RefreshRect; break;
                case AreaCopyAddress: r = m.CopyAddressRect; break;
                case AreaDetails: r = m.DetailsRect; break;
                case AreaRetryWifi: r = RetryRect(m); break;
                case AreaBuffer80: r = m.Buffer80Rect; break;
                case AreaBuffer120: r = m.Buffer120Rect; break;
                case AreaBuffer160: r = m.Buffer160Rect; break;
                case AreaExclusive: r = PreferenceRect(m,AreaExclusive); break;
                case AreaVolumeValue: r = VolumeValueRect(m); break;
                case AreaVolumeDown: r = VolumeRect(m,false); break;
                case AreaVolumeUp: r = VolumeRect(m,true); break;
                case AreaAutoStart: r = PreferenceRect(m, AreaAutoStart); break;
                case AreaCloseToTray: r = PreferenceRect(m, AreaCloseToTray); break;
                case AreaAutoConnect: r = PreferenceRect(m, AreaAutoConnect); break;
                case AreaThemeSystem:
                case AreaThemeLight:
                case AreaThemeDark: r = ThemeOptionRect(m, focusArea); break;
                default:
                    if(focusArea>=AreaHotkeyBind && focusArea<AreaHotkeyBind+4) r=HotkeyRect(m,focusArea-AreaHotkeyBind,false);
                    else if(focusArea>=AreaHotkeyClear && focusArea<AreaHotkeyClear+4) r=HotkeyRect(m,focusArea-AreaHotkeyClear,true);
                    else if (focusArea >= AreaDropdownBase && focusArea < AreaDropdownLimit && dropdownOpen)
                    { int top = dropdownRect.Top + S(4) + (focusArea - AreaDropdownBase) * S(DropdownRowHeight); r = Rect(dropdownRect.Left + S(4), top, dropdownRect.Right - S(4), top + S(DropdownRowHeight)); }
                    else if (focusArea >= AreaDeviceBase && focusArea < AreaDropdownBase)
                    { int top = m.DeviceListTop + (focusArea - AreaDeviceBase) * S(DeviceRowHeight); r = Rect(m.Left, top, m.Left + m.ContentWidth, top + S(DeviceRowHeight)); }
                    else return;
                    break;
            }
            if (SmoothShape(hdc, Rect(r.Left - S(2), r.Top - S(2), r.Right + S(2), r.Bottom + S(2)), S(10) / 2.0, S(2), AccentTextColor)) return;
            IntPtr pen = Win32.CreatePen(Win32.PS_SOLID, S(2), AccentTextColor);
            IntPtr oldPen = Win32.SelectObject(hdc, pen), oldBrush = Win32.SelectObject(hdc, Win32.GetStockObject(Win32.HOLLOW_BRUSH));
            Win32.RoundRect(hdc, r.Left - S(2), r.Top - S(2), r.Right + S(2), r.Bottom + S(2), S(10), S(10));
            Win32.SelectObject(hdc, oldPen); Win32.SelectObject(hdc, oldBrush); Win32.DeleteObject(pen);
        }

        private static Win32.RECT Rect(int left, int top, int right, int bottom)
        {
            Win32.RECT r = new Win32.RECT();
            r.Left = left;
            r.Top = top;
            r.Right = right;
            r.Bottom = bottom;
            return r;
        }

        private static bool Contains(Win32.RECT r, int x, int y)
        {
            return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
        }

        private static string DescribeState(SinkState state)
        {
            switch (state)
            {
                case SinkState.Idle: return "蓝牙尚未连接";
                case SinkState.Busy: return "正在连接";
                case SinkState.Armed: return "连接就绪，等待音频";
                case SinkState.LinkReady: return "链路已建立";
                case SinkState.Flowing: return "蓝牙音频已接入";
                case SinkState.Backoff: return "重试中";
                case SinkState.Faulted: return "连接失败";
                default: return state.ToString();
            }
        }

        public void Dispose()
        {
            windowDisposed = true;
            settingsWriter.Dispose();
            connectionOperation?.GetAwaiter().GetResult();
            logOperation?.GetAwaiter().GetResult();
            hotkeyCapture?.Dispose();
            hotkeys?.Dispose();
            volumeInput?.Dispose();
            smoothShapes?.Dispose();
            try
            {
                controller.SnapshotChanged -= OnSnapshotChanged;
                tray?.Dispose();
                wifi.Dispose();
                if (appIcon != IntPtr.Zero) Win32.DestroyIcon(appIcon);
                if (smallAppIcon != IntPtr.Zero) Win32.DestroyIcon(smallAppIcon);
                Win32.DeleteObject(fontTitle);
                Win32.DeleteObject(fontValue);
                Win32.DeleteObject(fontHeading);
                Win32.DeleteObject(fontBody);
                Win32.DeleteObject(fontSmall);
                Win32.DeleteObject(fontCaption);
                Win32.DeleteObject(fontGear);
                ReleaseThemeBrushes();
            }
            catch
            {
            }
        }
    }
}
