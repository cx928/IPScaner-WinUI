using IPScaner.Core.Logging;
using IPScaner.Core.Models;
using IPScaner.Core.Net;
using IPScaner.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPScaner.WinUI.Views;

/// <summary>
/// 修改本地IP — the page form of the original <c>FormLocalIP</c> (title 一键设置IP).
/// </summary>
/// <remarks>
/// <para>Fidelity: the control set, captions and the 4-way radio state machine of RE
/// spec A.7 are reproduced, including the rule that a static address forces static DNS
/// (so "static IP + automatic DNS" cannot be selected). Validation runs in the original's
/// order and shows the original's exact messages.</para>
/// <para>Defects fixed (spec A.3/A.5, contract §7):</para>
/// <list type="bullet">
/// <item>the original wrote a <c>netsh</c> line into <c>cmd.exe</c>'s stdin, never waited
/// and never read an exit code, then always reported 本地IP地址修改成功. Here every command
/// is awaited through <see cref="NetworkConfigurator"/> and the dialog shows netsh's real
/// message and exit code on failure;</item>
/// <item><c>Utility.ValidIP</c> was an unanchored prefix regex that accepted
/// <c>999.999.999.999</c> and <c>10.0.0.1abc</c>; every address is range-checked with
/// <see cref="IpMath.IsValidIPv4"/> before anything is spawned;</item>
/// <item>the original forced UAC on every launch. This port runs unelevated and warns
/// instead, offering 以管理员身份重启.</item>
/// </list>
/// </remarks>
public sealed partial class LocalIpPage : Page
{
    /// <summary>The original window title, used as every message-box caption.</summary>
    private const string DialogTitle = "一键设置IP";

    /// <summary>Design-time default of <c>txtMask</c> (spec A.1).</summary>
    private const string DefaultMask = "255.255.255.0";

    /// <summary>The four providers behind the original <c>cmbDNS</c> (阿里/腾讯/百度/114).</summary>
    private static readonly (string Name, string Address)[] DnsPresets =
    [
        ("阿里", "223.5.5.5"),
        ("腾讯", "119.29.29.29"),
        ("百度", "180.76.76.76"),
        ("114", "114.114.114.114"),
    ];

    private List<AdapterInfo> _adapters = [];
    private List<AdapterInfo> _history = [];

    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>Interface alias handed to netsh (see <see cref="ResolveNetshName"/>).</summary>
    private string _netshName = string.Empty;

    /// <summary>Guards the auto-gateway helper against its own writes.</summary>
    private bool _settingGateway;

    /// <summary>True while 网关 still holds a value the auto-gateway helper generated.</summary>
    private bool _gatewayAutoFilled;

    /// <summary>Suppresses the history list's selection handler while the list is rebuilt.</summary>
    private bool _suppressHistoryEvents;

    public LocalIpPage()
    {
        InitializeComponent();

        foreach (var preset in DnsPresets) DnsPresetCombo.Items.Add(preset.Name);

        Loaded += OnPageLoaded;
        Unloaded += (_, _) => CancelPendingWork();
    }

    /// <summary>The request staged by the form, after validation.</summary>
    private readonly record struct Request(
        bool StaticIp, string Ip, string Mask, string Gateway, bool StaticDns, List<string> DnsServers);

    // =====================================================================
    // load / adapters
    // =====================================================================

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // The original's permission model was "always elevated"; here the page states it.
        ElevationBar.IsOpen = !AppServices.Current.IsElevated;

        // FormLocalIP_Load: rbtnDHCP.Checked = true, then rbtnDNS.Checked = true.
        // Both are overwritten by the first adapter selection.
        IpDhcpRadio.IsChecked = true;
        DnsAutoRadio.IsChecked = true;
        UpdateIpFieldsEnabled();
        UpdateDnsFieldsEnabled();

