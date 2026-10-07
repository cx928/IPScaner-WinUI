using System.Net.NetworkInformation;
using IPScaner.Core.Configuration;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;

namespace IPScaner.Core.Net;

/// <summary>Verdict of a single liveness probe.</summary>
public readonly record struct LivenessResult(
    HostStatus Status,
    LivenessSource Source,
    IPStatus PingStatus,
    long RoundtripMs)
{
    public static LivenessResult Offline(IPStatus status) =>
        new(HostStatus.Offline, LivenessSource.None, status, int.MaxValue);
}

/// <summary>
/// Decides whether a host is reachable.
/// </summary>
/// <remarks>
/// This is the mechanism the v1.28 changelog reworked ("修复部分电脑禁PING导致显示不在线的问题").
/// The evaluation order is preserved exactly from the original
/// <c>FormMain.Ping_PingCompleted</c>:
/// <list type="number">
/// <item>ICMP echo with <see cref="AppConfig.PingTimeout"/>. Success wins outright.</item>
/// <item>If <see cref="AppConfig.ARPInsteadPingEnabled"/>: a populated ARP entry counts as online.</item>
/// <item>If <see cref="AppConfig.PortInsteadPingEnabled"/>: any open port from
/// <see cref="AppConfig.PrePortArray"/> counts as online.</item>
/// <item>Otherwise the host is offline.</item>
/// </list>
/// Note that ARP is consulted <i>before</i> the TCP probe, and that disabling both
/// fallbacks reproduces the old "禁PING = 不在线" behaviour.
/// </remarks>
public sealed class LivenessProbe
{
    private readonly ArpTable _arp;

    public LivenessProbe(ArpTable arp) => _arp = arp;

    /// <summary>Runs the full fallback chain for one address.</summary>
    public async Task<LivenessResult> ProbeAsync(string ipAddress, AppConfig config, CancellationToken ct = default)
    {
        var pingStatus = await PingAsync(ipAddress, config.PingTimeout, ct).ConfigureAwait(false);
        if (pingStatus.Status == IPStatus.Success)
        {
            AppLog.Instance.Log(nameof(LivenessProbe), $"{ipAddress} 在线 (Ping {pingStatus.RoundtripTime}ms)");
            return new LivenessResult(HostStatus.Online, LivenessSource.Icmp, IPStatus.Success, pingStatus.RoundtripTime);
        }

        var ports = config.GetPrePorts();
        var portFallbackAvailable = config.PortInsteadPingEnabled && ports.Count > 0;

        if (config.ARPInsteadPingEnabled)
        {
            if (await _arp.IsOnlineByArpAsync(ipAddress, ct).ConfigureAwait(false))
            {
                AppLog.Instance.Log(nameof(LivenessProbe), $"{ipAddress} 在线 (ARP)");
                return new LivenessResult(HostStatus.Online, LivenessSource.Arp, pingStatus.Status, int.MaxValue);
            }

            if (portFallbackAvailable &&
                await TcpProbe.AnyPortOpenAsync(ipAddress, ports, config.PortTimeout, null, ct).ConfigureAwait(false))
            {
                AppLog.Instance.Log(nameof(LivenessProbe), $"{ipAddress} 在线 (TCP)");
                return new LivenessResult(HostStatus.Online, LivenessSource.TcpPort, pingStatus.Status, int.MaxValue);
            }

            return LivenessResult.Offline(pingStatus.Status);
        }

        if (portFallbackAvailable &&
            await TcpProbe.AnyPortOpenAsync(ipAddress, ports, config.PortTimeout, null, ct).ConfigureAwait(false))
        {
            AppLog.Instance.Log(nameof(LivenessProbe), $"{ipAddress} 在线 (TCP)");
            return new LivenessResult(HostStatus.Online, LivenessSource.TcpPort, pingStatus.Status, int.MaxValue);
        }

        return LivenessResult.Offline(pingStatus.Status);
    }

    /// <summary>
    /// Sends a single ICMP echo. Never throws: an exception is reported as
    /// <see cref="IPStatus.Unknown"/> so the caller can continue the sweep.
    /// </summary>
    public static async Task<(IPStatus Status, long RoundtripTime)> PingAsync(
        string ipAddress, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ipAddress, timeoutMs).WaitAsync(ct).ConfigureAwait(false);
            return (reply.Status, reply.Status == IPStatus.Success ? reply.RoundtripTime : -1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(LivenessProbe), $"Ping {ipAddress} 异常: {ex.Message}");
            return (IPStatus.Unknown, -1);
        }
    }
}
