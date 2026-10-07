using System.Diagnostics;
using System.Text.RegularExpressions;
using IPScaner.Core.Caching;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;

namespace IPScaner.Core.Net;

/// <summary>
/// Reads MAC addresses out of the Windows ARP cache.
/// </summary>
/// <remarks>
/// Behaviourally equivalent to the original <c>Utility.GetMacAddressFromARP</c>:
/// a host that is one of our own adapter addresses is answered from the adapter
/// list, otherwise the address is looked up in the ARP table, and the table is
/// flushed with <c>arp -d *</c> at most once per hour so stale entries do not
/// masquerade as live hosts.
/// <para>
/// One important difference: the original spawned a fresh <c>arp -a</c> process
/// <i>for every host</i>, so a /24 sweep with name lookup enabled launched ~254
/// processes. This class snapshots the whole table once and reuses it for a short
/// window, which yields identical results far faster.
/// </para>
/// </remarks>
public sealed partial class ArpTable
{
    [GeneratedRegex(@"^\s*(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\s+([0-9A-Fa-f]{2}(?:[-:][0-9A-Fa-f]{2}){5})\s*",
        RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex ArpRowRegex();

    [GeneratedRegex(@"^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$", RegexOptions.Compiled)]
    private static partial Regex MacOnlyRegex();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _snapshotTtl;
    private readonly Func<IReadOnlyList<AdapterInfo>>? _adapterProvider;
    private readonly TimeProvider _clock;

    private Dictionary<string, string> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _snapshotAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;

    public ArpTable(
        Func<IReadOnlyList<AdapterInfo>>? adapterProvider = null,
        TimeSpan? snapshotTtl = null,
        TimeProvider? clock = null)
    {
        _adapterProvider = adapterProvider;
        _snapshotTtl = snapshotTtl ?? TimeSpan.FromSeconds(3);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Resolves the MAC for an address, or null when it is not in the ARP cache.
    /// Results are memoised for an hour, matching the original's cache policy.
    /// </summary>
    public async Task<string?> GetMacAsync(string ipAddress, CancellationToken ct = default)
    {
        // 1. Our own adapters answer instantly and authoritatively.
        var local = TryLocalAdapterMac(ipAddress);
        if (local is not null)
        {
            AppLog.Instance.Log(nameof(ArpTable), $"从本地适配器中获取{ipAddress}对应的MAC【{local}】");
            return local;
        }

        // 2. Hour-long cache.
        if (ScanCaches.MacAddresses.TryGet(ipAddress, out var cached)) return cached;

        // 3. Consult the ARP table snapshot.
        await FlushAtMostHourlyAsync(ct).ConfigureAwait(false);
        var table = await GetSnapshotAsync(force: false, ct).ConfigureAwait(false);
        if (table.TryGetValue(ipAddress, out var mac))
        {
            var normalized = mac.ToUpperInvariant();
            ScanCaches.MacAddresses.Set(ipAddress, normalized);
            AppLog.Instance.Log(nameof(ArpTable), $"从ARP表中获取{ipAddress}对应的MAC【{normalized}】");
            return normalized;
        }

        return null;
    }

    /// <summary>True when the ARP cache knows the address — the v1.28 liveness fallback.</summary>
    public async Task<bool> IsOnlineByArpAsync(string ipAddress, CancellationToken ct = default) =>
        !string.IsNullOrEmpty(await GetMacAsync(ipAddress, ct).ConfigureAwait(false));

    /// <summary>
    /// Returns the whole IP → MAC map, running <c>arp -a</c> when the snapshot has
    /// expired. Concurrent callers share one snapshot.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetSnapshotAsync(bool force, CancellationToken ct = default)
    {
        if (!force && _clock.GetUtcNow() - _snapshotAt < _snapshotTtl) return _snapshot;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _clock.GetUtcNow() - _snapshotAt < _snapshotTtl) return _snapshot;
            _snapshot = await ReadArpTableAsync(ct).ConfigureAwait(false);
            _snapshotAt = _clock.GetUtcNow();
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the snapshot and the per-address cache.</summary>
    public void Invalidate()
    {
        _snapshotAt = DateTimeOffset.MinValue;
        _snapshot.Clear();
        ScanCaches.MacAddresses.Clear();
    }

    /// <summary>Runs <c>arp -d *</c>, but no more than once an hour like the original.</summary>
    private async Task FlushAtMostHourlyAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (now - _lastFlush < TimeSpan.FromHours(1)) return;
        _lastFlush = now;
        try
        {
            await RunProcessAsync("arp", "-d *", ct).ConfigureAwait(false);
            _snapshotAt = DateTimeOffset.MinValue;
        }
        catch
        {
            // Flushing needs elevation in some configurations; a failure just
            // means we read a staler table.
        }
    }

    private async Task<Dictionary<string, string>> ReadArpTableAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string output;
        try
        {
            output = await RunProcessAsync("arp", "-a", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ArpTable), "读取ARP表失败: " + ex.Message);
            return map;
        }

        foreach (Match m in ArpRowRegex().Matches(output))
        {
            var ip = m.Groups[1].Value;
            var mac = m.Groups[2].Value;
            if (!MacOnlyRegex().IsMatch(mac)) continue;
            map[ip] = mac;
        }

        return map;
    }

    private string? TryLocalAdapterMac(string ipAddress)
    {
        if (_adapterProvider is null) return null;
        try
        {
            var match = _adapterProvider().FirstOrDefault(a =>
                string.Equals(a.IP, ipAddress, StringComparison.OrdinalIgnoreCase));
            if (match is null || string.IsNullOrEmpty(match.Mac)) return null;
            return match.Mac.Replace(':', '-');
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                // stderr must be captured too: `arp -d *` prints "拒绝访问" when the
                // process is not elevated, and an uncaptured stream writes that
                // straight onto the parent console.
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.Default,
                StandardErrorEncoding = System.Text.Encoding.Default,
            },
        };

        process.Start();

        // Drain both pipes concurrently; reading them in sequence can deadlock.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var error = stderr.Result;
        if (!string.IsNullOrWhiteSpace(error))
        {
            AppLog.Instance.Log(nameof(ArpTable), $"{fileName} {arguments} -> {error.Trim()}");
        }

        return stdout.Result;
    }
}
