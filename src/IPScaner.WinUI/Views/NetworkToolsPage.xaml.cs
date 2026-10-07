using IPScaner.Core.Logging;
using IPScaner.Core.Models;
using IPScaner.Core.Net;
using IPScaner.Core.Shell;
using IPScaner.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IPScaner.WinUI.Views;

/// <summary>
/// 网络工具 — DNS 一键切换 + SSH / RDP 快捷连接 + 常用诊断命令。
/// </summary>
/// <remarks>
/// <para>DNS 区域：网卡列表来自 <see cref="AppServices.AdapterList"/>，预设卡片来自
/// <see cref="DnsConfigurator.Presets"/>，单击卡片直接调用
/// <see cref="DnsConfigurator.ApplyAsync"/>，并把 <see cref="NetConfigResult.Message"/>
/// 原样显示 —— netsh 报什么就说什么，不做任何"看起来成功了"的包装。
/// <see cref="DnsConfigurator.GetCurrentAsync"/> 用于回显该网卡当前真实的 DNS。</para>
/// <para>SSH / RDP 区域：主机地址沿用端口扫描页交接的
/// <c>NavigationArgs.TakePortScanHost()</c>（取一次就清空，避免下次进页面又被填上）。
/// 当 <see cref="RemoteLauncher.IsSshClientAvailable"/> /
/// <see cref="RemoteLauncher.IsRdpAvailable"/> 为 false 时，对应按钮会被禁用并给出
/// 原因，而不是点了没反应。</para>
/// <para>诊断命令直接复用 <see cref="ShellLauncher.RunCommand"/>，命令串与 Core 的
/// <c>RunHostAction</c> 保持一致，因此行为和主界面右键菜单里的同名命令完全相同。
/// 唯一的例外是 telnet：那里固定 23 端口，这里允许跟随"SSH 端口"输入框，端口不对时
/// 用户能自己改。</para>
/// </remarks>
public sealed partial class NetworkToolsPage : Page
{
    /// <summary>Tooltip shown when ssh.exe is not installed.</summary>
    private const string SshMissingHint =
        "未找到 ssh.exe（OpenSSH 客户端）。请在「设置 → 系统 → 可选功能」中安装「OpenSSH 客户端」。";

    /// <summary>Tooltip shown when mstsc.exe is not available.</summary>
    private const string RdpMissingHint =
        "未找到 mstsc.exe（远程桌面连接），此 Windows 版本可能未安装该组件。";

    private readonly DnsConfigurator _dns = new();
    private readonly RemoteLauncher _remote = new();

    private IReadOnlyList<AdapterInfo> _adapters = [];
    private readonly List<Button> _presetCards = [];
    private CancellationTokenSource? _cts;

    public NetworkToolsPage()
    {
        InitializeComponent();

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;

        BuildPresetCards();
        ApplyClientAvailability();

        // The port-scan page hands a host over; consumed exactly once.
        var pendingHost = NavigationArgs.TakePortScanHost();
        if (!string.IsNullOrWhiteSpace(pendingHost))
        {
            HostBox.Text = pendingHost;
            StatusText.Text = $"已从端口扫描页带入主机 {pendingHost}";
        }
    }

