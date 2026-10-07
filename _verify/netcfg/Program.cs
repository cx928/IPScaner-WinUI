using System.Diagnostics;
using System.Reflection;
using System.Text;
using IPScaner.Core.Models;
using IPScaner.Core.Net;
using IPScaner.Core.Storage;

namespace VerifyNetCfg;

/// <summary>
/// Scratch harness for WifiService / NetworkConfigurator / NetworkHistory.
/// Safety: it never applies a network change — every NetworkConfigurator call either
/// fails validation before spawning, or targets a deliberately non-existent adapter name.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        await CheckWifiAsync();
        CheckParsers();
        CheckHistory();
        await CheckConfiguratorAsync();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ---- 1. WifiService (read-only) ----------------------------------------

    private static async Task CheckWifiAsync()
    {
        Section("1. WifiService.QueryAsync()  [read-only]");
        var sw = Stopwatch.StartNew();
        var profiles = await new WifiService().QueryAsync();
        sw.Stop();

        Info($"SSID count = {profiles.Count}   ({sw.ElapsedMilliseconds} ms)");
        foreach (var p in profiles.Take(5))
        {
            // Passwords are masked: only the length is printed.
            Info($"  ssid=\"{p.Ssid}\" pwdLen={p.Password.Length} auth=\"{p.Authentication}\" " +
                 $"cipher=\"{p.Encryption}\" open={p.IsOpen}");
        }

        Check("wifi: call did not throw", true);
        Check("wifi: every profile has a non-empty SSID", profiles.All(p => !string.IsNullOrWhiteSpace(p.Ssid)));
        Check("wifi: SSIDs are unique", profiles.Select(p => p.Ssid).Distinct(StringComparer.Ordinal).Count() == profiles.Count);
        Check("wifi: no replacement characters in SSIDs", profiles.All(p => !p.Ssid.Contains('\uFFFD')));
        Info(profiles.Count > 0
            ? "OK: locale-independent parsing found profiles on this (English-output) netsh."
            : "NOTE: netsh returned no saved profile on this machine.");
    }

    // ---- 2. locale-independence of the parsers (synthetic output) ----------

    private static void CheckParsers()
    {
        Section("2. WifiService parsers against synthetic localized output (via reflection)");

        const string englishList = """

            Profiles on interface WLAN:

            Group policy profiles (read only)
            ---------------------------------
                <None>

            User profiles
            -------------
                All User Profile     : cx-WIFI
                All User Profile     : @Ruijie-sCB1A_5G

            """;

        const string chineseList = """

            接口 WLAN 上的配置文件:

            组策略配置文件(只读)
            ---------------------------------
                <无>

            用户配置文件
            -------------
                所有用户配置文件 : cx-WIFI
                所有用户配置文件 ： 中文SSID-5G

            """;

        var en = ParseNames(englishList);
        var zh = ParseNames(chineseList);
        Info("EN names: " + string.Join(" | ", en));
        Info("ZH names: " + string.Join(" | ", zh));
        Check("names: English listing -> 2 profiles", en.Count == 2 && en[0] == "cx-WIFI");
        Check("names: Chinese listing (full-width colon) -> 2 profiles",
            zh.Count == 2 && zh[1] == "中文SSID-5G");
        Check("names: header/footer lines excluded",
            !en.Any(n => n.Contains("interface") || n.Contains("---")) && !zh.Any(n => n.Contains("接口")));

        const string englishKeyed = """
            Profile cx-WIFI on interface WLAN:
            =======================================================================

            Connectivity settings
            -------------------
                SSID name              : "cx-WIFI"
                Authentication         : WPA2-Personal
                Cipher                 : CCMP

            Security settings
            -------------------
                Authentication         : WPA2-Personal
                Cipher                 : CCMP
                Security key           : Present
                Key Content            : Sup3r-Secret!
            """;

        const string englishOpen = """
            Security settings
            -------------------
                Authentication         : Open
                Cipher                 : None
                Security key           : Absent
            """;

        const string chineseKeyed = """
            安全设置
            -------------------
                身份验证               : WPA2 - 个人
                加密                   : CCMP
                安全密钥               : 存在
                关键内容               : 我的密码123
            """;

        const string chinesePresentMarker = """
            安全设置
            -------------------
                身份验证               : 开放式
                加密                   : 无
                安全密钥               : 不存在
                关键内容               : 存在
            """;

        var keyed = ParseDetails(englishKeyed);
        Info($"EN keyed  -> pwdLen={keyed.Password.Length} open={keyed.IsOpen} auth={keyed.Authentication} cipher={keyed.Encryption}");
        Check("key: English Key Content captured",
            keyed.Password == "Sup3r-Secret!" && !keyed.IsOpen && keyed.Authentication == "WPA2-Personal" && keyed.Encryption == "CCMP");

        var open = ParseDetails(englishOpen);
        Check("key: open network -> IsOpen, empty password", open.IsOpen && open.Password.Length == 0);

        var zhKeyed = ParseDetails(chineseKeyed);
        Info($"ZH keyed  -> pwdLen={zhKeyed.Password.Length} open={zhKeyed.IsOpen} auth={zhKeyed.Authentication} cipher={zhKeyed.Encryption}");
        Check("key: Chinese 关键内容 captured",
            zhKeyed.Password == "我的密码123" && !zhKeyed.IsOpen && zhKeyed.Encryption == "CCMP");

        var zhMarker = ParseDetails(chinesePresentMarker);
        Check("key: 关键内容 = 存在 treated as open network", zhMarker.IsOpen && zhMarker.Password.Length == 0);
    }

    // ---- 3. NetworkHistory -------------------------------------------------

    private static void CheckHistory()
    {
        Section("3. NetworkHistory round-trip (temp file)");

        var dir = ResolveScratchDirectory();
        Info($"scratch directory = {dir}");
        var path = Path.Combine(dir, "ipScaner_his.xml");
        if (File.Exists(path)) File.Delete(path);

        var history = new NetworkHistory(path);
        Check("history: FilePath is the supplied path", history.FilePath == path);
        Check("history: default ctor points next to the exe",
            new NetworkHistory().FilePath == Path.Combine(AppContext.BaseDirectory, "ipScaner_his.xml"));

        history.Add(new AdapterInfo { Name = "以太网", IP = "192.168.1.100", SubnetMask = "255.255.255.0", Gateway = "192.168.1.1", Dns = "223.5.5.5" });
        history.Add(new AdapterInfo { Name = "WLAN", IP = "192.168.2.50", SubnetMask = "255.255.255.0", Gateway = "192.168.2.1", Dns = "8.8.8.8,1.1.1.1" });
        history.Add(new AdapterInfo { Name = "以太网", IP = "10.0.0.7", SubnetMask = "255.255.0.0", Gateway = "", Dns = "" });

        var bytes = File.ReadAllBytes(path);
        var xml = Encoding.UTF8.GetString(bytes);
        Info("first 200 chars of the written XML:");
        Console.WriteLine(xml[..Math.Min(200, xml.Length)]);
        Info($"first 3 bytes: {string.Join(" ", bytes.Take(3).Select(b => b.ToString("X2")))}  ({bytes.Length} bytes)");

        var firstLine = xml.Split('\n')[0].TrimEnd('\r');
        Check("xml: declaration is exactly <?xml version=\"1.0\"?>", firstLine == "<?xml version=\"1.0\"?>");
        Check("xml: no encoding attribute / no xsi-xsd namespaces",
            !xml.Contains("encoding=") && !xml.Contains("xmlns"));
        Check("xml: UTF-8 without BOM", !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
        Check("xml: <root><array><AdapterInfo .../></array></root> shape",
            xml.Contains("<root>") && xml.Contains("<array>") && xml.Contains("</array>") && xml.Contains("</root>") &&
            xml.Contains("""<AdapterInfo Name="以太网" IP="192.168.1.100" SubnetMask="255.255.255.0" Gateway="192.168.1.1" DNS="223.5.5.5" />"""));

        var loaded = new NetworkHistory(path).Load();
        Check("history: 3 entries reloaded", loaded.Count == 3);
        Check("history: newest first", loaded[0].IP == "10.0.0.7" && loaded[1].IP == "192.168.2.50");
        Check("history: fields round-trip (Name/IP/Mask/Gateway/DNS)",
            loaded[1].Name == "WLAN" && loaded[1].SubnetMask == "255.255.255.0" &&
            loaded[1].Gateway == "192.168.2.1" && loaded[1].Dns == "8.8.8.8,1.1.1.1" &&
            loaded[1].DnsServers.SequenceEqual(["8.8.8.8", "1.1.1.1"]));
        Check("history: empty gateway/DNS survive", loaded[0].Gateway.Length == 0 && loaded[0].DnsServers.Count == 0);

        // De-duplication on (Name, IP): re-applying updates in place, newest first.
        history.Add(new AdapterInfo { Name = "以太网", IP = "192.168.1.100", SubnetMask = "255.255.255.128", Gateway = "192.168.1.254", Dns = "114.114.114.114" });
        var afterDup = history.Load();
        Check("history: de-duplicated on (Name, IP)",
            afterDup.Count == 3 && afterDup[0].IP == "192.168.1.100" && afterDup[0].SubnetMask == "255.255.255.128" &&
            afterDup[0].Gateway == "192.168.1.254");

        // Legacy file written by the original tool must still load.
        var legacyPath = Path.Combine(dir, "legacy_his.xml");
        File.WriteAllText(legacyPath,
            "<?xml version=\"1.0\"?>\r\n<root>\r\n  <array>\r\n    <AdapterInfo Name=\"以太网\" IP=\"192.168.1.100\" " +
            "SubnetMask=\"255.255.255.0\" Gateway=\"192.168.1.1\" DNS=\"223.5.5.5\" />\r\n  </array>\r\n</root>",
            new UTF8Encoding(false));
        var legacy = new NetworkHistory(legacyPath).Load();
        Check("history: legacy file from the WinForms tool loads",
            legacy.Count == 1 && legacy[0].Name == "以太网" && legacy[0].DnsServers.Single() == "223.5.5.5");

        // Corrupt file -> empty list, no exception.
        var corruptPath = Path.Combine(dir, "corrupt_his.xml");
        File.WriteAllText(corruptPath, "<root><array><AdapterInfo Name=", new UTF8Encoding(false));
        var corrupt = new NetworkHistory(corruptPath);
        var corruptEntries = corrupt.Load();
        Check("history: corrupt file loads as empty without throwing", corruptEntries.Count == 0);
        Check("history: missing file loads as empty", new NetworkHistory(Path.Combine(dir, "nope.xml")).Load().Count == 0);

        // Cap at 100, keeping the newest.
        var capPath = Path.Combine(dir, "cap_his.xml");
        if (File.Exists(capPath)) File.Delete(capPath);
        var capHistory = new NetworkHistory(capPath);
        for (var i = 1; i <= 105; i++)
        {
            capHistory.Add(new AdapterInfo { Name = "以太网", IP = $"10.0.0.{i}", SubnetMask = "255.255.255.0" });
        }
        var capped = capHistory.Load();
        Check("history: capped at 100 entries, newest kept",
            capped.Count == NetworkHistory.MaxEntries && capped[0].IP == "10.0.0.105" && capped[^1].IP == "10.0.0.6");
        Info($"cap file entries = {capped.Count} (max {NetworkHistory.MaxEntries})");

        capHistory.Clear();
        Check("history: Clear() leaves an empty, loadable file",
            File.Exists(capPath) && capHistory.Load().Count == 0);
    }

    // ---- 4. NetworkConfigurator (nothing is applied) -----------------------

    private static async Task CheckConfiguratorAsync()
    {
        Section("4. NetworkConfigurator with invalid input (no configuration is changed)");

        var configurator = new NetworkConfigurator();

        await configurator.SetDnsDhcpAsync(string.Empty); // warm-up: JIT only, rejected by validation

        var before = Process.GetProcessesByName("netsh").Length;
        var sw = Stopwatch.StartNew();
        var result = await configurator.SetStaticAsync("nonexistent-adapter", "not-an-ip", "255.255.255.0", null);
        sw.Stop();
        var after = Process.GetProcessesByName("netsh").Length;

        Info($"SetStaticAsync(\"nonexistent-adapter\", \"not-an-ip\", \"255.255.255.0\", null)");
        Info($"  -> Success={result.Success} ExitCode={result.ExitCode} Message=\"{result.Message}\"");
        Info($"  -> elapsed {sw.Elapsed.TotalMilliseconds:0.0} ms, netsh processes {before} -> {after}");

        Check("invalid: Success == false", !result.Success);
        Check("invalid: message names the bad address", result.Message.Contains("not-an-ip") && result.Message.Contains("IP地址"));
        Check("invalid: ExitCode == NotRunExitCode (no process spawned)",
            result.ExitCode == NetworkConfigurator.NotRunExitCode);
        Check("invalid: returned in under 25 ms (no process launch)", sw.Elapsed.TotalMilliseconds < 25);
        Check("invalid: no netsh process appeared", after <= before);

        var gw = await configurator.SetStaticAsync("以太网", "192.168.1.5", "255.255.255.0", "999.1.1.1");
        Info($"  bad gateway   -> Success={gw.Success} Message=\"{gw.Message}\"");
        Check("invalid: bad gateway rejected without spawning",
            !gw.Success && gw.ExitCode == NetworkConfigurator.NotRunExitCode && gw.Message.Contains("999.1.1.1"));

        var mask = await configurator.SetStaticAsync("以太网", "192.168.1.5", "255.255.300.0", null);
        Check("invalid: bad mask rejected", !mask.Success && mask.Message.Contains("子网掩码"));

        var noName = await configurator.SetStaticAsync("   ", "192.168.1.5", "255.255.255.0", null);
        Check("invalid: empty adapter name rejected", !noName.Success && noName.Message.Contains("网卡名称"));

        var noDns = await configurator.SetDnsStaticAsync("以太网", []);
        Check("invalid: empty DNS list rejected", !noDns.Success && noDns.Message.Contains("DNS"));

        var badDns = await configurator.SetDnsStaticAsync("以太网", ["8.8.8.8", "nope"]);
        Check("invalid: bad secondary DNS rejected before spawning",
            !badDns.Success && badDns.ExitCode == NetworkConfigurator.NotRunExitCode && badDns.Message.Contains("nope"));

        // Exercises the real spawn + wait + exit-code path against an adapter name that
        // cannot exist, so netsh fails without touching this machine's configuration.
        Section("4b. spawn/exit-code plumbing probe (adapter name guaranteed not to exist)");
        var fake = $"IPScanerVerify-NoSuchAdapter-{Guid.NewGuid():N}";
        var probe = await configurator.SetDhcpAsync(fake);
        Info($"SetDhcpAsync(\"{fake}\")");
        Info($"  -> Success={probe.Success} ExitCode={probe.ExitCode} Message=\"{probe.Message}\"");
        Check("probe: real netsh failure reported as Success == false", !probe.Success);
        Check("probe: a real exit code was captured (not -1)", probe.ExitCode != NetworkConfigurator.NotRunExitCode);
        Check("probe: netsh's own text is in the message", probe.Message.Contains("netsh"));

        Section("4c. netsh console-encoding decoding (via reflection)");
        const string realText = "文件名、目录名或卷标语法不正确。";
        var utf8Bytes = Encoding.UTF8.GetBytes(realText);
        var gbkBytes = TextFileEncoding.Gbk.GetBytes(realText);
        Info($"UTF-8 bytes -> {DecodeNetshBytes(utf8Bytes)}");
        Info($"GBK   bytes -> {DecodeNetshBytes(gbkBytes)}");
        Check("encoding: UTF-8 console bytes decode correctly", DecodeNetshBytes(utf8Bytes) == realText);
        Check("encoding: GBK console bytes decode correctly", DecodeNetshBytes(gbkBytes) == realText);
        Check("encoding: ASCII unchanged", DecodeNetshBytes(Encoding.ASCII.GetBytes("The parameter is incorrect.")) == "The parameter is incorrect.");
        Check("encoding: empty input", DecodeNetshBytes([]) == string.Empty);
    }

    // ---- scratch space -----------------------------------------------------

    /// <summary>
    /// Picks a writable throwaway directory: the temp path first, then a scratch
    /// folder beside the test binary (some sandboxes deny creating directories in
    /// the shared temp folder while still allowing new files).
    /// </summary>
    private static string ResolveScratchDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Path.GetTempPath(), "ipscaner-verify"),
            Path.Combine(AppContext.BaseDirectory, "scratch"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, "probe.tmp");
                File.WriteAllText(probe, "ok", new UTF8Encoding(false));
                File.Delete(probe);
                return candidate;
            }
            catch (Exception ex)
            {
                Info($"scratch candidate unusable: {candidate} -> {ex.GetType().Name}: {ex.Message}");
            }
        }

        throw new InvalidOperationException("no writable scratch directory found");
    }

    // ---- reflection into the library's parsers -----------------------------
    private static List<string> ParseNames(string output)
    {
        var method = typeof(WifiService).GetMethod("ParseProfileNames", BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new InvalidOperationException("ParseProfileNames not found");
        return (List<string>)method.Invoke(null, [output])!;
    }

    private sealed record Details(string Password, bool IsOpen, string Authentication, string Encryption);

    private static Details ParseDetails(string output)
    {
        var method = typeof(WifiService).GetMethod("ParseProfileDetails", BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new InvalidOperationException("ParseProfileDetails not found");
        var boxed = method.Invoke(null, [output])!;
        var type = boxed.GetType();
        string Get(string name) => (string)type.GetProperty(name)!.GetValue(boxed)!;
        return new Details(Get("Password"), (bool)type.GetProperty("IsOpen")!.GetValue(boxed)!, Get("Authentication"), Get("Encryption"));
    }

    private static string DecodeNetshBytes(byte[] bytes)
    {
        var method = typeof(NetworkConfigurator).GetMethod("DecodeNetshBytes", BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new InvalidOperationException("DecodeNetshBytes not found");
        return (string)method.Invoke(null, [bytes, TextFileEncoding.Gbk])!;
    }

    // ---- tiny assertion helpers -------------------------------------------

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + title + " ===");
    }

    private static void Info(string message) => Console.WriteLine("    " + message);

    private static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {what}");
    }
}
