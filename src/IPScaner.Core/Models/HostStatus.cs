namespace IPScaner.Core.Models;

/// <summary>Reachability state of a host, using the original tool's vocabulary.</summary>
public enum HostStatus
{
    /// <summary>待检测 — not probed yet.</summary>
    Pending = 0,

    /// <summary>正常 — confirmed reachable.</summary>
    Online = 1,

    /// <summary>不通 — all probes failed.</summary>
    Offline = 2,
}

/// <summary>
/// How a host was determined to be online. Surfaced in the UI so the operator can
/// tell a real ICMP answer apart from an ARP-cache hit or an open TCP port — the
/// distinction the v1.28 changelog was about.
/// </summary>
public enum LivenessSource
{
    None = 0,
    Icmp = 1,
    Arp = 2,
    TcpPort = 3,
}

public static class HostStatusText
{
    /// <summary>短标签, used on the colour blocks and in legends.</summary>
    public static string Short(HostStatus s) => s switch
    {
        HostStatus.Online => "正常",
        HostStatus.Offline => "不通",
        _ => "待检测",
    };

    /// <summary>Batch-scan grid status column: "OK" / "NG".</summary>
    public static string Code(HostStatus s) => s switch
    {
        HostStatus.Online => "OK",
        HostStatus.Offline => "NG",
        _ => "待检测",
    };

    public static string Source(LivenessSource s) => s switch
    {
        LivenessSource.Icmp => "Ping",
        LivenessSource.Arp => "ARP",
        LivenessSource.TcpPort => "TCP",
        _ => string.Empty,
    };
}
