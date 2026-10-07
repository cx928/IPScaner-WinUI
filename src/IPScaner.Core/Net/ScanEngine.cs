using IPScaner.Core.Configuration;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;

namespace IPScaner.Core.Net;

/// <summary>Progress snapshot for a running sweep.</summary>
public readonly record struct ScanProgress(int Completed, int Total, string CurrentIP)
{
    public double Fraction => Total <= 0 ? 0 : (double)Completed / Total;
}

/// <summary>
/// Drives a full address sweep: probes every target with bounded concurrency,
/// then enriches the survivors with host name and MAC.
/// </summary>
/// <remarks>
/// The original dispatched one <c>Ping.SendAsync</c> every 10&nbsp;ms with no
/// concurrency ceiling and mutated WinForms controls straight from the callback
/// thread. This engine keeps the same observable results but:
/// <list type="bullet">
/// <item>caps in-flight probes (<see cref="DefaultConcurrency"/>),</item>
/// <item>reports results through <see cref="IProgress{T}"/> so the UI decides how
/// to marshal to its own thread, and</item>
/// <item>honours cancellation promptly.</item>
/// </list>
/// Each host is reported twice when name lookup is on — once as soon as liveness
/// is known (so blocks colour in immediately, as before) and again once the
/// host name and MAC arrive.
/// </remarks>
public sealed class ScanEngine
{
    public const int DefaultConcurrency = 64;

    /// <summary>
    /// Host-name lookups are far more expensive than pings, so they run on a
    /// smaller gate to stop reverse-DNS from starving the probe pool.
    /// </summary>
    public const int DefaultEnrichmentConcurrency = 16;

    private readonly LivenessProbe _probe;
    private readonly NameResolver _names;
    private readonly ArpTable _arp;

    public ScanEngine(LivenessProbe probe, NameResolver names, ArpTable arp)
    {
        _probe = probe;
        _names = names;
        _arp = arp;
    }

    /// <summary>Convenience factory wiring the standard collaborators together.</summary>
    public static ScanEngine CreateDefault(AdapterService? adapters = null)
    {
        var adapterService = adapters ?? new AdapterService();
        var arp = new ArpTable(() => adapterService.GetAll());
        return new ScanEngine(new LivenessProbe(arp), new NameResolver(), arp);
    }

    /// <summary>
    /// Probes a single host and returns an enriched result — the click-a-block
    /// path. Name and MAC are only looked up when the host answered and
    /// <see cref="AppConfig.QueryHostNameEnabled"/> is on, matching a sweep.
    /// </summary>
    public async Task<HostResult> ProbeOnceAsync(string ip, AppConfig config, CancellationToken ct = default)
    {
        var verdict = await _probe.ProbeAsync(ip, config, ct).ConfigureAwait(false);

        var host = new HostResult
        {
            IP = ip,
            LastOctet = IpMath.TryParseOctets(ip, out var o) ? o[3] : 0,
            Status = verdict.Status,
            Source = verdict.Source,
            PingStatus = verdict.PingStatus,
        };
        host.ApplyRoundtrip(verdict.RoundtripMs);

        if (host.Status == HostStatus.Online && config.QueryHostNameEnabled)
        {
            var name = await _names.ResolveAsync(ip, ct).ConfigureAwait(false);
            var mac = await _arp.GetMacAsync(ip, ct).ConfigureAwait(false) ?? string.Empty;
            if (name != NameResolver.Unknown) host.HostName = name;
            host.Mac = mac;
        }

        return host;
    }

    /// <summary>
    /// Probes every address in <paramref name="targets"/>. Results stream through
    /// <paramref name="onResult"/> as they become available.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<string> targets,
        AppConfig config,
        Func<HostResult, Task> onResult,
        IProgress<ScanProgress>? progress = null,
        int concurrency = DefaultConcurrency,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(onResult);

        if (targets.Count == 0) return;

        var total = targets.Count;
        var completed = 0;
        var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var enrichGate = new SemaphoreSlim(DefaultEnrichmentConcurrency);
        var enrichTasks = new List<Task>();
        var resultLock = new object();
        var results = new Dictionary<string, HostResult>(StringComparer.OrdinalIgnoreCase);

        var probeTasks = targets.Select(async ip =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var verdict = await _probe.ProbeAsync(ip, config, ct).ConfigureAwait(false);

                var host = new HostResult
                {
                    IP = ip,
                    LastOctet = IpMath.TryParseOctets(ip, out var o) ? o[3] : 0,
                    Status = verdict.Status,
                    Source = verdict.Source,
                    PingStatus = verdict.PingStatus,
                };
                host.ApplyRoundtrip(verdict.RoundtripMs);

                lock (resultLock) results[ip] = host;

                var done = Interlocked.Increment(ref completed);
                progress?.Report(new ScanProgress(done, total, ip));
                await onResult(host).ConfigureAwait(false);

                if (verdict.Status == HostStatus.Online && config.QueryHostNameEnabled)
                {
                    var enrich = Task.Run(async () =>
                    {
                        await enrichGate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var name = await _names.ResolveAsync(ip, ct).ConfigureAwait(false);
                            var mac = await _arp.GetMacAsync(ip, ct).ConfigureAwait(false) ?? string.Empty;

                            lock (resultLock)
                            {
                                if (!results.TryGetValue(ip, out var current)) return;
                                if (name != NameResolver.Unknown) current.HostName = name;
                                current.Mac = mac;
                            }

                            await onResult(host).ConfigureAwait(false);
                        }
                        finally
                        {
                            enrichGate.Release();
                        }
                    }, ct);

                    lock (resultLock) enrichTasks.Add(enrich);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected; the caller inspects the token.
            }
            catch (Exception ex)
            {
                AppLog.Instance.Log(nameof(ScanEngine), $"扫描 {ip} 出错: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(probeTasks).ConfigureAwait(false);

        // Let pending name/MAC lookups finish so the grid settles before "扫描完毕".
        if (enrichTasks.Count > 0)
        {
            try { await Task.WhenAll(enrichTasks).WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* cancelled */ }
            catch (Exception ex) { AppLog.Instance.Log(nameof(ScanEngine), "补充信息失败: " + ex.Message); }
        }
    }
}
