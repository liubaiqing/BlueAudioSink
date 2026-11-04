using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SinkLite.Platform;

namespace SinkLite.UI
{
    internal sealed partial class MainWindow
    {
        private const int AreaSettingsLogs = 354;
        private const int AreaLogDays = 410, AreaLogChoose = 411, AreaLogDefault = 412, AreaLogOpen = 413, AreaLogClear = 414;
        private static readonly int[] LogActions = { AreaLogDays, AreaLogChoose, AreaLogDefault, AreaLogOpen, AreaLogClear };
        private bool logSettings;

        private Win32.RECT LogControlRect(Metrics m, int area)
        {
            int right = m.Left + m.ContentWidth;
            int middle = m.Left + m.ContentWidth / 2;
            return area switch
            {
                AreaLogDays => Rect(right - S(126), S(80), right - S(36), S(112)),
                AreaLogChoose => Rect(m.Left, S(218), middle - S(4), S(252)),
                AreaLogDefault => Rect(middle + S(4), S(218), right, S(252)),
                AreaLogOpen => Rect(m.Left, S(270), middle - S(4), S(304)),
                _ => Rect(middle + S(4), S(270), right, S(304)),
            };
        }

        private void DrawLogSettings(IntPtr hdc, Metrics m)
        {
            int right = m.Left + m.ContentWidth;
            int ink = Win32.Rgb(23, 40, 63);
            var retention = Rect(m.Left, S(68), right, S(124));
            RoundFill(hdc, retention, S(8), brushCard);
            RoundBorder(hdc, retention, S(8), brushBorder);
            Text(hdc, "日志保留时间", fontBody, ink, Rect(m.Left + S(12), S(80), LogControlRect(m, AreaLogDays).Left - S(10), S(112)));
            DrawButton(hdc, LogControlRect(m, AreaLogDays), pageSettings.LogRetainDays.ToString(), AreaLogDays, false);
            Text(hdc, "天", fontBody, ink, Rect(right - S(28), S(80), right, S(112)));
            Text(hdc, "存放位置", fontHeading, ink, Rect(m.Left, S(138), right, S(164)));
            DrawWrappedText(hdc, log.DirectoryPath, fontSmall, ink, Rect(m.Left, S(168), right, S(214)));
            DrawButton(hdc, LogControlRect(m, AreaLogChoose), "修改存放位置", AreaLogChoose, false);
            DrawButton(hdc, LogControlRect(m, AreaLogDefault), "恢复默认位置", AreaLogDefault, false);
            DrawButton(hdc, LogControlRect(m, AreaLogOpen), "打开文件夹", AreaLogOpen, false);
            DrawButton(hdc, LogControlRect(m, AreaLogClear), "清理日志", AreaLogClear, false);
            if (log.WriteFailed)
                Text(hdc, "无法写入日志，请选择可写的文件夹", fontSmall, Win32.Rgb(151, 91, 22), Rect(m.Left, S(314), right, S(342)));
        }

        private void ActivateLogControl(int area)
        {
            try
            {
                switch (area)
                {
                    case AreaLogDays:
                        volumeInput?.Dispose();
                        volumeInput = new VolumeInput(SaveLogRetention, () => ShowNotice("请输入正整数天数"), minimum: 1, maximum: int.MaxValue);
                        if (!volumeInput.Begin(hwnd, LogControlRect(ComputeMetrics(), area), fontBody, pageSettings.LogRetainDays))
                            ShowNotice("无法打开天数输入框");
                        break;
                    case AreaLogChoose:
                        string chosen = ChooseLogFolder();
                        if (chosen != null) SaveLogDirectory(chosen);
                        break;
                    case AreaLogDefault:
                        SaveLogDirectory(string.Empty);
                        break;
                    case AreaLogOpen:
                        Directory.CreateDirectory(log.DirectoryPath);
                        Process.Start(new ProcessStartInfo(log.DirectoryPath) { UseShellExecute = true });
                        break;
                    case AreaLogClear:
                        if (Win32.MessageBox(hwnd, "清理当前文件夹中的蓝音频日志？", "连接日志", 0x124) != 6)
                            break;
                        var result = log.Clear();
                        ShowNotice(result.Failed == 0 ? $"已清理 {result.Removed} 个日志文件" : $"已清理 {result.Removed} 个，{result.Failed} 个未能清理");
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception)
            {
                ShowNotice(area == AreaLogOpen ? "无法打开日志文件夹" : "无法使用该文件夹，请选择可写的位置");
            }
        }

        private void SaveLogRetention(int days)
        {
            var settings = Settings.Load();
            settings.LogRetainDays = days;
            if (!settings.Save()) { ShowNotice("无法保存日志保留时间"); return; }
            log.Configure(log.DirectoryPath, days);
            pageSettings = settings;
            ShowNotice($"日志保留时间已设为 {days} 天");
        }

        private void SaveLogDirectory(string path)
        {
            string checkedPath = Log.CheckDirectory(string.IsNullOrEmpty(path) ? Log.DefaultDirectory : path);
            var settings = Settings.Load();
            settings.LogDirectory = string.IsNullOrEmpty(path) ? string.Empty : checkedPath;
            if (!settings.Save()) { ShowNotice("无法保存日志位置"); return; }
            log.Configure(checkedPath, settings.LogRetainDays);
            pageSettings = settings;
            ShowNotice("日志位置已更新");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BrowseInfo
        {
            public IntPtr Owner, Root, DisplayName;
            public string Title;
            public uint Flags;
            public IntPtr Callback, Parameter;
            public int Image;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHBrowseForFolder(ref BrowseInfo info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDList(IntPtr item, StringBuilder path);

        private string ChooseLogFolder()
        {
            const uint filesystemFolders = 0x0001, editBox = 0x0010, resizableDialog = 0x0040;
            var info = new BrowseInfo { Owner = hwnd, Title = "选择蓝音频日志文件夹", Flags = filesystemFolders | editBox | resizableDialog };
            IntPtr item = SHBrowseForFolder(ref info);
            if (item == IntPtr.Zero) return null;
            try
            {
                var path = new StringBuilder(260);
                return SHGetPathFromIDList(item, path) ? path.ToString() : null;
            }
            finally { Marshal.FreeCoTaskMem(item); }
        }
    }
}
