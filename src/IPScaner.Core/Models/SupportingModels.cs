namespace IPScaner.Core.Models;

/// <summary>One probed TCP port on one host.</summary>
public sealed class PortScanResult
{
    public required string IP { get; init; }
    public required int Port { get; init; }

    /// <summary>True when the TCP handshake completed.</summary>
    public bool IsOpen { get; set; }

    /// <summary>Connect time in ms, or -1 when closed/filtered.</summary>
    public long ElapsedMs { get; set; } = -1;

    /// <summary>Well-known service name for the port, when we can name it.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Clipboard/export line: "192.168.1.10:80  开放".</summary>
    public override string ToString() =>
        $"{IP}:{Port}\t{(IsOpen ? "开放" : "关闭")}" + (ElapsedMs >= 0 ? $"\t{ElapsedMs}ms" : string.Empty);
}

/// <summary>
/// One row of the local TCP/UDP endpoint table (本机端口占用查看).
/// </summary>
public sealed class LocalPortInfo
{
    public string Protocol { get; set; } = "TCP";
    public string LocalAddress { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }

    /// <summary>TCP state; UDP rows carry <see cref="UdpState"/>.</summary>
    public string State { get; set; } = string.Empty;

    public const string UdpState = "UDP";

    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;

    /// <summary>PIDs we could not resolve, or that have already exited.</summary>
    public bool ProcessMissing { get; set; }

    public override string ToString() =>
        $"Port:{LocalPort}，PID: {Pid}, Name: {ProcessName}, Path: {ProcessPath}";
}

/// <summary>A saved WLAN profile and its recovered cleartext key.</summary>
public sealed class WifiProfile
{
    public required string Ssid { get; init; }
    public string Password { get; set; } = string.Empty;

    /// <summary>认证方式, e.g. WPA2-Personal.</summary>
    public string Authentication { get; set; } = string.Empty;

    /// <summary>加密方式, e.g. CCMP.</summary>
    public string Encryption { get; set; } = string.Empty;

    /// <summary>True for open networks, which have no key.</summary>
    public bool IsOpen { get; set; }

    public override string ToString() => $"ssid:{Ssid}, pwd:{Password}";
}

/// <summary>One user-defined launcher parsed out of command.txt.</summary>
public sealed class DiyCommand
{
    public required string Name { get; init; }
    public required string Command { get; init; }

    public override string ToString() => $"{Name} -> {Command}";
}
