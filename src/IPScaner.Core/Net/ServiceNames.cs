namespace IPScaner.Core.Net;

/// <summary>
/// Friendly names for well-known TCP/UDP ports, shown in the port-scan results.
/// </summary>
public static class ServiceNames
{
    private static readonly Dictionary<int, string> Map = new()
    {
        [20] = "FTP-DATA", [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP",
        [53] = "DNS", [67] = "DHCP", [68] = "DHCP", [69] = "TFTP", [80] = "HTTP",
        [110] = "POP3", [111] = "RPC", [123] = "NTP", [135] = "RPC-EPM", [137] = "NetBIOS-NS",
        [138] = "NetBIOS-DGM", [139] = "NetBIOS-SSN", [143] = "IMAP", [161] = "SNMP",
        [162] = "SNMP-Trap", [179] = "BGP", [389] = "LDAP", [443] = "HTTPS",
        [445] = "SMB", [465] = "SMTPS", [500] = "IKE", [514] = "Syslog", [515] = "LPD",
        [548] = "AFP", [587] = "SMTP-Sub", [631] = "IPP", [636] = "LDAPS",
        [873] = "rsync", [993] = "IMAPS", [995] = "POP3S", [1080] = "SOCKS",
        [1194] = "OpenVPN", [1433] = "MSSQL", [1521] = "Oracle", [1723] = "PPTP",
        [2049] = "NFS", [2375] = "Docker", [2376] = "Docker-TLS", [3000] = "Dev-HTTP",
        [3128] = "Squid", [3306] = "MySQL", [3389] = "RDP", [4444] = "Metasploit",
        [5000] = "UPnP", [5060] = "SIP", [5353] = "mDNS", [5432] = "PostgreSQL",
        [5672] = "AMQP", [5900] = "VNC", [5985] = "WinRM-HTTP", [5986] = "WinRM-HTTPS",
        [6379] = "Redis", [7001] = "WebLogic", [8000] = "HTTP-Alt", [8006] = "Proxmox",
        [8080] = "HTTP-Proxy", [8081] = "HTTP-Alt", [8086] = "InfluxDB", [8443] = "HTTPS-Alt",
        [8888] = "HTTP-Alt", [9000] = "Portainer", [9090] = "WebSphere", [9200] = "Elasticsearch",
        [9300] = "Elasticsearch", [11211] = "Memcached", [27017] = "MongoDB",
        [50000] = "SAP", [50070] = "HDFS",
    };

    /// <summary>Returns a service name, or an empty string when the port is unknown.</summary>
    public static string Describe(int port) => Map.TryGetValue(port, out var name) ? name : string.Empty;
}
