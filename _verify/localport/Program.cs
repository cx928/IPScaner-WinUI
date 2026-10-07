using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using IPScaner.Core.Models;
using IPScaner.Core.Net;

// Scratch verification harness for IPScaner.Core.Net.LocalPortTable.
// Compiles the real source file against the already-built IPScaner.Core.dll so the
// real IPScaner.Core.csproj is never touched.

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* no console */ }

const string FallbackVariable = "IPSCANER_LOCALPORT_FORCE_FALLBACK";

var failures = 0;
var table = new LocalPortTable();

void Check(bool ok, string label)
{
    Console.WriteLine($"{(ok ? "PASS  " : "FAIL  ")}{label}");
    if (!ok) failures++;
}

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
}

void Stats(string label, IReadOnlyList<LocalPortInfo> rows)
{
    var tcp = rows.Count(r => r.Protocol == "TCP");
    var udp = rows.Count(r => r.Protocol == "UDP");
    var v6 = rows.Count(r => r.LocalAddress.Contains(':'));
    var missingRows = rows.Count(r => r.ProcessMissing);
    var distinctPids = rows.Select(r => r.Pid).Distinct().Count();
    var resolvedPids = rows.Where(r => !r.ProcessMissing).Select(r => r.Pid).Distinct().Count();
    var missingPids = rows.Where(r => r.ProcessMissing).Select(r => r.Pid).Distinct().Count();
    var states = string.Join(", ", rows
        .Where(r => r.Protocol == "TCP")
        .GroupBy(r => r.State)
        .OrderByDescending(g => g.Count())
        .Select(g => $"{g.Key}={g.Count()}"));

    Console.WriteLine($"{label}: rows={rows.Count}  TCP={tcp}  UDP={udp}  IPv4={rows.Count - v6}  IPv6={v6}");
    Console.WriteLine($"    distinct PIDs={distinctPids}   resolved-by-name PIDs={resolvedPids}   unresolved PIDs={missingPids}   rows with ProcessMissing=true: {missingRows}");
    Console.WriteLine($"    TCP states: {states}");
}

void PrintRows(IReadOnlyList<LocalPortInfo> rows, int max)
{
    foreach (var r in rows.Take(max))
    {
        var local = r.LocalAddress.Contains(':')
            ? $"[{r.LocalAddress}]:{r.LocalPort}"
            : $"{r.LocalAddress}:{r.LocalPort}";
        var remote = r.RemoteAddress.Contains(':')
            ? $"[{r.RemoteAddress}]:{r.RemotePort}"
            : $"{r.RemoteAddress}:{r.RemotePort}";
        var missing = r.ProcessMissing ? "  <ProcessMissing>" : string.Empty;
        Console.WriteLine($"    {r.Protocol,-3} {local,-46} {remote,-32} {r.State,-10} pid={r.Pid,-7} {r.ProcessName,-22} {r.ProcessPath}{missing}");
    }
}