    // =====================================================================
    // page lifecycle
    // =====================================================================

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        ElevationBar.IsOpen = !AppServices.Current.IsElevated;
        await ReloadAdaptersAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* the operation already finished */ }
    }

    // =====================================================================
    // DNS — presets
    // =====================================================================

    /// <summary>
    /// Builds one clickable card per <see cref="DnsConfigurator.Presets"/> entry.
    /// Built in code rather than XAML so the card set always follows the Core list.
    /// </summary>
    private void BuildPresetCards()
    {
        PresetPanel.Children.Clear();
        PresetPanel.RowDefinitions.Clear();
        _presetCards.Clear();

        var slot = 0;
        var row = 0;

        foreach (var preset in DnsConfigurator.Presets)
        {
            var name = new TextBlock
            {
                Text = preset.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var servers = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            };
            servers.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "主 " + preset.Primary,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
            });
            servers.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "    备 " + preset.Secondary,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

            var note = new TextBlock
            {
                Text = preset.Note,
                Style = (Style)Application.Current.Resources["HintTextStyle"],
                Margin = new Thickness(0, 2, 0, 0),
            };

            var content = new StackPanel();
            content.Children.Add(name);
            content.Children.Add(servers);
            content.Children.Add(note);

            var card = new Button
            {
                Content = content,
                Tag = preset,
                Padding = new Thickness(12, 8, 12, 8),
                MinHeight = 76,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, "应用 " + preset.Name);
            ToolTipService.SetToolTip(card, $"单击即可把 {preset.Name} 写入所选网卡（{preset.Primary} / {preset.Secondary}）");
            card.Click += OnPresetClick;

            PlaceCard(card, slot, ref row);
            slot++;
        }

        // 恢复自动获取 shares the card rhythm with the presets; the custom-DNS action
        // stays next to its input box, which is where users look for it.
        PlaceCard(BuildActionCard(
            "恢复自动获取",
            "清除手动 DNS，改回由 DHCP / 路由器下发。",
            "恢复自动获取",
            OnResetDnsClick), slot, ref row);
    }

    /// <summary>Drops a card into the two-column preset grid, growing rows as needed.</summary>
    private void PlaceCard(Button card, int slot, ref int row)
    {
        var column = slot % 2;
        if (column == 0)
        {
            PresetPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row = PresetPanel.RowDefinitions.Count - 1;
        }

        card.Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 0 ? 4 : 0, 8);
        Grid.SetColumn(card, column);
        Grid.SetRow(card, row);
        PresetPanel.Children.Add(card);
        _presetCards.Add(card);
    }

    private static Button BuildActionCard(string title, string description, string automationName, RoutedEventHandler handler)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Application.Current.Resources["HintTextStyle"],
            Margin = new Thickness(0, 2, 0, 0),
        });

        var card = new Button
        {
            Content = content,
            Padding = new Thickness(12, 8, 12, 8),
            MinHeight = 76,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, automationName);
        card.Click += handler;
        return card;
    }

    private async void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DnsPreset preset) return;
        if (!TryGetAdapter(out var adapter)) return;
        if (!EnsureElevated("切换 DNS")) return;

        await RunDnsOperationAsync($"正在把 {adapter.Name} 的 DNS 切换为 {preset.Name}…",
            ct => _dns.ApplyAsync(adapter.Name, preset, ct));
    }

    private async void OnResetDnsClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetAdapter(out var adapter)) return;
        if (!EnsureElevated("恢复自动获取")) return;

        await RunDnsOperationAsync($"正在把 {adapter.Name} 的 DNS 恢复为自动获取…",
            ct => _dns.ResetToAutomaticAsync(adapter.Name, ct));
    }

    private async void OnApplyCustomClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetAdapter(out var adapter)) return;
        if (!EnsureElevated("自定义 DNS")) return;

        var servers = ParseServers(CustomDnsBox.Text);
        if (servers.Count == 0)
        {
            Report(false, "请输入至少一个 DNS 服务器地址，例如 223.5.5.5, 119.29.29.29。");
            return;
        }

        var invalid = servers.FirstOrDefault(s => !IpMath.IsValidIPv4(s));
        if (invalid is not null)
        {
            Report(false, $"「{invalid}」不是合法的 IPv4 地址，未做任何修改。");
            return;
        }

        await RunDnsOperationAsync($"正在写入自定义 DNS：{string.Join(", ", servers)}…",
            ct => _dns.ApplyCustomAsync(adapter.Name, servers, ct));
    }

    /// <summary>Splits a hand-typed DNS list on every separator a user might use.</summary>
    private static List<string> ParseServers(string? text)
        => [.. (text ?? string.Empty)
            .Split([',', '，', ';', '；', ' ', '\t', '\r', '\n', '、'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private async Task RunDnsOperationAsync(string status, Func<CancellationToken, Task<NetConfigResult>> operation)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _cts = cts;
        SetBusy(true, status);

        NetConfigResult result;
        try
        {
            result = await operation(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new NetConfigResult(false, "操作超时（30 秒）或已取消，DNS 可能只应用了一部分。", -1);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(NetworkToolsPage), "应用 DNS 失败: " + ex.Message);
            result = new NetConfigResult(false, "应用 DNS 失败：" + ex.Message, -1);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            SetBusy(false);
        }

        Report(result.Success, result.Message, result.Success ? "DNS 已更新" : "DNS 更新失败");

        // Re-read either way: after a partial failure the real state matters most.
        await RefreshCurrentDnsAsync();
    }

    // =====================================================================
    // DNS — adapters
    // =====================================================================

    private async void OnRefreshAdaptersClick(object sender, RoutedEventArgs e) => await ReloadAdaptersAsync();

    private async Task ReloadAdaptersAsync()
    {
        SetBusy(true, "正在读取网卡信息…");
        try
        {
            _adapters = await Task.Run(() => AppServices.Current.RefreshAdapters());
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(NetworkToolsPage), "枚举网卡失败: " + ex.Message);
            _adapters = [];
        }
        finally
        {
            SetBusy(false);
        }

        var previous = (AdapterCombo.SelectedItem as ComboBoxItem)?.Tag as AdapterInfo;

        AdapterCombo.SelectionChanged -= OnAdapterChanged;
        AdapterCombo.Items.Clear();
        foreach (var adapter in _adapters)
        {
            AdapterCombo.Items.Add(new ComboBoxItem
            {
                Content = DescribeAdapter(adapter),
                Tag = adapter,
            });
        }

        var index = previous is null ? 0 : IndexOfAdapter(previous.Name);
        AdapterCombo.SelectedIndex = _adapters.Count == 0 ? -1 : Math.Max(0, index);
        AdapterCombo.SelectionChanged += OnAdapterChanged;

        if (_adapters.Count == 0)
        {
            CurrentDnsText.Text = "当前 DNS：没有检测到任何网卡";
            StatusText.Text = "没有检测到网卡，DNS 功能不可用。";
            return;
        }

        await RefreshCurrentDnsAsync();
    }

    private async void OnAdapterChanged(object sender, SelectionChangedEventArgs e) => await RefreshCurrentDnsAsync();

    private async Task RefreshCurrentDnsAsync()
    {
        if (!TryGetAdapter(out var adapter, quiet: true))
        {
            CurrentDnsText.Text = "当前 DNS：未选择网卡";
            return;
        }

        CurrentDnsText.Text = "当前 DNS：读取中…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var servers = await _dns.GetCurrentAsync(adapter.Name, cts.Token);
            CurrentDnsText.Text = servers.Count == 0
                ? $"当前 DNS：{adapter.Name} 使用自动获取（未配置手动 DNS）"
                : $"当前 DNS：{string.Join("，", servers)}";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(NetworkToolsPage), "读取当前 DNS 失败: " + ex.Message);
            CurrentDnsText.Text = "当前 DNS：读取失败";
        }
    }

    private bool TryGetAdapter(out AdapterInfo adapter, bool quiet = false)
    {
        adapter = (AdapterCombo.SelectedItem as ComboBoxItem)?.Tag as AdapterInfo ?? new AdapterInfo();
        if (!string.IsNullOrEmpty(adapter.Name)) return true;

        if (!quiet) Report(false, "请先选择一个网卡。");
        return false;
    }

    /// <summary>Position of an adapter in the current list, or 0 when it vanished.</summary>
    private int IndexOfAdapter(string name)
    {
        for (var i = 0; i < _adapters.Count; i++)
        {
            if (string.Equals(_adapters[i].Name, name, StringComparison.Ordinal)) return i;
        }
        return 0;
    }

    /// <summary>"名称 IP · 已断开" — the label the adapter picker shows.</summary>
    private static string DescribeAdapter(AdapterInfo adapter)
    {
        var text = adapter.Name + "  (" + adapter.IP + ")";
        if (!adapter.IsUp) text += "  · 已断开";
        return text;
    }

    private bool EnsureElevated(string what)
    {
        if (AppServices.Current.IsElevated) return true;
        Report(false, $"{what}需要管理员权限：当前进程未提权，netsh 会拒绝这次修改。" +
                      "可以点击上方的【以管理员身份重启】。");
        return false;
    }

    // =====================================================================
    // SSH / RDP
    // =====================================================================

    private void ApplyClientAvailability()
    {
        var ssh = _remote.IsSshClientAvailable;
        var rdp = _remote.IsRdpAvailable;

        SshButton.IsEnabled = ssh;
        if (!ssh) ToolTipService.SetToolTip(SshButton, SshMissingHint);

        RdpButton.IsEnabled = rdp;
        RdpFileButton.IsEnabled = rdp;
        if (!rdp)
        {
            ToolTipService.SetToolTip(RdpButton, RdpMissingHint);
            ToolTipService.SetToolTip(RdpFileButton, RdpMissingHint);
        }

        var parts = new List<string>();
        if (!ssh) parts.Add("未检测到 OpenSSH 客户端，SSH 已禁用");
        if (!rdp) parts.Add("未检测到远程桌面连接，RDP 已禁用");
        ClientStatusText.Text = parts.Count == 0
            ? "SSH 与远程桌面客户端均可用"
            : string.Join("；", parts) + "。";
    }

    private async void OnUseScanHostClick(object sender, RoutedEventArgs e)
    {
        // The constructor already consumed any pending host; if the field is empty by
        // now the most likely intent is "use what I just copied".
        var host = (HostBox.Text ?? string.Empty).Trim();
        if (!IpMath.IsValidIPv4(host))
        {
            var clipboard = await UiKit.ReadClipboardAsync();
            var candidate = (clipboard ?? string.Empty).Trim();
            if (IpMath.IsValidIPv4(candidate))
            {
                HostBox.Text = candidate;
                StatusText.Text = $"已从剪贴板填入主机 {candidate}";
                return;
            }

            Report(false, "没有可用的主机地址：端口扫描页没有交接地址，剪贴板里也没有 IPv4 地址。");
            return;
        }

        StatusText.Text = $"当前主机 {host}";
    }

    private void OnOpenSshClick(object sender, RoutedEventArgs e)
    {
        if (!_remote.IsSshClientAvailable)
        {
            Report(false, SshMissingHint);
            return;
        }
        if (!TryGetHost(out var host)) return;

        if (!TryGetSshPort(out var port))
        {
            Report(false, "SSH 端口必须是 1 - 65535 之间的整数。");
            return;
        }

        var user = (UserBox.Text ?? string.Empty).Trim();
        var started = _remote.LaunchSsh(host, user.Length == 0 ? null : user, port);
        Report(started, started
            ? $"已启动 SSH 连接：{(user.Length == 0 ? host : user + "@" + host)} (端口 {port})"
            : $"启动 SSH 失败：无法运行 ssh.exe。");
    }

    private void OnOpenRdpClick(object sender, RoutedEventArgs e)
    {
        if (!_remote.IsRdpAvailable)
        {
            Report(false, RdpMissingHint);
            return;
        }
        if (!TryGetHost(out var host)) return;

        var user = (UserBox.Text ?? string.Empty).Trim();
        var started = _remote.LaunchRdp(host, user.Length == 0 ? null : user);
        Report(started, started
            ? $"已启动远程桌面连接：{host}"
            : $"启动远程桌面失败：无法运行 mstsc.exe。");
    }

    private void OnOpenRdpFileClick(object sender, RoutedEventArgs e)
    {
        if (!_remote.IsRdpAvailable)
        {
            Report(false, RdpMissingHint);
            return;
        }
        if (!TryGetHost(out var host)) return;

        var user = (UserBox.Text ?? string.Empty).Trim();
        var started = _remote.LaunchRdpFile(host, user.Length == 0 ? null : user);
        Report(started, started
            ? $"已用 .rdp 文件方式连接：{host}"
            : $"生成或打开 .rdp 文件失败：{host}");
    }

    private bool TryGetHost(out string host)
    {
        host = (HostBox.Text ?? string.Empty).Trim();
        if (IpMath.IsValidIPv4(host)) return true;

        Report(false, host.Length == 0
            ? "请先填写目标主机地址，例如 192.168.1.10。"
            : $"「{host}」不是合法的 IPv4 地址，未启动任何连接。");
        return false;
    }

    private bool TryGetSshPort(out int port)
    {
        port = 22;
        var text = (SshPortBox.Text ?? string.Empty).Trim();
        if (text.Length == 0) return true;
        return int.TryParse(text, out port) && port is > 0 and <= 65535;
    }

    // =====================================================================
    // 快速诊断命令
    // =====================================================================

    /// <summary>The shell launcher from the shared service container.</summary>
    private static ShellLauncher ShellRunner => AppServices.Current.Shell;

    private void OnPingClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetHost(out var host)) return;
        Run($"ping {host} -n {Math.Clamp(AppServices.Current.Config.PingCount, 1, 100)} &pause",
            $"正在 ping {host}", $"已打开 ping 窗口：{host}");
    }

    private void OnTracertClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetHost(out var host)) return;
        Run($"tracert {host} &pause", $"正在 tracert {host}", $"已打开 tracert 窗口：{host}");
    }

    private void OnTelnetClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetHost(out var host)) return;
        var port = TryGetSshPort(out var parsed) ? parsed : 23;
        Run($"telnet {host} {port} &pause", $"正在 telnet {host} {port}",
            $"已打开 telnet 窗口：{host} {port}（若提示找不到 telnet，请先安装 telnet 客户端）");
    }

    private void OnNetstatClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetHost(out var host)) return;
        Run($"netstat -ano | findstr {host} &pause", $"正在筛选与 {host} 相关的连接",
            $"已打开 netstat 窗口：与 {host} 相关的条目");
    }

    private void OnArpClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetHost(out var host)) return;
        Run($"arp -a {host} &pause", $"正在查询 {host} 的 ARP 记录",
            $"已打开 arp 窗口：{host}");
    }

    private void OnPortTestClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetHost(out var host)) return;

        // The remote host is already a validated dotted quad, so it cannot escape the
        // single-quoted PowerShell literal.
        var script =
            $"1..1024 | ForEach-Object {{ $r = Test-NetConnection -ComputerName '{host}' -Port $_ " +
            "-WarningAction SilentlyContinue -InformationLevel Quiet; " +
            "if ($r) { Write-Host \"[开放] $_\" } }}; Write-Host '端口测试完成（1-1024）'; pause";

        Run($"powershell -NoProfile -Command \"{script}\"", $"正在测试 {host} 的常用端口",
            $"已打开端口测试窗口：{host}（1-1024）");
    }

    private void Run(string command, string status, string done)
    {
        ShellRunner.RunCommand(command, visible: true);
        StatusText.Text = done;
        Report(true, done, status);
    }

    // =====================================================================
    // shared helpers
    // =====================================================================

    private void OnRestartElevatedClick(object sender, RoutedEventArgs e) => UiKit.RestartElevated();

    /// <summary>
    /// Reports an outcome. <paramref name="message"/> is the Core service's own text,
    /// shown verbatim so a failed netsh call cannot look like a success.
    /// </summary>
    private void Report(bool success, string message, string? status = null)
    {
        ResultBar.IsOpen = false;
        ResultBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ResultBar.Title = success ? "操作成功" : "操作失败";

        // InfoBar.Message is a plain string property; the control wraps it itself.
        ResultBar.Message = message;
        ResultBar.IsOpen = true;

        StatusText.Text = status ?? message;

        try { App.MainWindow?.SetStatus(success ? "网络工具：操作成功" : "网络工具：操作失败，详见提示"); }
        catch (Exception ex) { AppLog.Instance.Log(nameof(NetworkToolsPage), "更新状态栏失败: " + ex.Message); }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        AdapterCombo.IsEnabled = !busy;
        RefreshAdapterButton.IsEnabled = !busy;
        ApplyCustomButton.IsEnabled = !busy;

        // Grid has no IsEnabled (it is not a Control), so the cards are toggled
        // individually — this is also why they are kept in a list.
        foreach (var card in _presetCards) card.IsEnabled = !busy;

        if (status is not null) StatusText.Text = status;
    }
}