        LoadHistory();
        _ = ReloadAdaptersAsync();
    }

    private void CancelPendingWork()
    {
        try { _cts?.Cancel(); }
        catch { /* already gone */ }
    }

    private async Task ReloadAdaptersAsync()
    {
        SetBusy(true, "正在读取网卡信息…");
        try
        {
            // Enumeration touches the network stack; keep it off the UI thread.
            var list = await Task.Run(() => AppServices.Current.RefreshAdapters());
            _adapters = [.. list];
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(LocalIpPage), "枚举网卡失败: " + ex.Message);
            _adapters = [];
        }
        finally
        {
            SetBusy(false);
        }

        PopulateAdapters();
    }

    private void PopulateAdapters()
    {
        var previous = SelectedAdapter;

        AdapterCombo.Items.Clear();
        foreach (var adapter in _adapters)
        {
            AdapterCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{adapter.Name}  ({adapter.IP})",
                Tag = adapter,
            });
        }

        AdapterHintBar.IsOpen = _adapters.Count == 0;

        if (_adapters.Count == 0)
        {
            CurrentIpLink.Content = "—";
            ShowAdapterDetails(null);
            _netshName = string.Empty;
            StatusText.Text = "未找到可用的以太网或无线网卡。";
            return;
        }

        // Keep the user's place across a refresh; the IP changes after an apply, so
        // fall back to the same interface name, then to the first "up" adapter.
        var target =
            _adapters.FirstOrDefault(a => previous is not null && a.Name == previous.Name && a.IP == previous.IP)
            ?? _adapters.FirstOrDefault(a => previous is not null && a.Name == previous.Name)
            ?? _adapters.FirstOrDefault(a => a.IsUp)
            ?? _adapters[0];

        foreach (var item in AdapterCombo.Items.OfType<ComboBoxItem>())
        {
            if (!ReferenceEquals(item.Tag, target)) continue;
            AdapterCombo.SelectedItem = item;
            break;
        }

        // Selection can be a no-op when the combo already pointed at this entry.
        if (AdapterCombo.SelectedItem is null) ShowAdapter(target);
    }

    private AdapterInfo? SelectedAdapter => (AdapterCombo.SelectedItem as ComboBoxItem)?.Tag as AdapterInfo;

    private void OnAdapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedAdapter is { } adapter) ShowAdapter(adapter);
    }

    private async void OnRefreshAdaptersClick(object sender, RoutedEventArgs e) => await ReloadAdaptersAsync();

    /// <summary>
    /// Fills the form from the selected binding (spec A.2): the DHCP flag drives the mode
    /// radios, then IP / mask / gateway and the first DNS server are copied in.
    /// </summary>
    private void ShowAdapter(AdapterInfo adapter)
    {
        IpDhcpRadio.IsChecked = adapter.IsDhcpEnabled;
        IpStaticRadio.IsChecked = !adapter.IsDhcpEnabled;

        SetIpBox.Text = adapter.IP;
        MaskBox.Text = string.IsNullOrWhiteSpace(adapter.SubnetMask) ? DefaultMask : adapter.SubnetMask;
        SetGatewayText(adapter.Gateway, auto: false);
        DnsBox.Text = adapter.DnsServers.Count > 0 ? string.Join(", ", adapter.DnsServers) : string.Empty;

        CurrentIpLink.Content = string.IsNullOrEmpty(adapter.IP) ? "—" : adapter.IP;
        ShowAdapterDetails(adapter);

        _netshName = ResolveNetshName(adapter.Name);
        if (!string.Equals(_netshName, adapter.Name, StringComparison.Ordinal))
        {
            AppLog.Instance.Log(nameof(LocalIpPage),
                $"网卡显示名 {adapter.Name} 对应系统名 {_netshName}（同名多IP时 Core 会加 _1/_2 后缀）");
        }

        UpdateIpFieldsEnabled();
        UpdateDnsFieldsEnabled();
        RefreshHistoryList();

        var mode = adapter.IsDhcpEnabled ? "自动获取" : "静态";
        StatusText.Text = $"已选择 {adapter.Name}（{adapter.IP}，{mode}）";
    }

    private void ShowAdapterDetails(AdapterInfo? adapter)
    {
        if (adapter is null)
        {
            DetailName.Text = DetailMac.Text = DetailIp.Text = "—";
            DetailMask.Text = DetailGateway.Text = DetailDhcp.Text = DetailDns.Text = "—";
            return;
        }

        DetailName.Text = adapter.Name;
        DetailMac.Text = adapter.Mac.Length > 0 ? adapter.Mac : "—";
        DetailIp.Text = adapter.IP;
        DetailMask.Text = adapter.SubnetMask.Length > 0 ? adapter.SubnetMask : "—";
        DetailGateway.Text = adapter.Gateway.Length > 0 ? adapter.Gateway : "—";
        DetailDhcp.Text = adapter.IsDhcpEnabled ? "自动获取" : "静态";
        DetailDns.Text = adapter.DnsServers.Count > 0 ? string.Join(", ", adapter.DnsServers) : "—";
    }

    /// <summary>
    /// The interface alias netsh accepts. <c>AdapterService</c> de-duplicates display
    /// names with the original's <c>_1</c>/<c>_2</c> suffix when one NIC carries several
    /// IPv4 addresses, but netsh only knows the real alias — so a suffixed name is mapped
    /// back to the live interface name when, and only when, that name exists here.
    /// Without this the second IP row of a multi-IP NIC would always fail to apply.
    /// </summary>
    private static string ResolveNetshName(string displayName)
    {
        try
        {
            var live = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Select(ni => ni.Name)
                .ToHashSet(StringComparer.Ordinal);

            if (live.Contains(displayName)) return displayName;

            var cut = displayName.LastIndexOf('_');
            if (cut > 0 && int.TryParse(displayName[(cut + 1)..], out _))
            {
                var baseName = displayName[..cut];
                if (live.Contains(baseName)) return baseName;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(LocalIpPage), "解析网卡系统名失败: " + ex.Message);
        }

        return displayName;
    }

    // =====================================================================
    // radio state machine (spec A.7)
    // =====================================================================

    private void OnIpModeChanged(object sender, RoutedEventArgs e)
    {
        // Both handlers ignore the "unchecked" half, exactly like the original.
        if (sender is not RadioButton { IsChecked: true } radio) return;

        if (ReferenceEquals(radio, IpStaticRadio))
        {
            // 使用下面的 IP地址 forces 使用下面的 DNS服务器地址.
            DnsStaticRadio.IsChecked = true;
        }

        UpdateIpFieldsEnabled();
        UpdateDnsFieldsEnabled();
    }

    private void OnDnsModeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } radio) return;

        if (ReferenceEquals(radio, DnsAutoRadio) && IpStaticRadio.IsChecked == true)
        {
            // "static IP + automatic DNS" is not a legal combination in the original UI.
            DnsStaticRadio.IsChecked = true;
            DnsBox.IsEnabled = true;
            DnsPresetCombo.IsEnabled = true;
            StatusText.Text = "静态IP下不能自动获取DNS，已切换为【使用下面的 DNS服务器地址】";
            return;
        }

        UpdateDnsFieldsEnabled();
    }

    private void UpdateIpFieldsEnabled()
    {
        var isStatic = IpStaticRadio.IsChecked == true;
        SetIpBox.IsEnabled = isStatic;
        MaskBox.IsEnabled = isStatic;
        GatewayBox.IsEnabled = isStatic;
        HistoryList.IsEnabled = isStatic;
        ApplyHistoryButton.IsEnabled = isStatic;
    }

    private void UpdateDnsFieldsEnabled()
    {
        var isStatic = DnsStaticRadio.IsChecked == true;
        DnsBox.IsEnabled = isStatic;
        DnsPresetCombo.IsEnabled = isStatic;
    }

    // =====================================================================
    // input helpers
    // =====================================================================

    /// <summary>
    /// The original rewrote 网关 on every keystroke to "&lt;first three octets&gt;.254",
    /// which destroyed a manually typed gateway as soon as the IP started matching
    /// (spec A.6). Same suggestion, but only while the field is empty or still holds a
    /// value this helper generated.
    /// </summary>
    private void OnIpTextChanged(object sender, TextChangedEventArgs e)
    {
        var prefix = FirstThreeOctets(SetIpBox.Text);
        if (prefix is null) return;

        var suggestion = prefix + ".254";
        if (GatewayBox.Text == suggestion)
        {
            _gatewayAutoFilled = true;
            return;
        }

        if (!_gatewayAutoFilled && GatewayBox.Text.Trim().Length > 0) return;
        SetGatewayText(suggestion, auto: true);
    }

    private void OnGatewayTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingGateway) _gatewayAutoFilled = false; // the user typed it themselves
    }

    private void SetGatewayText(string text, bool auto)
    {
        _settingGateway = true;
        GatewayBox.Text = text;
        _settingGateway = false;
        _gatewayAutoFilled = auto;
    }

    /// <summary>"192.168.1." prefix, i.e. the original regex <c>^\d{1,3}\.\d{1,3}\.\d{1,3}\.</c>.</summary>
    private static string? FirstThreeOctets(string text)
    {
        var parts = text.Split('.');
        if (parts.Length < 4) return null;

        for (var i = 0; i < 3; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3) return null;
            foreach (var c in part) if (c is < '0' or > '9') return null;
        }

        return $"{parts[0]}.{parts[1]}.{parts[2]}";
    }

    private void OnDnsPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        var index = DnsPresetCombo.SelectedIndex;
        if (index < 0 || index >= DnsPresets.Length) return;
        DnsBox.Text = DnsPresets[index].Address;
    }

    /// <summary>Accepts the multiple-DNS form the field promises: commas, semicolons or spaces.</summary>
    private static List<string> ParseDnsList(string text) =>
    [
        .. text.Split([',', '，', ';', '；', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    ];

    // =====================================================================
    // history (spec A.4) — fills the form only, never applies
    // =====================================================================

    private void LoadHistory()
    {
        try
        {
            _history = AppServices.Current.History.Load();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(LocalIpPage), "读取历史记录失败: " + ex.Message);
            _history = [];
        }

        RefreshHistoryList();
    }

    private void RefreshHistoryList()
    {
        _suppressHistoryEvents = true;
        try
        {
            HistoryList.ItemsSource = null;
            HistoryList.ItemsSource = _history;
            HistoryList.SelectedItem = null;
        }
        finally
        {
            _suppressHistoryEvents = false;
        }

        HistoryHintText.Text = _history.Count == 0
            ? "暂无历史记录。成功应用一次静态IP后会自动记录（保存在 ipScaner_his.xml）。"
            : $"共 {_history.Count} 条历史记录：选中一条即可填入表单（不会自动应用），确认后点击【保存设置】。";
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHistoryEvents) return;
        if (HistoryList.SelectedItem is AdapterInfo record) FillFromHistory(record);
    }

    private void OnApplyHistoryClick(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not AdapterInfo record)
        {
            _ = UiKit.InfoAsync(XamlRoot, DialogTitle, "请先在【历史地址】列表中选中一条记录。");
            return;
        }

        FillFromHistory(record);
    }

    /// <summary>
    /// Copies a history record into the form for the <em>currently selected</em> adapter
    /// and switches both groups to their static modes. Nothing is written to the NIC —
    /// the original behaved the same way, and 【保存设置】 is still required.
    /// </summary>
    private void FillFromHistory(AdapterInfo record)
    {
        IpStaticRadio.IsChecked = true;
        DnsStaticRadio.IsChecked = true;

        SetIpBox.Text = record.IP;
        MaskBox.Text = record.SubnetMask;
        SetGatewayText(record.Gateway, auto: false);
        DnsBox.Text = record.DnsServers.Count > 0 ? string.Join(", ", record.DnsServers) : string.Empty;

        UpdateIpFieldsEnabled();
        UpdateDnsFieldsEnabled();

        StatusText.Text = $"历史记录（{record.Name} {record.IP}）已填入表单，尚未应用。";
    }

    // =====================================================================
    // validation + apply
    // =====================================================================

    /// <summary>
    /// Runs the original's validation in order (spec A.5) with the original's exact
    /// messages, but with strict IPv4 range checking instead of <c>Utility.ValidIP</c>.
    /// </summary>
    private bool TryBuildRequest(out Request request, out string error, out Control? focus)
    {
        request = default;
        error = string.Empty;
        focus = null;

        var staticIp = IpStaticRadio.IsChecked == true;
        var staticDns = DnsStaticRadio.IsChecked == true;

        var ip = SetIpBox.Text.Trim();
        var mask = MaskBox.Text.Trim();
        var gateway = GatewayBox.Text.Trim();
        var dnsText = DnsBox.Text.Trim();
        var dns = ParseDnsList(dnsText);

        if (staticIp)
        {
            if (ip.Length == 0 || mask.Length == 0 || gateway.Length == 0)
            {
                error = "请输入完整的IP信息";
                focus = ip.Length == 0 ? SetIpBox : mask.Length == 0 ? MaskBox : GatewayBox;
                return false;
            }

            if (dnsText.Length == 0)
            {
                error = "请输入DNS服务器地址";
                focus = DnsBox;
                return false;
            }

            if (!IpMath.IsValidIPv4(ip)) { error = "请输入正确的IP地址"; focus = SetIpBox; return false; }
            if (!IpMath.IsValidIPv4(mask)) { error = "请输入正确的子网掩码"; focus = MaskBox; return false; }
            if (!IpMath.IsValidIPv4(gateway)) { error = "请输入正确的网关地址"; focus = GatewayBox; return false; }
        }

        if (staticDns)
        {
            if (dns.Count == 0)
            {
                error = "请输入DNS服务器地址";
                focus = DnsBox;
                return false;
            }

            foreach (var server in dns)
            {
                if (IpMath.IsValidIPv4(server)) continue;
                error = $"请输入正确的DNS地址（\"{server}\" 不是合法的IPv4地址）";
                focus = DnsBox;
                return false;
            }
        }

        request = new Request(staticIp, ip, mask, gateway, staticDns, dns);
        return true;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (SelectedAdapter is not { } adapter)
        {
            await UiKit.InfoAsync(XamlRoot, DialogTitle, "请先选择要设置的网卡。");
            return;
        }

        if (!TryBuildRequest(out var request, out var error, out var focus))
        {
            await UiKit.InfoAsync(XamlRoot, DialogTitle, error);
            focus?.Focus(FocusState.Programmatic);
            return;
        }

        if (!AppServices.Current.IsElevated && !await ConfirmUnelevatedAsync()) return;

        await ApplyAsync(adapter, request);
    }

    /// <summary>
    /// netsh set address/set dns need an elevated token (the original simply relaunched
    /// itself with <c>runas</c> and died if UAC was declined). Here the user chooses.
    /// </summary>
    private async Task<bool> ConfirmUnelevatedAsync()
    {
        var content = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "当前程序未以管理员身份运行，netsh 很可能以“访问被拒绝”失败，设置不会生效。\n\n"
                 + "· 【继续尝试】仍会执行，并显示 netsh 的真实返回结果；\n"
                 + "· 【以管理员身份重启】将通过 UAC 提权重启本程序。",
        };

        var choice = await UiKit.ShowContentAsync(
            XamlRoot, DialogTitle, content, "继续尝试", "以管理员身份重启", "取消");

        if (choice == ContentDialogResult.Secondary)
        {
            UiKit.RestartElevated();
            return false;
        }

        return choice == ContentDialogResult.Primary;
    }

    private async Task ApplyAsync(AdapterInfo adapter, Request request)
    {
        SetBusy(true, "正在应用设置…");

        var cts = new CancellationTokenSource();
        _cts = cts;

        var lines = new List<string>();
        var success = true;

        try
        {
            var config = AppServices.Current.NetworkConfig;

            // The original always issued both commands for one click — address first,
            // then DNS — so a DHCP address with a static DNS stays possible.
            var addressResult = request.StaticIp
                ? await config.SetStaticAsync(_netshName, request.Ip, request.Mask, request.Gateway, cts.Token)
                : await config.SetDhcpAsync(_netshName, cts.Token);
            success &= addressResult.Success;
            lines.Add(Describe("IP地址", addressResult));

            var dnsResult = request.StaticDns
                ? await config.SetDnsStaticAsync(_netshName, request.DnsServers, cts.Token)
                : await config.SetDnsDhcpAsync(_netshName, cts.Token);
            success &= dnsResult.Success;
            lines.Add(Describe("DNS", dnsResult));

            if (success && request.StaticIp)
            {
                // Spec A.4: only a static-IP apply is persisted, unique on (Name, IP),
                // and the file stays ipScaner_his.xml next to the exe.
                AppServices.Current.History.Add(new AdapterInfo
                {
                    Name = adapter.Name,
                    IP = request.Ip,
                    SubnetMask = request.Mask,
                    Gateway = request.Gateway,
                    Dns = string.Join(",", request.DnsServers),
                });
            }
        }
        catch (OperationCanceledException)
        {
            success = false;
            lines.Add("操作已取消，网卡配置可能只应用了一部分。");
        }
        catch (Exception ex)
        {
            success = false;
            AppLog.Instance.Log(nameof(LocalIpPage), "应用网络设置失败: " + ex.Message);
            lines.Add("应用网络设置失败：" + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            SetBusy(false);
        }

        var body = success
            ? "本地IP地址修改成功\n\n" + string.Join("\n", lines)
            : "本地IP地址修改失败，以下是 netsh 的实际返回：\n\n" + string.Join("\n", lines);

        StatusText.Text = success ? "本地IP地址修改成功" : "本地IP地址修改失败，请查看提示";

        // Spec A.3: re-read the adapters so the combo shows the applied values.
        if (success)
        {
            await ReloadAdaptersAsync();
            LoadHistory();
            StatusText.Text = "本地IP地址修改成功";
        }

        await UiKit.InfoAsync(XamlRoot, DialogTitle, body);
    }

    private static string Describe(string what, NetConfigResult result) =>
        result.Success
            ? $"[成功] {what}：{result.Message}"
            : $"[失败] {what}：{result.Message}";

    // =====================================================================
    // busy / misc
    // =====================================================================

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        SaveButton.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
        AdapterCombo.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (SelectedAdapter is not { } adapter) return;

        ShowAdapter(adapter);
        StatusText.Text = "已按当前网卡的实际信息重新填充表单。";
    }

    private void OnRestartElevatedClick(object sender, RoutedEventArgs e) => UiKit.RestartElevated();
}
