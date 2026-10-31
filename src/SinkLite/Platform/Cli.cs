using System;
using System.Collections.Generic;

namespace SinkLite.Platform
{
    internal sealed class CliOptions
    {
        public bool ShowHelp { get; set; }

        public bool Console { get; set; }

        public int DiagnoseSeconds { get; set; }

        public bool ListOnly { get; set; }

        public bool ConnectOnce { get; set; }

        public bool NoTray { get; set; }

        /// <summary>--lock-output 指定的输出设备名称片段。</summary>
        public string LockOutputName { get; set; }

        /// <summary>--wifi：直接以 Wi-Fi 模式启动，跳过蓝牙自动连接。</summary>
        public bool Wifi { get; set; }
    }

    internal static class Cli
    {
        public static CliOptions Parse(string[] args)
        {
            CliOptions options = new CliOptions();
            if (args == null)
            {
                return options;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? string.Empty;
                switch (a.ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                    case "--tray":
                        // 兼容别名：显示主窗口，关闭行为由设置中的托盘开关控制。
                        break;
                    case "--console":
                        options.Console = true;
                        options.NoTray = true;
                        break;
                    case "--list":
                        options.ListOnly = true;
                        options.NoTray = true;
                        break;
                    case "--connect":
                        options.ConnectOnce = true;
                        options.NoTray = true;
                        break;
                    case "--wifi":
                        options.Wifi = true;
                        break;
                    case "--lock-output":
                        if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                        {
                            options.LockOutputName = args[i + 1];
                            i++;
                        }
                        break;
                    case "--diagnose":
                        options.NoTray = true;
                        int seconds = 30;
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed) && parsed > 0)
                        {
                            seconds = parsed;
                            i++;
                        }
                        options.DiagnoseSeconds = seconds;
                        break;
                    default:
                        break;
                }
            }

            return options;
        }

        public static string HelpText()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "BlueAudioSink - 让 Windows 电脑接收手机音频（蓝牙 / Wi-Fi）",
                "",
                "用法：BlueAudioSink [选项]",
                "",
                "  （无参数）     启动主窗口（默认）",
                "  --list         列出可接收的已配对设备后退出",
                "  --connect      连接后进入监听并输出状态变化（Ctrl+C 退出）",
                "  --wifi         以 Wi-Fi 模式启动（监听 UDP 45679，广播 45678）",
                "  --diagnose [秒] 诊断模式：采样并输出三层归因（默认 30 秒）",
                "  --lock-output 名称片段   把声音输出锁定到匹配的设备（会自动记住）",
                "  --console      不启动托盘，仅控制台运行",
                "  --help         显示本帮助",
                "",
                "日志目录：%LocalAppData%\\BlueAudioSink\\logs"
            });
        }
    }
}
