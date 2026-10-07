namespace IPScaner.WinUI.Services;

/// <summary>
/// Hand-off values passed between pages when one navigates to another — for
/// example "scan this host's ports" from the main grid's context menu.
/// </summary>
/// <remarks>
/// WinUI navigation parameters are not used directly because pages are resolved
/// by type, so a tiny ambient holder keeps the flow simple and explicit.
/// </remarks>
public static class NavigationArgs
{
    /// <summary>Host the port-scan page should pre-fill, then clear.</summary>
    public static string? PendingPortScanHost { get; set; }

    /// <summary>Segment the batch-scan page should pre-fill, then clear.</summary>
    public static string? PendingBatchSegment { get; set; }

    /// <summary>Memo key (IP or MAC) the memo page should focus, then clear.</summary>
    public static string? PendingMemoKey { get; set; }

    /// <summary>Consumes the pending port-scan host, if any.</summary>
    public static string? TakePortScanHost()
    {
        var value = PendingPortScanHost;
        PendingPortScanHost = null;
        return value;
    }

    public static string? TakeBatchSegment()
    {
        var value = PendingBatchSegment;
        PendingBatchSegment = null;
        return value;
    }

    public static string? TakeMemoKey()
    {
        var value = PendingMemoKey;
        PendingMemoKey = null;
        return value;
    }
}
