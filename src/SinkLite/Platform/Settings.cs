using System;
using System.IO;
using System.Text;

namespace SinkLite.Platform
{
    internal enum AppTheme { System, Light, Dark }

    /// <summary>
    /// 极简配置：ini 风格键值，存放于 %LocalAppData%\BlueAudioSink\settings.ini。
    /// 只保存设备 ID 与开关，不保存任何隐私内容。
    /// </summary>
    internal sealed class Settings
    {
        public string DeviceId { get; set; } = string.Empty;

        public bool AutoConnect { get; set; } = true;

        public bool CloseToTray { get; set; }
        public AppTheme Theme { get; set; } = AppTheme.System;
        public int LogRetainDays { get; set; } = Log.DefaultRetainDays;
        public string LogDirectory { get; set; } = string.Empty;

        public HotkeyBinding[] Hotkeys { get; } = new HotkeyBinding[4];

        public bool WifiExclusive { get; set; }
        public int WifiVolume { get; set; } = 100;
        public void SetWifiExclusive(bool enabled)
        {
            WifiExclusive=enabled;
            if(enabled)WifiVolume=0;
        }

        public int SampleSeconds { get; set; } = 2;

        /// <summary>首选输出端点 ID。非空时程序会把默认输出锁定在它上面。</summary>
        public string PreferredRenderId { get; set; } = string.Empty;

        /// <summary>启动模式："bluetooth" 或 "wifi"。</summary>
        public string Mode { get; set; } = "bluetooth";

        /// <summary>Wi-Fi 音频接收端口（UDP）。</summary>
        public int WifiPort { get; set; } = 45679;

        /// <summary>Wi-Fi 抖动缓冲下限，毫秒。</summary>
        public int BufferBaseMs { get; set; } = 80;

        /// <summary>Wi-Fi 抖动缓冲上限，毫秒。</summary>
        public int BufferMaxMs { get; set; } = 200;

        public static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BlueAudioSink",
                    "settings.ini");
            }
        }

        public static Settings Load()
        {
            Settings settings = new Settings();
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return settings;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                    {
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "logretaindays":
                            if (int.TryParse(value, out int days) && days > 0) settings.LogRetainDays = days;
                            break;
                        case "logdirectory": settings.LogDirectory = value; break;
                        case "hotkeydisconnect": settings.Hotkeys[0] = HotkeyBinding.Parse(value); break;
                        case "hotkeyreconnect": settings.Hotkeys[1] = HotkeyBinding.Parse(value); break;
                        case "hotkeyvolumeup": settings.Hotkeys[2] = HotkeyBinding.Parse(value); break;
                        case "hotkeyvolumedown": settings.Hotkeys[3] = HotkeyBinding.Parse(value); break;
                        case "deviceid":
                            settings.DeviceId = value;
                            break;
                        case "autoconnect":
                            settings.AutoConnect = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "closetotray":
                            settings.CloseToTray = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "theme":
                            if (Enum.TryParse(value, true, out AppTheme theme) && Enum.IsDefined(theme))
                                settings.Theme = theme;
                            break;
                        case "preferredrenderid":
                            settings.PreferredRenderId = value;
                            break;
                        case "wifiexclusive":
                            settings.WifiExclusive = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "wifivolume":
                            if(int.TryParse(value,out int volume)) settings.WifiVolume=Math.Clamp(volume,0,100);
                            break;
                        case "sampleseconds":
                            int seconds;
                            if (int.TryParse(value, out seconds) && seconds >= 1 && seconds <= 30)
                            {
                                settings.SampleSeconds = seconds;
                            }
                            break;
                        case "mode":
                            if (value.Equals("wifi", StringComparison.OrdinalIgnoreCase))
                            {
                                settings.Mode = "wifi";
                            }
                            else if (value.Equals("bluetooth", StringComparison.OrdinalIgnoreCase) ||
                                     value.Equals("bt", StringComparison.OrdinalIgnoreCase))
                            {
                                settings.Mode = "bluetooth";
                            }
                            break;
                        case "wifiport":
                            int wifiPort;
                            if (int.TryParse(value, out wifiPort) && wifiPort >= 1024 && wifiPort <= 65535)
                            {
                                settings.WifiPort = wifiPort;
                            }
                            break;
                        case "bufferbasems":
                            int bufferBase;
                            if (int.TryParse(value, out bufferBase) && bufferBase >= 10 && bufferBase <= 800)
                            {
                                settings.BufferBaseMs = bufferBase;
                            }
                            break;
                        case "buffermaxms":
                            int bufferMax;
                            if (int.TryParse(value, out bufferMax) && bufferMax >= 40 && bufferMax <= 1000)
                            {
                                settings.BufferMaxMs = bufferMax;
                            }
                            break;
                    }
                }
            }
            catch
            {
            }

            // 允许用户把上限改得比下限还小：以本文件中的修正为准，保证缓冲算法参数合法。
            if (settings.BufferMaxMs < settings.BufferBaseMs)
            {
                settings.BufferMaxMs = settings.BufferBaseMs;
            }

            return settings;
        }

        public bool Save()
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# BlueAudioSink 设置");
                sb.AppendLine("deviceid=" + (DeviceId ?? string.Empty));
                sb.AppendLine("autoconnect=" + (AutoConnect ? "1" : "0"));
                sb.AppendLine("closetotray=" + (CloseToTray ? "1" : "0"));
                sb.AppendLine("theme=" + Theme.ToString().ToLowerInvariant());
                sb.AppendLine("logretaindays=" + LogRetainDays);
                sb.AppendLine("logdirectory=" + LogDirectory);
                sb.AppendLine("wifiexclusive=" + (WifiExclusive ? "1" : "0"));
                sb.AppendLine("wifivolume=" + Math.Clamp(WifiVolume,0,100));
                sb.AppendLine("sampleseconds=" + SampleSeconds);
                sb.AppendLine("preferredrenderid=" + (PreferredRenderId ?? string.Empty));
                sb.AppendLine("mode=" + (Mode ?? "bluetooth"));
                sb.AppendLine("wifiport=" + WifiPort);
                sb.AppendLine("bufferbasems=" + BufferBaseMs);
                sb.AppendLine("buffermaxms=" + BufferMaxMs);
                for(int i=0;i<4;i++) sb.AppendLine(GlobalHotkeys.SettingNames[i]+"="+Hotkeys[i].Encode());
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
