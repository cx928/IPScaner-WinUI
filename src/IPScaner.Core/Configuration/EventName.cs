namespace IPScaner.Core.Configuration;

/// <summary>
/// The double-click action bound to an IP colour block.
/// Mirrors the original <c>EventName</c> enum byte-for-byte so that
/// <c>DoubleEvent="Ping"</c> in an existing IPScaner.cfg still round-trips.
/// </summary>
public enum EventName
{
    Ping = 0,
    ViewWeb = 1,
    Tracert = 2,
    Telnet = 3,
    Netstat = 4,
    ARP = 5,
    Share = 6,
}

/// <summary>Localised display names for <see cref="EventName"/> (combo box binding).</summary>
public static class EventNameText
{
    public static readonly (EventName Value, string Text)[] All =
    [
        (EventName.Ping, "Ping命令"),
        (EventName.ViewWeb, "浏览网页"),
        (EventName.Tracert, "Tracert命令"),
        (EventName.Telnet, "Telnet命令"),
        (EventName.Netstat, "Netstat命令"),
        (EventName.ARP, "ARP命令"),
        (EventName.Share, "访问共享目录"),
    ];

    public static string Describe(EventName value)
    {
        foreach (var (v, t) in All)
        {
            if (v == value) return t;
        }
        return value.ToString();
    }
}
