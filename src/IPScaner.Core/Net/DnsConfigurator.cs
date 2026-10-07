using System.Net.NetworkInformation;
using System.Net.Sockets;
using IPScaner.Core.Logging;

namespace IPScaner.Core.Net;

/// <summary>One ready-made DNS choice for the "一键更换 DNS" page.</summary>
/// <param name="Name">Chinese display name, e.g. 阿里 DNS.</param>
/// <param name="Primary">Primary server; empty for <see cref="DnsConfigurator.Automatic"/>.</param>
/// <param name="Secondary">Secondary server; empty when the provider publishes only one.</param>
/// <param name="Note">Short Chinese hint about latency / pollution.</param>
public sealed record DnsPreset(string Name, string Primary, string Secondary, string Note);

/// <summary>
/// Applies DNS presets to one adapter ("一键更换 DNS").
/// </summary>
/// <remarks>
/// <para>
/// The actual work is delegated to <see cref="NetworkConfigurator"/>, which already
/// wraps <c>netsh interface ip set dns</c> correctly (awaited process, captured
/// stdout/stderr, real exit code, elevation detection) — the original
/// <c>FormLocalIP.ExceCmd</c> reported 成功 unconditionally.
/// </para>
/// <para>
/// What this class adds on top is the part netsh cannot answer cheaply: the adapter
/// is resolved against <see cref="NetworkInterface"/> <i>before</i> anything is
/// spawned, so a typo in the adapter name, an empty list or a malformed address
/// comes back as a failure with <see cref="NetworkConfigurator.NotRunExitCode"/> and
/// genuinely runs nothing. Applying to the wrong adapter (or to a name that netsh
/// would match loosely) is what breaks a machine's networking, so nothing is
/// delegated until the target and every address have been checked.
/// </para>
/// <para>
/// Only IPv4 DNS servers are handled and reported — the rest of the tool is
/// IPv4-only, and a list read back by <see cref="GetCurrentAsync"/> must be safe to
/// feed straight into <see cref="ApplyCustomAsync"/>.
/// </para>
/// </remarks>
public sealed class DnsConfigurator
{
    /// <summary>Display name of the DHCP entry; <see cref="ApplyAsync"/> recognises it by its empty primary.</summary>
    public const string AutomaticName = "自动获取(DHCP)";

    private const string Category = nameof(DnsConfigurator);

    private readonly NetworkConfigurator _configurator = new();

    /// <summary>Restores the router/ISP-supplied servers: <c>netsh … set dns "…" dhcp</c>.</summary>
    public static DnsPreset Automatic { get; } = new(AutomaticName, string.Empty, string.Empty, "由路由器或运营商自动下发");

    /// <summary>Every preset offered by the page, 自动获取 first.</summary>
    public static IReadOnlyList<DnsPreset> Presets { get; } =
    [
        Automatic,
        new("阿里 DNS", "223.5.5.5", "223.6.6.6", "国内、延迟低，解析稳定"),
        new("腾讯 DNSPod", "119.29.29.29", "182.254.116.116", "国内、延迟低，游戏视频友好"),
        new("114 DNS", "114.114.114.114", "114.114.115.115", "国内、通用性强，可拦截恶意网站"),
        new("百度 DNS", "180.76.76.76", string.Empty, "国内、延迟低，无备用地址"),
        new("Cloudflare", "1.1.1.1", "1.0.0.1", "国外、注重隐私，国内可能被污染"),
        new("Google", "8.8.8.8", "8.8.4.4", "国外、稳定，国内可能被污染"),
    ];

    /// <summary>True when the preset means "let DHCP decide" rather than a fixed server list.</summary>
    public static bool IsAutomatic(DnsPreset preset) =>
        preset is not null && string.IsNullOrWhiteSpace(preset.Primary);

    /// <summary>Applies a preset — <see cref="Automatic"/> switches DNS back to DHCP.</summary>
    public async Task<NetConfigResult> ApplyAsync(string adapterName, DnsPreset preset, CancellationToken ct = default)
    {
        if (!TryValidateAdapter(adapterName, out var name, out var adapterError)) return adapterError;
        if (preset is null) return Failure("请选择DNS方案");

        if (IsAutomatic(preset))
        {
            AppLog.Instance.Log(Category, $"{name} → 自动获取 DNS");
            return await _configurator.SetDnsDhcpAsync(name, ct).ConfigureAwait(false);
        }

        var servers = BuildServerList(preset);
        if (!TryValidateServers(servers, out var serverError)) return serverError;

        AppLog.Instance.Log(Category, $"{name} → DNS {string.Join(", ", servers)}（{preset.Name}）");
        return await _configurator.SetDnsStaticAsync(name, servers, ct).ConfigureAwait(false);
    }

