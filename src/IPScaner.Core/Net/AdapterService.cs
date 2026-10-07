using System.Net.NetworkInformation;
using System.Net.Sockets;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;

namespace IPScaner.Core.Net;

/// <summary>
/// Enumerates the machine's IPv4 adapter bindings — the source for the adapter
/// combo box, the local-IP window and the desktop badge.
/// </summary>
/// <remarks>
/// Filtering mirrors the original <c>Utility.GetAllAdapterIP</c>: only Ethernet
/// and Wi-Fi adapters, VMware virtual adapters excluded, and APIPA / loopback /
/// ".0" addresses skipped. Duplicate adapter names get the original's "_1", "_2"
/// suffix so the combo box stays unambiguous.
/// </remarks>
public sealed class AdapterService
{
    /// <summary>Returns every usable IPv4 binding, ordered by adapter name then status.</summary>
    public List<AdapterInfo> GetAll()
    {
        var raw = new List<AdapterInfo>();

        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(AdapterService), "枚举网卡失败: " + ex.Message);
            return raw;
        }

        foreach (var ni in interfaces)
        {
            try
            {
                if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
                    continue;
                if (ni.Name.Contains("VMware", StringComparison.OrdinalIgnoreCase))
                    continue;

                var mac = FormatMac(ni.GetPhysicalAddress());
                var props = ni.GetIPProperties();
                var ipv4 = TryGetIPv4Properties(props);

                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = unicast.Address.ToString();
                    if (IpMath.IsIgnorableLocalAddress(ip)) continue;

                    var info = new AdapterInfo
                    {
                        Name = ni.Name,
                        Description = ni.Description,
                        IP = ip,
                        Status = (int)ni.OperationalStatus,
                        Mac = mac,
                        SubnetMask = SafeMask(unicast),
                        IsDhcpEnabled = ipv4?.IsDhcpEnabled ?? false,
                        Speed = TryGetSpeed(ni),
                    };

                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        info.Gateway = gw.Address.ToString();
                        AppLog.Instance.Log(nameof(AdapterService), $"读取{info.IP} 网关: {info.Gateway}");
                    }

                    foreach (var dns in props.DnsAddresses)
                    {
                        if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                        info.DnsServers.Add(dns.ToString());
                        AppLog.Instance.Log(nameof(AdapterService), $"读取{info.IP} DNS服务器: {dns}");
                    }

                    raw.Add(info);
                    AppLog.Instance.Log(nameof(AdapterService), $"读取本地网卡信息 {info}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Instance.Log(nameof(AdapterService), $"跳过网卡 {ni.Name}: {ex.Message}");
            }
        }

        // Stable ordering, then de-duplicate display names.
        var ordered = raw.OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Status).ToList();
        var result = new List<AdapterInfo>();
        foreach (var item in ordered)
        {
            var name = item.Name;
            var n = 1;
            while (result.Any(r => r.Name == name)) name = $"{item.Name}_{n++}";
            item.Name = name;
            result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// The 192.168.1 style segment of the first usable adapter — what the main
    /// window pre-fills at startup.
    /// </summary>
    public string? GetPrimarySegment()
    {
        var adapters = GetAll();
        var preferred = adapters.FirstOrDefault(a => a.IsUp && !string.IsNullOrEmpty(a.IP))
                        ?? adapters.FirstOrDefault(a => !string.IsNullOrEmpty(a.IP));
        if (preferred is null) return null;

        var segment = IpMath.GetSegment(preferred.IP);
        return IpMath.IsValidSegment(segment) ? segment : null;
    }

    /// <summary>All distinct scan segments implied by the current adapters.</summary>
    public List<string> GetSegments()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var a in GetAll())
        {
            var segment = IpMath.GetSegment(a.IP);
            if (IpMath.IsValidSegment(segment) && seen.Add(segment)) list.Add(segment);
        }
        return list;
    }

    /// <summary>Formats a physical address as the original did: "00-1A-2B-3C-4D-5E".</summary>
    public static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 0) return string.Empty;
        return string.Join("-", bytes.Select(b => b.ToString("X2")));
    }

    private static IPv4InterfaceProperties? TryGetIPv4Properties(IPInterfaceProperties props)
    {
        try { return props.GetIPv4Properties(); }
        catch { return null; }
    }

    private static string SafeMask(UnicastIPAddressInformation unicast)
    {
        try { return unicast.IPv4Mask?.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static long TryGetSpeed(NetworkInterface ni)
    {
        try { return ni.Speed; }
        catch { return 0; }
    }
}
