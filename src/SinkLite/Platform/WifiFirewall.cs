using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SinkLite.Platform
{
    internal sealed class FirewallStatus
    {
        public bool Available;
        public bool RuleExists;
        public bool RuleReady;
        public bool PrivateActive;
        public bool PrivateFirewallEnabled;
        public bool InboundBlocked;
        public int ModifyState;
        public string Error;
    }

    internal sealed class FirewallResult
    {
        public FirewallStatus Status;
        public string Message;
    }

    /// <summary>Explicit, per-executable LAN exception. Never changes firewall profiles or other rules.</summary>
    internal static class WifiFirewall
    {
        internal const string HelperArgument = "--configure-wifi-firewall";
        private static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");

        internal static string RuleName(string executable)
        {
            string path = Path.GetFullPath(executable).ToUpperInvariant();
            return "BlueAudioSink.WiFi." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).Substring(0, 16);
        }

        internal static string Ports(int audioPort)
        {
            if (audioPort < 1024 || audioPort > 65535) throw new ArgumentOutOfRangeException(nameof(audioPort));
            return audioPort == 45678 ? "45678" : "45678," + audioPort.ToString(CultureInfo.InvariantCulture);
        }

        // Paths are data encoded inside a fixed script, never PowerShell source or shell arguments.
        private static string PsString(string value) => "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";

        internal static string BuildScript(string executable, int audioPort, string operation)
        {
            if (operation != "query" && operation != "enable" && operation != "disable")
                throw new ArgumentException("Unknown firewall operation", nameof(operation));
            string ports = Ports(audioPort);
            string script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue';\n" +
                "$application=" + PsString(Path.GetFullPath(executable)) + ";\n" +
                "$name=" + PsString(RuleName(executable)) + "; $ports='" + ports + "';\n" +
                "$policy=New-Object -ComObject HNetCfg.FwPolicy2;\n";
            if (operation == "enable")
                script += @"
foreach ($protocol in @(17,6)) {
$rule=New-Object -ComObject HNetCfg.FWRule;
$rule.Name=if ($protocol -eq 17) { $name } else { $name+'.TCP' };
$rule.Description='BlueAudioSink BAS2; current executable; Private/Public; LocalSubnet only.';
$rule.ApplicationName=$application; $rule.Protocol=$protocol; $rule.LocalPorts=$ports;
$rule.RemoteAddresses='LocalSubnet'; $rule.Direction=1; $rule.Profiles=6;
$rule.InterfaceTypes='All'; $rule.EdgeTraversal=$false; $rule.Action=1; $rule.Enabled=$true;
$rule.Grouping='BlueAudioSink Wi-Fi'; $policy.Rules.Add($rule);
}
";
            else if (operation == "disable")
                script += @"
foreach ($ruleName in @($name,($name+'.TCP'))) {
$rule=$null; try { $rule=$policy.Rules.Item($ruleName) } catch { if ($_.Exception.HResult -ne -2147024894) { throw } }
if ($null -ne $rule) { $policy.Rules.Remove($ruleName) }
}
";
            script += @"
$exists=$false; $ready=$true;
$wantedPorts=($ports.Split(',') | Sort-Object) -join ',';
foreach ($protocol in @(17,6)) {
$ruleName=if ($protocol -eq 17) { $name } else { $name+'.TCP' };
$rule=$null; try { $rule=$policy.Rules.Item($ruleName) } catch { if ($_.Exception.HResult -ne -2147024894) { throw } }
$present=$null -ne $rule; $exists=$exists -or $present;
$actualPorts=if ($present) { ($rule.LocalPorts -split ',' | ForEach-Object { $_.Trim() } | Sort-Object) -join ',' } else { '' };
$ready=$ready -and $present -and $rule.Enabled -and $rule.ApplicationName -eq $application -and $rule.Protocol -eq $protocol -and $actualPorts -eq $wantedPorts -and $rule.RemoteAddresses -eq 'LocalSubnet' -and $rule.Direction -eq 1 -and $rule.Profiles -eq 6 -and $rule.Action -eq 1 -and !$rule.EdgeTraversal;
}
$private=($policy.CurrentProfileTypes -band 6) -ne 0;
$enabled=(($policy.CurrentProfileTypes -band 2) -ne 0 -and $policy.FirewallEnabled(2)) -or (($policy.CurrentProfileTypes -band 4) -ne 0 -and $policy.FirewallEnabled(4));
$blocked=(($policy.CurrentProfileTypes -band 2) -ne 0 -and $policy.BlockAllInboundTraffic(2)) -or (($policy.CurrentProfileTypes -band 4) -ne 0 -and $policy.BlockAllInboundTraffic(4));
Write-Output ('BASFW1|{0}|{1}|{2}|{3}|{4}|{5}' -f [int]$exists,[int]$ready,[int]$private,[int]$enabled,[int]$blocked,[int]$policy.LocalPolicyModifyState);
";
            return script;
        }

        internal static FirewallStatus ParseStatus(string output)
        {
            foreach (string line in (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Trim().Split('|');
                if (fields.Length != 7 || fields[0] != "BASFW1") continue;
                bool valid = true;
                for (int i = 1; i <= 5; i++) valid &= fields[i] == "0" || fields[i] == "1";
                if (!valid || !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int state)) continue;
                return new FirewallStatus { Available = true, RuleExists = fields[1] == "1", RuleReady = fields[2] == "1",
                    PrivateActive = fields[3] == "1", PrivateFirewallEnabled = fields[4] == "1", InboundBlocked = fields[5] == "1", ModifyState = state };
            }
            return new FirewallStatus { Error = "无法读取防火墙状态，请稍后重新检测。" };
        }

        private static FirewallStatus Run(string executable, int audioPort, string operation)
        {
            try
            {
                var start = new ProcessStartInfo(PowerShellPath) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-EncodedCommand");
                start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildScript(executable, audioPort, operation))));
                using (Process process = Process.Start(start))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(15000))
                    {
                        process.Kill(true);
                        return new FirewallStatus { Error = "防火墙操作超时，请重新检测。" };
                    }
                    string output = stdout.GetAwaiter().GetResult();
                    stderr.GetAwaiter().GetResult();
                    return process.ExitCode == 0 ? ParseStatus(output) : new FirewallStatus { Error = "无法访问防火墙服务，可能受权限或系统策略限制。" };
                }
            }
            catch (Exception) { return new FirewallStatus { Error = "无法访问 Windows 防火墙，请检查系统服务或管理策略。" }; }
        }

        public static FirewallStatus Query(int audioPort) => Run(Environment.ProcessPath, audioPort, "query");

        // Called before single-instance handling. No audio initialization and no caller-controlled executable path.
        public static int RunHelper(string operation, int audioPort)
        {
            if (operation != "enable" && operation != "disable" || audioPort < 1024 || audioPort > 65535) return 2;
            FirewallStatus status = Run(Environment.ProcessPath, audioPort, operation);
            return status.Available && (operation == "enable" ? status.RuleReady : !status.RuleExists) ? 0 : 1;
        }

        public static FirewallResult RequestChange(bool enable, int audioPort, IntPtr owner)
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden, ErrorDialog = false, ErrorDialogParentHandle = owner };
                start.ArgumentList.Add(HelperArgument); start.ArgumentList.Add(enable ? "enable" : "disable");
                start.ArgumentList.Add(audioPort.ToString(CultureInfo.InvariantCulture));
                using (Process process = Process.Start(start))
                {
                    if (process == null || !process.WaitForExit(60000))
                        return new FirewallResult { Message = "配置尚未完成，请稍后重新检测。" };
                    FirewallStatus status = Query(audioPort);
                    bool success = process.ExitCode == 0 && status.Available && (enable ? status.RuleReady : !status.RuleExists);
                    return new FirewallResult { Status = status, Message = success
                        ? enable ? "本程序的局域网放行规则已开启。" : "本程序的放行规则已移除。"
                        : "操作未完成，请检查管理员权限或系统策略。" };
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { return new FirewallResult { Message = "已取消管理员授权，规则未更改。" }; }
            catch (Exception) { return new FirewallResult { Message = "无法启动配置，请检查管理员权限或系统策略。" }; }
        }
    }
}
