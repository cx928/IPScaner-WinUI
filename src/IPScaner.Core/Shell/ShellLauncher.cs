using System.Diagnostics;
using IPScaner.Core.Configuration;
using IPScaner.Core.Net;

namespace IPScaner.Core.Shell;

/// <summary>
/// Launches the external helpers the tool relies on: cmd windows (ping, tracert,
/// netstat, arp), Explorer, and Control-Panel applets.
/// </summary>
/// <remarks>
/// The original concatenated raw user text into a <c>cmd.exe /c</c> command line.
/// Every address that reaches this class is validated as a dotted quad first, so
/// a hostile "IP" typed into the scan box cannot inject shell metacharacters.
/// </remarks>
public sealed class ShellLauncher
{
    /// <summary>Windows tools exposed by the 快捷工具 menu (name -> command).</summary>
    public static readonly (string Name, string Command, string Hint)[] BuiltInTools =
    [
        ("网络连接", "ncpa.cpl", "打开网络连接面板"),
        ("程序和功能", "appwiz.cpl", "卸载或更改程序"),
        ("服务", "services.msc", "管理 Windows 服务"),
        ("设备管理器", "devmgmt.msc", "查看硬件设备"),
        ("计算机管理", "compmgmt.msc", "磁盘、服务、事件查看器"),
        ("任务管理器", "taskmgr", "查看运行中的进程"),
        ("资源监视器", "resmon", "实时查看资源占用"),
        ("控制面板", "control", "打开控制面板"),
        ("系统信息", "msinfo32", "查看系统详细配置"),
        ("注册表编辑器", "regedit", "编辑注册表"),
        ("事件查看器", "eventvwr.msc", "查看系统日志"),
        ("磁盘管理", "diskmgmt.msc", "管理磁盘分区"),
        ("防火墙", "wf.msc", "高级安全 Windows 防火墙"),
        ("远程桌面", "mstsc", "远程桌面连接"),
        ("系统配置", "msconfig", "启动项与引导配置"),
        ("命令提示符", "cmd", "打开命令提示符"),
        ("PowerShell", "powershell", "打开 PowerShell"),
        ("计算器", "calc", "打开计算器"),
    ];

    /// <summary>Runs a command through cmd.exe, optionally with a visible window.</summary>
    public void RunCommand(string command, bool visible = true)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        // A bare existing file path is launched directly, as the original did.
        if (File.Exists(command))
        {
            SafeStart(new ProcessStartInfo(command) { UseShellExecute = true });
            return;
        }

        SafeStart(new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            UseShellExecute = true,
            WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>Opens an http(s) URL in the default browser.</summary>
    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }
        SafeStart(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Opens a UNC share in Explorer.</summary>
    public void OpenShare(string host)
    {
        if (!IpMath.IsValidIPv4(host)) return;
        SafeStart(new ProcessStartInfo("explorer.exe", $@"\\{host}") { UseShellExecute = true });
    }

    /// <summary>Opens Explorer at a folder (or selects a file).</summary>
    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        SafeStart(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    /// <summary>
    /// Builds and launches the command for one of the <see cref="EventName"/>
    /// actions — the exact command lines the original used for double-click and
    /// for the colour-block context menu.
    /// </summary>
    public void RunHostAction(EventName action, string ip, int pingCount = 4)
    {
        if (!IpMath.IsValidIPv4(ip)) return;

        switch (action)
        {
            case EventName.Ping:
                RunCommand($"ping {ip} -n {pingCount} &pause");
                break;
            case EventName.Tracert:
                RunCommand($"tracert {ip} &pause");
                break;
            case EventName.Telnet:
                RunCommand($"telnet {ip} 23 &pause");
                break;
            case EventName.Netstat:
                RunCommand($"netstat -ano | findstr {ip} &pause");
                break;
            case EventName.ARP:
                RunCommand($"arp -a {ip} &pause");
                break;
            case EventName.ViewWeb:
                OpenUrl("http://" + ip);
                break;
            case EventName.Share:
                OpenShare(ip);
                break;
        }
    }

    /// <summary>Launches a Control-Panel style applet from the 快捷工具 list.</summary>
    public void RunBuiltInTool(string command) => RunCommand(command);

    private static void SafeStart(ProcessStartInfo psi)
    {
        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logging.AppLog.Instance.Log(nameof(ShellLauncher), $"启动失败 {psi.FileName}: {ex.Message}");
        }
    }
}
