using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite
{
    internal static class Program
    {
        private static Log log;

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == WifiFirewall.HelperArgument)
            {
                if (args.Length != 3 || !int.TryParse(args[2], out int port)) return 2;
                return WifiFirewall.RunHelper(args[1], port);
            }
            CliOptions options = Cli.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(Cli.HelpText());
                return 0;
            }

            // 默认（或 --tray）即托盘模式：释放控制台，双击启动时不出现黑窗。
            // 带显式参数（--list/--diagnose 等）时保留控制台，便于查看输出。
            bool launchedWithArgs = args != null && args.Length > 0;
            bool isTrayMode = options.NoTray == false;
            if (isTrayMode && !launchedWithArgs)
            {
                try { FreeConsole(); } catch { }
            }

            if (isTrayMode)
            {
                // 单实例：已在运行则直接退出，避免两个进程争抢同一个接收通道。
                bool createdNew;
                using (Mutex singleInstance = new Mutex(true, @"Global\BlueAudioSink.SingleInstance", out createdNew))
                {
                    if (!createdNew)
                    {
                        for (int attempt = 0; attempt < 15; attempt++)
                        {
                            IntPtr existing = Win32.FindWindow("BlueAudioSinkMainWindow", null);
                            if (existing != IntPtr.Zero)
                            {
                                Win32.GetWindowThreadProcessId(existing, out uint processId);
                                Win32.AllowSetForegroundWindow(processId);
                                Win32.PostMessage(existing, SinkLite.Platform.TrayIcon.RestoreMessage, IntPtr.Zero, IntPtr.Zero);
                                return 0;
                            }
                            Thread.Sleep(100);
                        }
                        Win32.MessageBox(IntPtr.Zero, "程序已在运行，但主窗口未就绪。请关闭旧进程后重试。", "BlueAudioSink", 0x40);
                        return 1;
                    }

                    try
                    {
                        return RunCore(options);
                    }
                    catch (Exception ex)
                    {
                        if (log != null) log.Error("App.StartFailed", ("error", ex.ToString()));
                        Win32.MessageBox(IntPtr.Zero, "启动失败：" + ex.Message, "BlueAudioSink", 0x10);
                        return 2;
                    }
                    finally
                    {
                        try { singleInstance.ReleaseMutex(); } catch { }
                    }
                }
            }

            return RunCore(options);
        }

        private static int RunCore(CliOptions options)
        {
            // WinRT 需要初始化 COM 单元；在后台线程上 MTA 是安全选择。
            WinRtCompat.Initialize();

            Settings settings = Settings.Load();
            using var runLog = new Log(settings.LogDirectory, settings.LogRetainDays);
            log = runLog;
            Policy policy = Policy.Default;
            if (settings.SampleSeconds >= 1)
            {
                policy.SampleInterval = TimeSpan.FromSeconds(settings.SampleSeconds);
            }

            using (SinkController controller = new SinkController(policy, log))
            {
                try
                {
                    controller.Start();
                }
                catch (Exception ex)
                {
                    log.Error("App.StartFailed", ("error", ex.Message));
                    Console.WriteLine("启动失败：" + ex.Message);
                    return 2;
                }

                if (options.ListOnly)
                {
                    return RunList(controller);
                }

                if (options.DiagnoseSeconds > 0)
                {
                    return RunDiagnose(controller, options.DiagnoseSeconds);
                }

                if (options.ConnectOnce)
                {
                    return RunConnected(controller);
                }

                return RunWindow(controller, options);
            }
        }

        private static int RunList(SinkController controller)
        {
            WaitForDevices(controller);
            Console.WriteLine("可接收音频的已配对设备：" + controller.Devices.Count);
            int index = 1;
            foreach (SinkDevice device in controller.Devices)
            {
                Console.WriteLine("  " + index + ") " + device.Name);
                Console.WriteLine("     " + device.Id);
                index++;
            }

            if (controller.Devices.Count == 0)
            {
                Console.WriteLine("  （无）请先在 Windows 蓝牙设置中配对手机。");
                return 1;
            }
            return 0;
        }

        private static int RunDiagnose(SinkController controller, int seconds)
        {
            WaitForDevices(controller);
            if (controller.Devices.Count == 0)
            {
                Console.WriteLine("没有可接收音频的设备，请先配对手机。");
                return 1;
            }

            using (EndpointWatcher watcher = new EndpointWatcher())
            {
                Console.WriteLine("诊断开始，采样 " + seconds + " 秒（2 秒一次）");
                Console.WriteLine("提示：如需验证音频到达，请在手机上开始播放音乐。");
                Console.WriteLine();

                controller.RequestConnect(null);

                DateTime end = DateTime.Now.AddSeconds(seconds);
                while (DateTime.Now < end)
                {
                    SinkSnapshot snapshot = controller.Current;
                    float peak = watcher.GetDefaultPeak();
                    EndpointInfo render = watcher.GetDefaultRender();

                    bool hasA2dp = false;
                    string a2dpName = null;
                    foreach (EndpointInfo e in watcher.Sample())
                    {
                        if (e.IsBluetoothA2dp)
                        {
                            hasA2dp = true;
                            a2dpName = e.Name;
                            break;
                        }
                    }

                    Console.WriteLine(string.Format(
                        "{0:HH:mm:ss}  状态={1,-10} 峰值={2:0.0000} 蓝牙端点={3} 默认输出={4}",
                        DateTime.Now,
                        snapshot.State,
                        peak < 0 ? 0f : peak,
                        hasA2dp ? (string.IsNullOrEmpty(a2dpName) ? "有" : a2dpName) : "无",
                        render == null ? "?" : render.Name));

                    Thread.Sleep(2000);
                }

                Console.WriteLine();
                Console.WriteLine("归因结论：");
                SinkSnapshot final = controller.Current;
                Console.WriteLine("  最终状态：" + final.State);
                Console.WriteLine("  说明：" + final.Detail);
                if (!string.IsNullOrEmpty(final.Hint))
                {
                    Console.WriteLine("  建议：" + final.Hint);
                }
                return 0;
            }
        }

        private static int RunConnected(SinkController controller)
        {
            WaitForDevices(controller);
            Console.WriteLine("按 Ctrl+C 退出。");
            controller.RequestConnect(null);

            ManualResetEventSlim exit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                exit.Set();
            };

            SinkState last = SinkState.Idle;
            while (!exit.Wait(500))
            {
                SinkSnapshot snapshot = controller.Current;
                if (snapshot.State != last)
                {
                    last = snapshot.State;
                    Console.WriteLine(string.Format("{0:HH:mm:ss}  {1}  {2}", DateTime.Now, snapshot.State, snapshot.Detail));
                    if (!string.IsNullOrEmpty(snapshot.Hint))
                    {
                        Console.WriteLine("           提示：" + snapshot.Hint);
                    }
                }
            }

            return 0;
        }

        private static int RunWindow(SinkController controller, CliOptions options)
        {
            ShellAppearance.Initialize(log);
            Settings settings = Settings.Load();
            bool wifiStart = options.Wifi ||
                string.Equals(settings.Mode, "wifi", StringComparison.OrdinalIgnoreCase);

            // 刻意不在这里 WaitForDevices：
            // 设备枚举在后台线程进行，窗口应当先显示出来，设备列表随后自动填充。
            // 阻塞等待会让启动速度直接受蓝牙枚举耗时影响（曾导致数百毫秒到数秒的白等）。
            using (EndpointWatcher watcher = new EndpointWatcher())
            {
                RouteControl route = new RouteControl(watcher);
                controller.AttachRoute(route);

                if (!string.IsNullOrEmpty(options.LockOutputName))
                {
                    string hit = controller.SetPreferredRenderByName(options.LockOutputName);
                    log.Info("Route.LockArgument", ("requested", options.LockOutputName), ("matched", hit ?? "(未命中)"));
                }

                using (SinkLite.UI.MainWindow window = new SinkLite.UI.MainWindow(controller, log, wifiStart))
                {
                    window.Create();
                    window.ShowWindow();
                    log.Info("Window.Starting", ("devices", controller.Devices.Count), ("mode", wifiStart ? "wifi" : "bluetooth"));

                    // 只在主窗口统一执行当前模式的启动偏好。
                    window.ApplyAutoConnect(options.Wifi);

                    // 主窗口统一处理界面与托盘消息。
                    Win32.MSG msg;
                    while (Win32.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        Win32.TranslateMessage(ref msg);
                        Win32.DispatchMessage(ref msg);
                    }

                    log.Info("App.Exited");
                }
            }

            return 0;
        }

        private static void WaitForDevices(SinkController controller)
        {
            // 只请求一次刷新并耐心等待，避免启动阶段反复枚举设备
            if (controller.Devices.Count > 0)
            {
                return;
            }

            controller.RequestRefreshDevices();
            DateTime deadline = DateTime.Now.AddSeconds(6);
            while (controller.Devices.Count == 0 && DateTime.Now < deadline)
            {
                Thread.Sleep(150);
            }
        }
    }

    internal static class WinRtCompat
    {
        /// <summary>
        /// 轻量预热 WinRT。刻意不再调用 GetDeviceSelector()：
        /// 实测它会连带加载 windows.storage.dll（约 9 MB 工作集），
        /// 而设备 ID 现已由注册表构造，不再需要设备选择器。
        /// </summary>
        public static void Initialize()
        {
            // 实际初始化发生在后台线程首次调用 WinRT 时（线程池为 MTA）。
        }
    }
}
