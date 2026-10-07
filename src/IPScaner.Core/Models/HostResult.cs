using System.Net.NetworkInformation;

namespace IPScaner.Core.Models;

/// <summary>
/// Result of probing one IPv4 host. This is the single record type shared by the
/// main grid, the batch-scan grid and all exports, so every surface reports the
/// same facts.
/// </summary>
public sealed class HostResult
{
    public required string IP { get; init; }

    /// <summary>Final octet, for the 1..254 colour-block grid.</summary>
    public int LastOctet { get; init; }

    public HostStatus Status { get; set; } = HostStatus.Pending;

    /// <summary>Which probe confirmed liveness (or the last one tried).</summary>
    public LivenessSource Source { get; set; } = LivenessSource.None;

    public string HostName { get; set; } = string.Empty;

    public string Mac { get; set; } = string.Empty;

    /// <summary>User memo, resolved by MAC first and IP second.</summary>
    public string Memo { get; set; } = string.Empty;

    /// <summary>ICMP round-trip in ms; <see cref="int.MaxValue"/> when unknown.</summary>
    public long RoundtripMs { get; set; } = int.MaxValue;

    /// <summary>
    /// What the tooltip / detail pane shows: <c>&lt;1ms</c>, <c>12ms</c> or <c>Timeout</c>.
    /// Matches the original PingReplyInfo formatting.
    /// </summary>
    public string TimeText { get; set; } = "Timeout";

    /// <summary>True while a probe is in flight.</summary>
    public bool IsBusy { get; set; }

    /// <summary>The raw <see cref="IPStatus"/>, kept for the detail window.</summary>
    public IPStatus PingStatus { get; set; } = IPStatus.Unknown;

    /// <summary>批次扫描网格中的状态列文本（OK / NG）。</summary>
    public string StatusCode => HostStatusText.Code(Status);

    /// <summary>Sets the timing fields from an ICMP round-trip value.</summary>
    public void ApplyRoundtrip(long ms)
    {
        RoundtripMs = ms;
        TimeText = ms < 0 ? "Timeout" : ms < 1 ? "<1ms" : ms + "ms";
    }

    /// <summary>Marks the host unreachable and clears any stale identity data.</summary>
    public void MarkOffline()
    {
        Status = HostStatus.Offline;
        Source = LivenessSource.None;
        HostName = string.Empty;
        Mac = string.Empty;
        RoundtripMs = int.MaxValue;
        TimeText = "Timeout";
    }
}
