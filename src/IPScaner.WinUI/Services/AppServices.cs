using System.Security.Principal;
using IPScaner.Core.Caching;
using IPScaner.Core.Commands;
using IPScaner.Core.Configuration;
using IPScaner.Core.Logging;
using IPScaner.Core.Memo;
using IPScaner.Core.Models;
using IPScaner.Core.Net;
using IPScaner.Core.Shell;

namespace IPScaner.WinUI.Services;

/// <summary>
/// Process-wide service container. A single shared instance keeps every page
/// working against the same configuration, memo store and caches — the role the
/// original's static <c>Global</c> class played, but explicit and testable.
/// </summary>
public sealed class AppServices
{
    private static AppServices? _current;

    public static AppServices Current =>
        _current ?? throw new InvalidOperationException("AppServices.Initialize() has not run yet.");

    public static void Initialize()
    {
        if (_current is not null) return;
        _current = new AppServices();
        _current.Start();
    }

    private AppServices()
    {
        ConfigStore = new ConfigStore();
        Memo = new MemoStore();
        DiyCommands = new DiyCommandStore();
        History = new NetworkHistory();
        Shell = new ShellLauncher();
        Adapters = new AdapterService();
        Arp = new ArpTable(() => Adapters.GetAll());
        Scanner = new ScanEngine(new LivenessProbe(Arp), new NameResolver(), Arp);
        PortScanner = new PortScanner();
        LocalPorts = new LocalPortTable();
        Wifi = new WifiService();
        NetworkConfig = new NetworkConfigurator();
    }

    // ---- persisted state ---------------------------------------------------

    public ConfigStore ConfigStore { get; }

    /// <summary>The live configuration. Mutate a clone, then call <see cref="ApplyConfig"/>.</summary>
    public AppConfig Config { get; private set; } = new();

    public MemoStore Memo { get; }

    public DiyCommandStore DiyCommands { get; }

    public NetworkHistory History { get; }

    // ---- engines -----------------------------------------------------------

    public ShellLauncher Shell { get; }
    public AdapterService Adapters { get; }
    public ArpTable Arp { get; }
    public ScanEngine Scanner { get; }
    public PortScanner PortScanner { get; }
    public LocalPortTable LocalPorts { get; }
    public WifiService Wifi { get; }
    public NetworkConfigurator NetworkConfig { get; }

    // ---- environment -------------------------------------------------------

    /// <summary>
    /// True when running elevated. Unlike the original — which forced UAC on every
    /// launch — elevation is only required for 修改本地IP and ARP cache flushing,
    /// so the UI surfaces this rather than refusing to start.
    /// </summary>
    public bool IsElevated { get; private set; }

    /// <summary>Adapters as of the last <see cref="RefreshAdapters"/> call.</summary>
    public IReadOnlyList<AdapterInfo> AdapterList { get; private set; } = [];

    /// <summary>Raised after the configuration is saved or reloaded.</summary>
    public event EventHandler<AppConfig>? ConfigChanged;

    /// <summary>Raised when caches are cleared, so open pages can re-render.</summary>
    public event EventHandler? CachesCleared;

    private void Start()
    {
        IsElevated = DetectElevation();

        Config = ConfigStore.Load();
        ApplyLogging();
        AppLog.Instance.Log(nameof(AppServices), $"配置文件: {ConfigStore.FilePath} (存在={ConfigStore.Exists})");

        Memo.Load();
        DiyCommands.WriteTemplate();

        // Adapter enumeration touches WMI-adjacent APIs; keep it off the UI thread.
        _ = Task.Run(() =>
        {
            try { AdapterList = Adapters.GetAll(); }
            catch (Exception ex) { AppLog.Instance.Log(nameof(AppServices), "枚举网卡失败: " + ex.Message); }
        });
    }

    /// <summary>Persists the configuration and notifies listeners.</summary>
    public void ApplyConfig(AppConfig config)
    {
        Config = config;
        ApplyLogging();
        try
        {
            ConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(AppServices), "保存配置失败: " + ex.Message);
        }
        ConfigChanged?.Invoke(this, config);
    }

    /// <summary>Re-reads the configuration from disk.</summary>
    public void ReloadConfig()
    {
        Config = ConfigStore.Load();
        ApplyLogging();
        ConfigChanged?.Invoke(this, Config);
    }

    private void ApplyLogging() => AppLog.Instance.Enabled = Config.LogEnabled;

    /// <summary>Drops cached host names and MACs so the next scan re-queries them.</summary>
    public void ClearCaches()
    {
        ScanCaches.ClearAll();
        Arp.Invalidate();
        CachesCleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-enumerates adapters (after an IP change, or on user request).</summary>
    public IReadOnlyList<AdapterInfo> RefreshAdapters()
    {
        try { AdapterList = Adapters.GetAll(); }
        catch (Exception ex) { AppLog.Instance.Log(nameof(AppServices), "刷新网卡失败: " + ex.Message); }
        return AdapterList;
    }

    /// <summary>
    /// The best guess at the segment to scan.
    /// </summary>
    /// <remarks>
    /// A gateway is the strongest signal that an adapter is the real LAN link:
    /// machines commonly also carry ZeroTier, Hyper-V ("vEthernet") and ICS
    /// adapters, and the original's only filter was a hard-coded "VMware" name
    /// check. Preference order is up + gateway, then up, then anything.
    /// </remarks>
    public string GetDefaultSegment()
    {
        var candidates = AdapterList.Count > 0 ? AdapterList : RefreshAdapters();

        foreach (var predicate in new Func<AdapterInfo, bool>[]
                 {
                     a => a.IsUp && !string.IsNullOrEmpty(a.Gateway),
                     a => a.IsUp,
                     _ => true,
                 })
        {
            foreach (var adapter in candidates.Where(predicate))
            {
                var segment = IpMath.GetSegment(adapter.IP);
                if (IpMath.IsValidSegment(segment)) return segment;
            }
        }

        return Adapters.GetPrimarySegment() ?? "192.168.1";
    }

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