Section("environment");
Console.WriteLine($"OS            : {RuntimeInformation.OSDescription}");
Console.WriteLine($"Runtime       : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
Console.WriteLine($"Elevated      : {IsElevated()}");
Console.WriteLine($"OEM codepage  : {CultureInfo.CurrentCulture.TextInfo.OEMCodePage}");

Section("P/Invoke struct sizes (reflection over the private nested structs)");
foreach (var (name, expected) in new[]
         {
             ("MIB_TCPROW_OWNER_PID", 24),
             ("MIB_TCP6ROW_OWNER_PID", 56),
             ("MIB_UDPROW_OWNER_PID", 12),
             ("MIB_UDP6ROW_OWNER_PID", 28),
         })
{
    var type = typeof(LocalPortTable).GetNestedType(name, BindingFlags.NonPublic);
    if (type is null)
    {
        Check(false, $"{name}: nested type not found");
        continue;
    }

    var size = Marshal.SizeOf(type);
    Check(size == expected, $"{name} = {size} bytes (expected {expected})");
}

Section("QueryAsync() — native IP Helper path, TCP + UDP, IPv4 only");
var native = await table.QueryAsync();
Stats("native", native);
Console.WriteLine("    first 15 rows:");
PrintRows(native, 15);

Check(native.Count > 0, "native enumeration returned rows");
Check(native.Count(r => r.Protocol == "TCP" && r.State == "侦听") >= 3,
    $"at least 3 TCP 侦听 (LISTENING) rows (found {native.Count(r => r.Protocol == "TCP" && r.State == "侦听")})");
Check(native.All(r => r.Protocol is "TCP" or "UDP"), "every row carries protocol TCP or UDP");
Check(native.All(r => r.LocalPort is >= 0 and <= 65535), "every local port is in 0..65535");
Check(native.All(r => r.RemotePort is >= 0 and <= 65535), "every remote port is in 0..65535");
Check(native.All(r => r.Pid >= 0), "every PID is non-negative");
Check(native.All(r => !string.IsNullOrEmpty(r.State)), "every row has non-empty state text");
Check(native.Where(r => r.Protocol == "UDP").All(r => r.State == LocalPortInfo.UdpState),
    $"UDP rows use LocalPortInfo.UdpState ('{LocalPortInfo.UdpState}')");
Check(native.All(r => r.LocalAddress.Contains(':') || IsIPv4(r.LocalAddress)), "every IPv4 local address is a dotted quad");
Check(native.Where(r => r.Protocol == "TCP").All(r => r.RemoteAddress.Contains(':') || IsIPv4(r.RemoteAddress)),
    "every TCP remote address is a dotted quad");
Check(native.Where(r => r.Protocol == "UDP").All(r => r.RemoteAddress == "*" && r.RemotePort == 0),
    "every UDP row uses netstat's '*:*' remote endpoint");
Check(native.All(r => r.ProcessMissing || r.ProcessName.Length > 0), "resolved rows have a process name");
Check(native.All(r => !r.ProcessMissing || (r.ProcessName.Length == 0 && r.ProcessPath.Length == 0)),
    "ProcessMissing rows have empty name and path");
Check(IsSorted(native), "rows are sorted by protocol, local port, PID");

var tcpOnly = await table.QueryAsync(includeTcp: true, includeUdp: false);
Check(tcpOnly.Count > 0 && tcpOnly.All(r => r.Protocol == "TCP"), $"includeUdp:false returns only TCP ({tcpOnly.Count} rows)");
var udpOnly = await table.QueryAsync(includeTcp: false, includeUdp: true, includeIpv6: true);
Check(udpOnly.Count > 0 && udpOnly.All(r => r.Protocol == "UDP"), $"includeTcp:false returns only UDP ({udpOnly.Count} rows)");
var nothing = await table.QueryAsync(includeTcp: false, includeUdp: false);
Check(nothing.Count == 0, $"both protocols disabled returns an empty table ({nothing.Count} rows)");

Section("QueryAsync(includeIpv6: true) — IPv6 must not disturb the IPv4 rows");
var withV6 = await table.QueryAsync(includeIpv6: true);
Stats("native+IPv6", withV6);
var v4Subset = withV6.Where(r => !r.LocalAddress.Contains(':')).ToList();
var v6Rows = withV6.Where(r => r.LocalAddress.Contains(':')).ToList();
var netstatAfterV6 = CountNetstatRows();
Console.WriteLine($"    IPv6 rows: {v6Rows.Count}; IPv4 rows with flag on: {v4Subset.Count}, flag off: {native.Count}; fresh netstat IPv4 rows: {netstatAfterV6.Tcp4 + netstatAfterV6.Udp4}");
// The endpoint table is live, so cross-query counts are compared with a small tolerance;
// a "same query vs fresh netstat" comparison is what actually proves IPv4 completeness.
Check(Math.Abs(v4Subset.Count - native.Count) <= 3, $"IPv4 row count unchanged by includeIpv6 (±3: {v4Subset.Count} vs {native.Count})");
Check(Math.Abs(v4Subset.Count - (netstatAfterV6.Tcp4 + netstatAfterV6.Udp4)) <= 6,
    $"IPv4 rows complete when includeIpv6:true (±6 vs fresh netstat: {v4Subset.Count} vs {netstatAfterV6.Tcp4 + netstatAfterV6.Udp4})");
Check(v6Rows.Count > 0, $"includeIpv6:true actually returns IPv6 rows ({v6Rows.Count})");
Check(v6Rows.All(r => IsIPv6(r.LocalAddress)), "every IPv6 local address parses as IPv6");
Check(v6Rows.All(r => r.State.Length > 0), "IPv6 rows carry state text");
Check(IsSorted(withV6), "IPv6-enabled result is still sorted");
Check(native.All(r => !r.LocalAddress.Contains(':')), "includeIpv6:false returns no IPv6 rows");
Console.WriteLine("    first 10 IPv6 rows:");
PrintRows(v6Rows, 10);

Section("independent netstat -ano cross-check (validates the struct sizes / row stride)");
var netstat = CountNetstatRows();
var nativeTcp4 = native.Count(r => r.Protocol == "TCP");
var nativeUdp4 = native.Count(r => r.Protocol == "UDP");
Console.WriteLine($"    netstat: TCP/IPv4={netstat.Tcp4}  UDP/IPv4={netstat.Udp4}  TCP/IPv6={netstat.Tcp6}  UDP/IPv6={netstat.Udp6}");
Console.WriteLine($"    native : TCP/IPv4={nativeTcp4}  UDP/IPv4={nativeUdp4}");
Check(Math.Abs(nativeTcp4 - netstat.Tcp4) <= 5, $"native TCP/IPv4 count within ±5 of netstat ({nativeTcp4} vs {netstat.Tcp4})");
Check(Math.Abs(nativeUdp4 - netstat.Udp4) <= 3, $"native UDP/IPv4 count within ±3 of netstat ({nativeUdp4} vs {netstat.Udp4})");

Section($"fallback path — {FallbackVariable}=1 forces netstat");
Environment.SetEnvironmentVariable(FallbackVariable, "1");
var fallback = await table.QueryAsync();
var fallbackWithV6 = await table.QueryAsync(includeIpv6: true);
Environment.SetEnvironmentVariable(FallbackVariable, null);

Stats("fallback(netstat)", fallback);
Console.WriteLine("    first 15 fallback rows:");
PrintRows(fallback, 15);

var fallbackTcp4 = fallback.Count(r => r.Protocol == "TCP");
var fallbackUdp4 = fallback.Count(r => r.Protocol == "UDP");
var freshNetstat = CountNetstatRows();
Console.WriteLine($"    fresh netstat right after the fallback run: TCP/IPv4={freshNetstat.Tcp4}  UDP/IPv4={freshNetstat.Udp4}");
Check(fallback.Count > 0, "netstat fallback returned rows");
Check(Math.Abs(fallbackTcp4 - freshNetstat.Tcp4) <= 2, $"fallback TCP/IPv4 count within ±2 of netstat ({fallbackTcp4} vs {freshNetstat.Tcp4})");
Check(Math.Abs(fallbackUdp4 - freshNetstat.Udp4) <= 2, $"fallback UDP/IPv4 count within ±2 of netstat ({fallbackUdp4} vs {freshNetstat.Udp4})");
Check(fallback.Count(r => r.Protocol == "TCP" && r.State == "侦听") >= 3,
    $"fallback found TCP 侦听 rows ({fallback.Count(r => r.Protocol == "TCP" && r.State == "侦听")})");
Check(Math.Abs(fallbackTcp4 - nativeTcp4) <= 5, $"fallback TCP/IPv4 count within ±5 of native ({fallbackTcp4} vs {nativeTcp4})");
Check(Math.Abs(fallbackUdp4 - nativeUdp4) <= 3, $"fallback UDP/IPv4 count within ±3 of native ({fallbackUdp4} vs {nativeUdp4})");
Check(fallback.All(r => !r.LocalAddress.Contains(':')), "fallback honours includeIpv6:false");
Check(fallback.All(r => r.LocalPort is >= 0 and <= 65535), "fallback local ports are in range");
Check(fallback.Where(r => r.Protocol == "UDP").All(r => r.State == LocalPortInfo.UdpState), "fallback UDP rows use UdpState");
Check(fallback.All(r => r.ProcessMissing || r.ProcessName.Length > 0), "fallback resolved rows have a process name");
Check(IsSorted(fallback), "fallback rows are sorted");
Check(fallbackWithV6.Count(r => r.LocalAddress.Contains(':')) > 0, "fallback returns IPv6 rows when asked");
Check(fallbackWithV6.All(r => !r.LocalAddress.Contains(':') || IsIPv6(r.LocalAddress)), "fallback IPv6 addresses parse as IPv6");
var fallbackWithV6V4 = fallbackWithV6.Count(r => !r.LocalAddress.Contains(':'));
var freshNetstatV6 = CountNetstatRows();
Check(Math.Abs(fallbackWithV6V4 - freshNetstatV6.Tcp4 - freshNetstatV6.Udp4) <= 6,
    $"fallback keeps its IPv4 rows when includeIpv6:true (±6 vs fresh netstat: {fallbackWithV6V4} vs {freshNetstatV6.Tcp4 + freshNetstatV6.Udp4})");

Section("cancellation");
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    var cancelled = false;
    try
    {
        await table.QueryAsync(true, true, false, cts.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }

    Check(cancelled, "an already-cancelled token throws OperationCanceledException");
}

Environment.SetEnvironmentVariable(FallbackVariable, "1");
using (var ctsMid = new CancellationTokenSource(40))
{
    var cancelledMid = false;
    IReadOnlyList<LocalPortInfo>? midRows = null;
    try
    {
        midRows = await table.QueryAsync(true, true, false, ctsMid.Token);
    }
    catch (OperationCanceledException)
    {
        cancelledMid = true;
    }

    Check(cancelledMid || midRows is not null,
        $"mid-flight cancellation during the netstat fallback settles cleanly (cancelled={cancelledMid}, rows={midRows?.Count})");
}

Environment.SetEnvironmentVariable(FallbackVariable, null);

Section("TryKillProcess");
using (var victim = StartVictim())
{
    var drain = Task.Run(() => { try { victim.StandardOutput.ReadToEnd(); } catch { /* killed */ } });
    var victimPid = victim.Id;
    var killed = LocalPortTable.TryKillProcess(victimPid, out var killError);
    Check(killed, $"TryKillProcess({victimPid}) => true  (error='{killError}')");
    await Task.Delay(300);
    Check(IsGone(victimPid), $"victim process {victimPid} no longer exists");
    _ = drain;
}

var bogusOk = LocalPortTable.TryKillProcess(int.MaxValue, out var bogusError);
Check(!bogusOk && bogusError.Length > 0, $"(nonexistent pid) => false, error='{bogusError}'");
var selfOk = LocalPortTable.TryKillProcess(Environment.ProcessId, out var selfError);
Check(!selfOk && selfError.Length > 0, $"(own pid) => false, error='{selfError}'");
var zeroOk = LocalPortTable.TryKillProcess(0, out var zeroError);
Check(!zeroOk && zeroError.Length > 0, $"(pid 0) => false, error='{zeroError}'");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures;

static bool IsElevated()
{
    try
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    catch
    {
        return false;
    }
}

static bool IsIPv4(string text) =>
    IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

static bool IsIPv6(string text) =>
    IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;

static bool IsSorted(IReadOnlyList<LocalPortInfo> rows)
{
    for (var i = 1; i < rows.Count; i++)
    {
        var a = rows[i - 1];
        var b = rows[i];
        var c = string.CompareOrdinal(a.Protocol, b.Protocol);
        if (c > 0) return false;
        if (c < 0) continue;
        if (a.LocalPort > b.LocalPort) return false;
        if (a.LocalPort == b.LocalPort && a.Pid > b.Pid) return false;
    }

    return true;
}

static Process StartVictim()
{
    var startInfo = new ProcessStartInfo("ping", "-n 120 127.0.0.1")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    return Process.Start(startInfo) ?? throw new InvalidOperationException("cannot start victim process");
}

static bool IsGone(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return process.HasExited;
    }
    catch (ArgumentException)
    {
        return true;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
}

static (int Tcp4, int Udp4, int Tcp6, int Udp6) CountNetstatRows()
{
    var startInfo = new ProcessStartInfo("netstat", "-ano")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("cannot start netstat");
    var text = process.StandardOutput.ReadToEnd();
    process.WaitForExit(15000);

    int tcp4 = 0, udp4 = 0, tcp6 = 0, udp6 = 0;
    foreach (var line in text.Split('\n'))
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) continue;

        var protocol = parts[0].ToUpperInvariant();
        if (protocol is not ("TCP" or "UDP")) continue;

        var isV6 = parts[1].StartsWith('[');
        if (protocol == "TCP")
        {
            if (isV6) tcp6++;
            else tcp4++;
        }
        else
        {
            if (isV6) udp6++;
            else udp4++;
        }
    }

    return (tcp4, udp4, tcp6, udp6);
}