    /// <summary>Switches DNS back to DHCP, the same thing as applying <see cref="Automatic"/>.</summary>
    public Task<NetConfigResult> ResetToAutomaticAsync(string adapterName, CancellationToken ct = default)
    {
        if (!TryValidateAdapter(adapterName, out var name, out var adapterError)) return Task.FromResult(adapterError);

        AppLog.Instance.Log(Category, $"{name} → 自动获取 DNS");
        return _configurator.SetDnsDhcpAsync(name, ct);
    }

    /// <summary>Applies a hand-typed server list. Every address is validated before netsh is started.</summary>
    public Task<NetConfigResult> ApplyCustomAsync(
        string adapterName, IReadOnlyList<string> servers, CancellationToken ct = default)
    {
        if (!TryValidateAdapter(adapterName, out var name, out var adapterError)) return Task.FromResult(adapterError);

        var cleaned = CleanServers(servers);
        if (!TryValidateServers(cleaned, out var serverError)) return Task.FromResult(serverError);

        AppLog.Instance.Log(Category, $"{name} → 自定义 DNS {string.Join(", ", cleaned)}");
        return _configurator.SetDnsStaticAsync(name, cleaned, ct);
    }

    /// <summary>
    /// Reads the adapter's effective IPv4 DNS servers (what DHCP or a static
    /// setting currently resolves to). A missing adapter, a cancelled token or an
    /// unreadable adapter yields an empty list — this method never throws.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCurrentAsync(string adapterName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return [];

        try
        {
            var name = adapterName.Trim();
            return await Task.Run(() => ReadCurrentServers(name), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(Category, $"读取 {adapterName} 的DNS设置失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// True when Windows currently has an interface with this name. netsh matches
    /// interface names case-insensitively, and so does this check.
    /// </summary>
    /// <remarks>
    /// The raw <see cref="NetworkInterface.Name"/> is used rather than
    /// <c>AdapterService.GetAll()</c>: that service filters out virtual adapters and
    /// appends "_1" to duplicate names for the combo box, and those display names
    /// would not be accepted by netsh.
    /// </remarks>
    public static bool AdapterExists(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return false;

        try
        {
            var name = adapterName.Trim();
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(ni => string.Equals(ni.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(Category, "枚举网卡失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>Every interface name Windows knows, for callers that need to build their own list.</summary>
    public static IReadOnlyList<string> GetAdapterNames()
    {
        var names = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.IsNullOrWhiteSpace(ni.Name)) names.Add(ni.Name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(Category, "枚举网卡失败: " + ex.Message);
        }
        return names;
    }

    // ---- helpers -----------------------------------------------------------

    private static IReadOnlyList<string> BuildServerList(DnsPreset preset)
    {
        var servers = new List<string>();
        if (!string.IsNullOrWhiteSpace(preset.Primary)) servers.Add(preset.Primary.Trim());
        if (!string.IsNullOrWhiteSpace(preset.Secondary)) servers.Add(preset.Secondary.Trim());
        return servers;
    }

    private static List<string> CleanServers(IReadOnlyList<string>? servers)
    {
        var cleaned = new List<string>();
        if (servers is null) return cleaned;
        foreach (var server in servers)
        {
            if (!string.IsNullOrWhiteSpace(server)) cleaned.Add(server.Trim());
        }
        return cleaned;
    }

    private static bool TryValidateAdapter(string? adapterName, out string name, out NetConfigResult failure)
    {
        name = adapterName?.Trim() ?? string.Empty;
        failure = default;

        if (name.Length == 0)
        {
            failure = Failure("请选择要设置的网卡");
            return false;
        }

        if (name.Contains('"'))
        {
            failure = Failure($"网卡名称不合法（不能包含引号）：\"{name}\"");
            return false;
        }

        if (!AdapterExists(name))
        {
            failure = Failure($"网卡不存在：\"{name}\"，请刷新网卡列表后重试");
            return false;
        }

        return true;
    }

    private static bool TryValidateServers(IReadOnlyList<string> servers, out NetConfigResult failure)
    {
        failure = default;

        if (servers.Count == 0)
        {
            failure = Failure("请输入DNS服务器地址");
            return false;
        }

        for (var i = 0; i < servers.Count; i++)
        {
            if (!IpMath.IsValidIPv4(servers[i]))
            {
                failure = Failure($"第 {i + 1} 个DNS地址格式不正确：\"{servers[i]}\"");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads DNS servers straight from the IP Helper API — no process is started,
    /// so polling this while the user browses the presets is cheap.
    /// </summary>
    private static IReadOnlyList<string> ReadCurrentServers(string adapterName)
    {
        var result = new List<string>();
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => string.Equals(ni.Name, adapterName, StringComparison.OrdinalIgnoreCase));
            if (adapter is null) return result;

            foreach (var dns in adapter.GetIPProperties().DnsAddresses)
            {
                if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                var text = dns.ToString();
                if (!result.Contains(text, StringComparer.Ordinal)) result.Add(text);
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(Category, $"读取 {adapterName} 的DNS设置失败: {ex.Message}");
        }
        return result;
    }

    private static NetConfigResult Failure(string message) =>
        new(false, message, NetworkConfigurator.NotRunExitCode);
}
