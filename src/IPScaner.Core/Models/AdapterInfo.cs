using System.Xml.Serialization;

namespace IPScaner.Core.Models;

/// <summary>
/// One IPv4 address bound to a network adapter. Mirrors the original
/// <c>AdapterInfo</c>, including its <c>AdapterInfoCollection</c> XML shape that
/// the "modify local IP" history file uses.
/// </summary>
[XmlRoot("Adapter")]
public class AdapterInfo
{
    [XmlAttribute] public string Name { get; set; } = string.Empty;

    [XmlAttribute] public string IP { get; set; } = string.Empty;

    /// <summary>Raw <see cref="System.Net.NetworkInformation.OperationalStatus"/> value.</summary>
    [XmlIgnore] public int Status { get; set; }

    [XmlIgnore] public string Mac { get; set; } = string.Empty;

    [XmlAttribute] public string SubnetMask { get; set; } = string.Empty;

    [XmlAttribute] public string Gateway { get; set; } = string.Empty;

    [XmlIgnore] public bool IsDhcpEnabled { get; set; }

    [XmlIgnore] public List<string> DnsServers { get; set; } = [];

    /// <summary>DNS servers flattened for persistence: "8.8.8.8,1.1.1.1".</summary>
    [XmlAttribute("DNS")]
    public string Dns
    {
        get => string.Join(",", DnsServers);
        set => DnsServers = string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Adapter speed in bits/sec, when the OS reports it.</summary>
    [XmlIgnore] public long Speed { get; set; }

    /// <summary>Adapter description, e.g. "Intel(R) Ethernet Connection".</summary>
    [XmlIgnore] public string Description { get; set; } = string.Empty;

    [XmlIgnore] public bool IsUp => Status == 1; // OperationalStatus.Up

    public override string ToString() => $"名称：{Name} IP：{IP} MAC：{Mac}";
}

/// <summary>Root element of the saved adapter/history file.</summary>
[XmlRoot("root")]
public class AdapterInfoCollection
{
    [XmlArray("array")]
    public List<AdapterInfo> AdapterList { get; set; } = [];
}
