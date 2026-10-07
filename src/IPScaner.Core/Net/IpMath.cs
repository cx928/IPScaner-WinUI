using System.Net;
using System.Net.Sockets;

namespace IPScaner.Core.Net;

/// <summary>
/// IPv4 parsing and enumeration helpers. Pure functions — no I/O, no Windows
/// dependencies — so they are directly unit-testable.
/// </summary>
public static class IpMath
{
    /// <summary>
    /// Accepts a strict dotted quad. Unlike the original (whose regex matched a
    /// prefix and let "999.1.1.1" through to an OverflowException) every octet is
    /// range-checked here.
    /// </summary>
    public static bool IsValidIPv4(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
        {
            if (p.Length is 0 or > 3) return false;
            foreach (var c in p) if (c is < '0' or > '9') return false;
            if (!int.TryParse(p, out var v) || v > 255) return false;
        }
        return true;
    }

    /// <summary>Splits a dotted quad into four octets; throws when malformed.</summary>
    public static int[] ParseOctets(string ip)
    {
        if (!IsValidIPv4(ip)) throw new FormatException($"不是合法的IPv4地址: {ip}");
        return [.. ip.Split('.').Select(int.Parse)];
    }

    /// <summary>Tries to parse a dotted quad into four octets.</summary>
    public static bool TryParseOctets(string? ip, out int[] octets)
    {
        octets = [];
        if (!IsValidIPv4(ip)) return false;
        octets = [.. ip!.Trim().Split('.').Select(int.Parse)];
        return true;
    }

    /// <summary>
    /// The "192.168.1" portion of an address — what the main window scans.
    /// Returns an empty string when fewer than three octets are present, which is
    /// the original's validation signal ("请输入IP段信息，比如192.168.1").
    /// </summary>
    public static string GetSegment(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return string.Empty;
        var parts = ip.Trim().Split('.');
        return parts.Length >= 3 ? string.Join(".", parts.Take(3)) : string.Empty;
    }

    /// <summary>Validates a three-octet segment such as "192.168.1".</summary>
    public static bool IsValidSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        var parts = segment.Trim().Split('.');
        if (parts.Length != 3) return false;
        foreach (var p in parts)
        {
            if (p.Length is 0 or > 3) return false;
            foreach (var c in p) if (c is < '0' or > '9') return false;
            if (!int.TryParse(p, out var v) || v > 255) return false;
        }
        return true;
    }

    public static uint ToUInt32(string ip)
    {
        var o = ParseOctets(ip);
        return ((uint)o[0] << 24) | ((uint)o[1] << 16) | ((uint)o[2] << 8) | (uint)o[3];
    }

    public static string FromUInt32(uint value) =>
        $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    /// <summary>
    /// Enumerates an inclusive IPv4 range. When <paramref name="skipNetworkAndBroadcast"/>
    /// is true (the original's default) addresses ending in .0 or .255 are dropped
    /// — the same rule the batch-scan and mask modes relied on.
    /// </summary>
    public static List<string> GetRange(string startIp, string endIp, bool skipNetworkAndBroadcast = true)
    {
        var list = new List<string>();
        if (!IsValidIPv4(startIp) || !IsValidIPv4(endIp)) return list;

        uint start = ToUInt32(startIp), end = ToUInt32(endIp);
        if (start > end) return list;

        // Guard against a caller asking for the whole address space.
        const int maxCount = 65536;
        for (uint v = start; v <= end; v++)
        {
            var last = v & 0xFF;
            if (!skipNetworkAndBroadcast || (last != 0 && last != 255))
            {
                list.Add(FromUInt32(v));
                if (list.Count >= maxCount) break;
            }
            if (v == uint.MaxValue) break; // avoid overflow wrap
        }
        return list;
    }

    /// <summary>Enumerates the host addresses of a segment: "192.168.1" -> .1 .. .254.</summary>
    public static List<string> GetSegmentHosts(string segment, int first = 1, int last = 254)
    {
        var list = new List<string>();
        if (!IsValidSegment(segment)) return list;
        for (var i = first; i <= last; i++) list.Add($"{segment}.{i}");
        return list;
    }

    /// <summary>True for loopback, APIPA and 0.x addresses, which adapters never show.</summary>
    public static bool IsIgnorableLocalAddress(string ip) =>
        ip.StartsWith("169.254", StringComparison.Ordinal) ||
        ip.StartsWith("127.", StringComparison.Ordinal) ||
        ip.EndsWith(".0", StringComparison.Ordinal);

    /// <summary>Ping/HTTP-safe host literal for a user-supplied address.</summary>
    public static bool TryResolve(string hostOrIp, out IPAddress? address)
    {
        address = null;
        if (IPAddress.TryParse(hostOrIp, out var direct))
        {
            address = direct;
            return true;
        }
        try
        {
            var addrs = Dns.GetHostAddresses(hostOrIp);
            address = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return address is not null;
        }
        catch
        {
            return false;
        }
    }
}
