using System.Collections.Concurrent;
using IPScaner.Core.Configuration;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;

namespace IPScaner.Core.Net;

/// <summary>Inputs for one port-scan run.</summary>
public sealed class PortScanRequest
{
    /// <summary>Hosts to scan (addresses or names).</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>Ports to try on each host.</summary>
    public IReadOnlyList<int> Ports { get; init; } = [];

    public int TimeoutMs { get; init; } = 50;

    /// <summary>Concurrent connect attempts. The original allowed 1000 process-wide.</summary>
    public int Concurrency { get; init; } = 256;

    /// <summary>Report closed ports too, not just open ones.</summary>
    public bool ReportClosed { get; init; }

    public int TotalProbes => Targets.Count * Ports.Count;
}

/// <summary>
/// Target port scanner (目标端口扫描).
/// </summary>
/// <remarks>
/// Probe semantics match the original: a plain TCP connect with
/// <see cref="PortScanRequest.TimeoutMs"/>, no retry, and only successfully
/// opened ports are reported by default.
/// <para>
/// Two intentional improvements over the original: port lists accept ranges
/// ("80,443,1000-2000") instead of silently dropping them, and concurrency is a
/// real bounded semaphore rather than 1000 tasks each parking a thread-pool
/// thread inside a blocking wait.
/// </para>
/// </remarks>
public sealed class PortScanner
{
    /// <summary>
    /// Parses a port list. Accepts comma/、/space separators, ranges with '-',
    /// and the keyword "all" (or 全部) for 1-65535.
    /// </summary>
    public static List<int> ParsePorts(string? text, bool allowAll = true)
    {
        var ports = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(text)) return [];

        var normalized = text.Replace('，', ',').Replace('、', ',').Replace(' ', ',').Replace(';', ',');
        foreach (var token in normalized.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            if (t.Length == 0) continue;

            if (allowAll && (t.Equals("all", StringComparison.OrdinalIgnoreCase) || t == "全部" || t == "全部端口"))
            {
                return [.. Enumerable.Range(1, 65535)];
            }

            var dash = t.IndexOf('-');
            if (dash > 0)
            {
                var loText = t[..dash].Trim();
                var hiText = t[(dash + 1)..].Trim();
                if (int.TryParse(loText, out var lo) && int.TryParse(hiText, out var hi))
                {
                    if (lo > hi) (lo, hi) = (hi, lo);
                    lo = Math.Max(1, lo);
                    hi = Math.Min(65535, hi);
                    // Cap a single range so "1-65535" cannot be typed by accident
                    // into a 254-host scan without the caller realising.
                    if (hi - lo > 20000) hi = lo + 20000;
                    for (var p = lo; p <= hi; p++) ports.Add(p);
                }
                continue;
            }

            if (int.TryParse(t, out var port) && port is >= 1 and <= 65535) ports.Add(port);
        }

        return [.. ports];
    }

    /// <summary>Parses the host list: a segment, a range, a CIDR, or a single address.</summary>
    public static List<string> ParseTargets(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var t = text.Trim();

        // "192.168.1.1-192.168.1.50" or "192.168.1.1-50"
        var dash = t.IndexOf('-');
        if (dash > 0)
        {
            var left = t[..dash].Trim();
            var right = t[(dash + 1)..].Trim();
            if (IpMath.IsValidIPv4(left))
            {
                var end = right.Contains('.')
                    ? right
                    : IpMath.GetSegment(left) + "." + right;
                if (IpMath.IsValidIPv4(end)) return IpMath.GetRange(left, end, skipNetworkAndBroadcast: false);
            }
        }

        // "192.168.1.0/24"
        var slash = t.IndexOf('/');
        if (slash > 0)
        {
            var ipPart = t[..slash].Trim();
            var bits = SubnetCalculator.BitsFromMask(t[(slash + 1)..].Trim());
            if (bits >= 0 && IpMath.IsValidIPv4(ipPart)) return SubnetCalculator.HostsForMask(ipPart, bits);
        }

        // "192.168.1" -> .1 .. .254
        if (IpMath.IsValidSegment(t)) return IpMath.GetSegmentHosts(t);

        if (IpMath.IsValidIPv4(t)) return [t];

        return result;
    }

    /// <summary>
    /// Runs a scan, invoking <paramref name="onResult"/> for every probe the
    /// caller asked to see and reporting progress as pairs complete.
    /// </summary>
    public async Task RunAsync(
        PortScanRequest request,
        Func<PortScanResult, Task> onResult,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onResult);

        var total = request.TotalProbes;
        if (total == 0) return;

        var completed = 0;
        var gate = new SemaphoreSlim(Math.Max(1, request.Concurrency));
        var results = new ConcurrentBag<PortScanResult>();

        var tasks = new List<Task>(total);
        foreach (var ip in request.Targets)
        {
            foreach (var port in request.Ports)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var elapsed = await TcpProbe.MeasureAsync(ip, port, request.TimeoutMs, ct).ConfigureAwait(false);
                        var result = new PortScanResult
                        {
                            IP = ip,
                            Port = port,
                            IsOpen = elapsed >= 0,
                            ElapsedMs = elapsed,
                            Service = ServiceNames.Describe(port),
                        };

                        results.Add(result);
                        if (result.IsOpen || request.ReportClosed) await onResult(result).ConfigureAwait(false);

                        var done = Interlocked.Increment(ref completed);
                        progress?.Report(new ScanProgress(done, total, $"{ip}:{port}"));
                    }
                    catch (OperationCanceledException)
                    {
                        // cancelled — stop quietly
                    }
                    catch (Exception ex)
                    {
                        AppLog.Instance.Log(nameof(PortScanner), $"{ip}:{port} 扫描失败: {ex.Message}");
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct));
            }
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
    }
}
