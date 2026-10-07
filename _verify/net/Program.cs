using System.Diagnostics;
using System.Net.NetworkInformation;
using IPScaner.Core.Configuration;
using IPScaner.Core.Net;

// Headless check of the liveness chain — no GUI, no windows.
//
// Two things need answering:
//   1. Does System.Net.NetworkInformation.Ping actually work on this machine, or
//      does every probe fail (which would make the whole scanner useless until the
//      ARP/TCP fallbacks are switched on)?
//   2. Does ArpTable work, or does it crash / hang?

var adapters = new AdapterService().GetAll();
Console.WriteLine($"adapters: {adapters.Count}");
foreach (var a in adapters)
{
    Console.WriteLine($"  {a.Name,-28} {a.IP,-16} mask={a.SubnetMask,-16} gw={a.Gateway,-16} dhcp={a.IsDhcpEnabled} dns={string.Join('/', a.DnsServers)}");
}

var segment = adapters.Count > 0 ? IpMath.GetSegment(adapters[0].IP) : "127.0.0";
Console.WriteLine();

// ---- 1. raw Ping against a couple of targets ---------------------------------
async Task ProbePing(string target)
{
    try
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(target, 1000);
        Console.WriteLine($"  ping {target,-16} -> {reply.Status} ({reply.RoundtripTime}ms)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ping {target,-16} -> EXCEPTION {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine("== raw ICMP ==");
await ProbePing("127.0.0.1");
await ProbePing(adapters.Count > 0 ? adapters[0].Gateway : "192.168.1.1");
if (adapters.Count > 0) await ProbePing(adapters[0].IP);
Console.WriteLine();

// ---- 2. ArpTable -------------------------------------------------------------
Console.WriteLine("== ArpTable ==");
var sw = Stopwatch.StartNew();
try
{
    var arp = new ArpTable(() => adapters);
    var snapshot = await arp.GetSnapshotAsync(force: true);
    sw.Stop();
    Console.WriteLine($"  snapshot: {snapshot.Count} entries in {sw.ElapsedMilliseconds}ms");
    foreach (var kv in snapshot.Take(8)) Console.WriteLine($"    {kv.Key,-16} {kv.Value}");

    // The local adapter path must answer without touching the ARP cache.
    if (adapters.Count > 0)
    {
        var local = await arp.GetMacAsync(adapters[0].IP);
        Console.WriteLine($"  local adapter MAC for {adapters[0].IP}: {local ?? "<null>"}");
    }
}
catch (Exception ex)
{
    sw.Stop();
    Console.WriteLine($"  ArpTable CRASHED after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}
Console.WriteLine();

// ---- 3. full liveness chain, both fallback configurations --------------------
var liveness = new LivenessProbe(new ArpTable(() => adapters));
var targets = new List<string> { "127.0.0.1" };
if (adapters.Count > 0)
{
    targets.Add(adapters[0].IP);
    if (!string.IsNullOrEmpty(adapters[0].Gateway)) targets.Add(adapters[0].Gateway);
    targets.Add($"{segment}.254");
}

async Task RunChain(string label, AppConfig cfg)
{
    Console.WriteLine($"== liveness: {label} ==");
    foreach (var t in targets)
    {
        var r = await liveness.ProbeAsync(t, cfg);
        Console.WriteLine($"  {t,-16} -> {r.Status,-8} via {r.Source,-8} icmp={r.PingStatus} rtt={r.RoundtripMs}");
    }
    Console.WriteLine();
}

await RunChain("defaults (no fallbacks)", new AppConfig());
await RunChain("TCP fallback on", new AppConfig { PortInsteadPingEnabled = true, PortTimeout = 50 });
await RunChain("ARP fallback on", new AppConfig { ARPInsteadPingEnabled = true });

Console.WriteLine("done");
return 0;
