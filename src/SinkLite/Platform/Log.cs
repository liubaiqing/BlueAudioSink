using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SinkLite.Platform
{
    /// <summary>
    /// 极简滚动 JSONL 日志：按设置保留，单文件上限 8 MB。
    /// 只记录状态、耗时与错误码，不记录音频内容。
    /// </summary>
    internal sealed class Log : IDisposable
    {
        private const long MaxBytes = 8L * 1024 * 1024;
        public const int DefaultRetainDays = 90;
        private static readonly Regex LogFileName = new Regex(@"\Asink-\d{8}(?:-\d{6}(?:-\d{3})?)?\.jsonl\z", RegexOptions.CultureInvariant);

        private readonly object sync = new object();
        private readonly HashSet<string> androidEventIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> androidEventOrder = new Queue<string>();
        private string directory;
        private int retainDays;
        private readonly Timer cleanupTimer;
        private bool disposed;
        public bool WriteFailed { get; private set; }
        public string DirectoryPath => Volatile.Read(ref directory);
        private StreamWriter writer;
        private DateTime openedDay;
        private long written;

        public Log(string directory, int retainDays = DefaultRetainDays)
        {
            this.directory = string.IsNullOrEmpty(directory) ? DefaultDirectory : directory;
            this.retainDays = retainDays;
            if (string.IsNullOrEmpty(directory)) MoveLegacyLogs();
            CleanupExpired();
            cleanupTimer = new Timer(_ => CleanupExpired(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        private static string ExecutableDirectory => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        public static string DefaultDirectory => Path.Combine(ExecutableDirectory, "logs");

        private void MoveLegacyLogs()
        {
            try
            {
                foreach (var file in new DirectoryInfo(ExecutableDirectory).EnumerateFiles("sink-*.jsonl"))
                {
                    try
                    {
                        if (!IsLogFile(file)) continue;
                        Directory.CreateDirectory(directory);
                        string destination = Path.Combine(directory, file.Name);
                        if (File.Exists(destination))
                        {
                            string prefix = file.Name.Substring(0, 13) + "-" +
                                file.LastWriteTime.ToString("HHmmss", CultureInfo.InvariantCulture);
                            for (int index = 0; index < 1000 && File.Exists(destination); index++)
                                destination = Path.Combine(directory, prefix + "-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".jsonl");
                        }
                        file.MoveTo(destination);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static string CheckDirectory(string path)
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(fullPath);
            string probe = Path.Combine(fullPath, ".bas-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return fullPath;
        }

        public void Configure(string path, int days)
        {
            lock (sync)
            {
                if (!string.Equals(directory, path, StringComparison.OrdinalIgnoreCase))
                {
                    CloseWriter();
                    directory = path;
                    WriteFailed = false;
                }
                retainDays = days;
                CleanupExpired();
            }
        }

        public (int Removed, int Failed) Clear()
        {
            lock (sync)
            {
                CloseWriter();
                return DeleteLogs(_ => true);
            }
        }

        public void Info(string eventName, params (string Key, object Value)[] fields)
        {
            Write("info", eventName, fields);
        }

        public void Warn(string eventName, params (string Key, object Value)[] fields)
        {
            Write("warn", eventName, fields);
        }

        public void Error(string eventName, params (string Key, object Value)[] fields)
        {
            Write("error", eventName, fields);
        }

        public bool WriteAndroidEvent(string id, long phoneUnixMs, string code, long a, long b, long c, uint pingToken, string context = null)
        {
            lock (sync)
            {
                if (androidEventIds.Contains(id)) return true;
                if (!Write("info", "Android." + code, new (string, object)[] {
                    ("id", id), ("phoneUnixMs", phoneUnixMs),
                    ("pingToken", pingToken), ("a", a), ("b", b), ("c", c), ("context", context) })) return false;
                androidEventIds.Add(id);
                androidEventOrder.Enqueue(id);
                if (androidEventOrder.Count > 4096) androidEventIds.Remove(androidEventOrder.Dequeue());
                return true;
            }
        }

        private bool Write(string level, string eventName, (string Key, object Value)[] fields)
        {
            lock (sync)
            {
                try
                {
                    if (disposed) return false;
                    EnsureWriter();
                    StringBuilder sb = new StringBuilder(256);
                    sb.Append("{\"ts\":\"");
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    sb.Append("\",\"source\":\"").Append(eventName.StartsWith("Android.", StringComparison.Ordinal) ? "Android" : "Windows");
                    sb.Append("\",\"level\":\"").Append(level);
                    sb.Append("\",\"event\":\"").Append(Escape(eventName)).Append('"');
                    if (fields != null)
                    {
                        foreach (var f in fields)
                        {
                            sb.Append(",\"").Append(Escape(f.Key)).Append("\":");
                            AppendValue(sb, f.Value);
                        }
                    }
                    sb.Append('}');
                    string line = sb.ToString();
                    writer.WriteLine(line);
                    writer.Flush();
                    written += Encoding.UTF8.GetByteCount(line) + 2;
                    WriteFailed = false;
                    return true;
                }
                catch
                {
                    WriteFailed = true;
                    CloseWriter();
                    return false;
                }
            }
        }

        private static void AppendValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }

            if (value is int i)
            {
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is uint ui)
            {
                sb.Append(ui.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is long l)
            {
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is double d)
            {
                sb.Append(d.ToString("0.####", CultureInfo.InvariantCulture));
                return;
            }

            if (value is float f)
            {
                sb.Append(f.ToString("0.####", CultureInfo.InvariantCulture));
                return;
            }

            sb.Append('"').Append(Escape(value.ToString())).Append('"');
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        private void EnsureWriter()
        {
            DateTime now = DateTime.Now;
            if (writer != null && openedDay == now.Date && written < MaxBytes)
                return;

            CloseWriter();
            Directory.CreateDirectory(directory);
            string prefix = "sink-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string path = Path.Combine(directory, prefix + ".jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                File.Move(path, Path.Combine(directory, prefix + "-" + now.ToString("HHmmss-fff", CultureInfo.InvariantCulture) + ".jsonl"));

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(stream, new UTF8Encoding(false));
            openedDay = now.Date;
            written = stream.Length;
        }

        private void CleanupExpired()
        {
            lock (sync)
            {
                if (disposed) return;
                DateTime now = DateTime.UtcNow;
                DeleteLogs(file => (now - file.LastWriteTimeUtc).TotalDays >= retainDays);
            }
        }

        private (int Removed, int Failed) DeleteLogs(Func<FileInfo, bool> shouldDelete)
        {
            int removed = 0, failed = 0;
            try
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("sink-*.jsonl"))
                {
                    try
                    {
                        if (!IsLogFile(file) || !shouldDelete(file))
                            continue;
                        file.Delete();
                        removed++;
                    }
                    catch { failed++; }
                }
            }
            catch (DirectoryNotFoundException) { }
            catch { failed++; }
            return (removed, failed);
        }

        private static bool IsLogFile(FileInfo file) =>
            LogFileName.IsMatch(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0;

        private void CloseWriter()
        {
            try { writer?.Dispose(); }
            catch { }
            writer = null;
        }

        public void Dispose()
        {
            cleanupTimer.Dispose();
            lock (sync)
            {
                disposed = true;
                CloseWriter();
            }
        }
    }
}
