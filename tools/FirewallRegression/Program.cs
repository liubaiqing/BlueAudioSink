using System.Diagnostics;
using System.Text;
using SinkLite.Platform;

class Program
{
    static void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    static void Main()
    {
        int count = 0;
        void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); count++; }
        string app = @"C:\Audio Test\中文';$(throw 'injected')\BlueAudioSink.exe";
        Test("Path identity is stable, case insensitive, and different per install", () => {
            Assert(WifiFirewall.RuleName(app) == WifiFirewall.RuleName(app.ToUpperInvariant()), "Unstable identity");
            Assert(WifiFirewall.RuleName(app) != WifiFirewall.RuleName(app + ".copy"), "Path collision");
        });
        Test("Only valid specific BAS2 ports accepted", () => {
            Assert(WifiFirewall.Ports(45679) == "45678,45679" && WifiFirewall.Ports(45678) == "45678", "Port construction");
            foreach (int invalid in new[] { -1, 0, 80, 65536 }) {
                try { WifiFirewall.Ports(invalid); throw new Exception("Accepted invalid port"); }
                catch (ArgumentOutOfRangeException) { }
            }
            Assert(WifiFirewall.RunHelper("query", 45679) == 2 && WifiFirewall.RunHelper("enable", 0) == 2, "Helper accepts invalid operation");
        });
        Test("Read-only script cannot change rules or global firewall", () => {
            string query = WifiFirewall.BuildScript(app, 45679, "query");
            Assert(!query.Contains(".Add(") && !query.Contains(".Remove(") && !query.Contains("=New-Object -ComObject HNetCfg.FWRule"), "Query mutates rules");
            Assert(!query.Contains(app) && query.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(app))), "Executable path became PowerShell source");
        });
        Test("Malformed or incomplete responses never report enabled", () => {
            foreach (string bad in new[] { "", "BASFW1|1", "BASFW1|yes|1|1|1|0|0", "other|1|1|1|1|0|0" })
                Assert(!WifiFirewall.ParseStatus(bad).Available, "Malformed response accepted");
            var s = WifiFirewall.ParseStatus("BASFW1|1|1|0|1|0|1");
            Assert(s.Available && s.RuleReady && !s.PrivateActive && s.ModifyState == 1, "Status flags lost");
        });
        Test("Actual PowerShell executes scoped idempotent add/query/remove against isolated fake COM store", () => {
            // The production script runs unchanged except the two COM constructors; no OS writes.
            string mock = @"
class Rules {
  [hashtable]$Items=@{}
  [object] Item([string]$name) { if (!$this.Items.ContainsKey($name)) { return $null }; return $this.Items[$name] }
  [void] Add([object]$rule) { $this.Items[$rule.Name]=$rule }
  [void] Remove([string]$name) { $this.Items.Remove($name) }
}
class Policy {
  [Rules]$Rules=[Rules]::new()
  [int]$CurrentProfileTypes=2
  [int]$LocalPolicyModifyState=0
  [bool] FirewallEnabled([int]$profile) { return $true }
  [bool] BlockAllInboundTraffic([int]$profile) { return $false }
}
$global:store=[Policy]::new()
function Get-TestPolicy { return $global:store }
function Get-TestRule { return [pscustomobject]@{ Name='';Description='';ApplicationName='';Protocol=0;LocalPorts='';RemoteAddresses='';Direction=0;Profiles=0;InterfaceTypes='';EdgeTraversal=$true;Action=0;Enabled=$false;Grouping='' } }
";
            string Adapt(string operation, int port = 45679) => WifiFirewall.BuildScript(app, port, operation)
                .Replace("New-Object -ComObject HNetCfg.FwPolicy2", "Get-TestPolicy")
                .Replace("New-Object -ComObject HNetCfg.FWRule", "Get-TestRule");
            string script = mock + Adapt("enable") + Adapt("enable") + @"
if ($global:store.Rules.Items.Count -ne 2) { throw 'Duplicate rules' }
$testRule=$global:store.Rules.Items.Values | Select-Object -First 1
if ($testRule.Profiles -ne 6 -or $testRule.Direction -ne 1 -or $testRule.Protocol -notin @(6,17) -or $testRule.RemoteAddresses -ne 'LocalSubnet' -or $testRule.Action -ne 1 -or $testRule.EdgeTraversal) { throw 'Overbroad rule' }
" + Adapt("enable", 50001) + Adapt("query", 50001) + Adapt("disable", 50001) + Adapt("disable", 50001);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            string scriptFile = Path.Combine(Path.GetTempPath(), "bas2-firewall-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(scriptFile, script, new UTF8Encoding(true));
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptFile }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            Assert(process.WaitForExit(15000), "PowerShell timed out");
            string lines = output.GetAwaiter().GetResult(); string error = errors.GetAwaiter().GetResult(); File.Delete(scriptFile);
            Assert(process.ExitCode == 0, error);
            var states = lines.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(WifiFirewall.ParseStatus).ToArray();
            Assert(states.Length == 6 && states.Take(4).All(s => s.RuleReady) && states.Skip(4).All(s => s.Available && !s.RuleExists), "Idempotence or post-write verification failed: " + lines);
        });
        Test("Real Windows firewall can be queried without elevation or mutation", () => {
            var status = WifiFirewall.Query(45679);
            Assert(status.Available, status.Error);
            Console.WriteLine($"  ruleExists={status.RuleExists}, privateActive={status.PrivateActive}, modifyState={status.ModifyState}");
        });
        Console.WriteLine($"{count} firewall tests passed (no system rules changed)");
    }
}
