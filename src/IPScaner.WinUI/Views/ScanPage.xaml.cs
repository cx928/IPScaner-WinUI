using System.Collections.ObjectModel;
using IPScaner.Core.Configuration;
using IPScaner.Core.Export;
using IPScaner.Core.Models;
using IPScaner.Core.Net;
using IPScaner.WinUI.Services;
using IPScaner.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace IPScaner.WinUI.Views;

/// <summary>
/// 主界面 — the /24 colour-block scanner.
/// </summary>
/// <remarks>
/// Interaction model carried over from the original:
/// <list type="bullet">
/// <item>the grid always holds blocks 1..254 (network and broadcast are never probed),</item>
/// <item>the block background encodes reachability,</item>
/// <item>a single click probes that host and copies its IP,</item>
/// <item>a double click runs the user's configured <see cref="EventName"/> action, and</item>
/// <item>the gap between the two is the configurable
/// <see cref="AppConfig.DoubleClickTime"/>, not the OS default.</item>
/// </list>
/// Unlike the original, cancelling a scan also discards in-flight results, so a
/// late reply can no longer recolour a block against a changed IP segment.
/// </remarks>
public sealed partial class ScanPage : Page
{
    private const int FirstHost = 1;
    private const int LastHost = 254;

    private readonly Dictionary<string, IpBlock> _byIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _singleClickTimer = new();

    private AppConfigColors _colors;
    private CancellationTokenSource? _scanCts;
    private bool _running;
    private int _icmpFailures;
    private bool _icmpWarningShown;
    private IpBlock? _pendingClickBlock;
    private IpBlock? _menuTargetBlock;
    private MenuFlyout? _blockMenu;

    /// <summary>The 254 colour blocks bound to the grid.</summary>
    public ObservableCollection<IpBlock> Blocks { get; } = [];

    public ScanPage()
    {
        InitializeComponent();

        _colors = AppConfigColors.From(AppServices.Current.Config);
        BuildBlocks();
        BuildBlockMenu();

        _singleClickTimer.Tick += OnSingleClickElapsed;

        Loaded += OnPageLoaded;
        Unloaded += (_, _) => StopScan();
    }

    // =====================================================================
    // setup
    // =====================================================================

    private void BuildBlocks()
    {
        Blocks.Clear();
        _byIp.Clear();

        for (var i = FirstHost; i <= LastHost; i++)
        {
            var block = new IpBlock(i);
            Blocks.Add(block);
            _byIp[block.Ip] = block;
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        ApplyConfigToUi();
        RefreshLegend();
        PopulateAdapters();

        if (string.IsNullOrWhiteSpace(SegmentBox.Text))
        {
            SegmentBox.Text = AppServices.Current.GetDefaultSegment();
            Resegment();
        }
    }

    private void ApplyConfigToUi()
    {
        var config = AppServices.Current.Config;
        _colors = AppConfigColors.From(config);
        HostNameToggle.IsChecked = config.QueryHostNameEnabled;
        LegendOnline.Background = UiKit.BrushFromArgb(config.NetworkOKColorArgb);
        LegendOffline.Background = UiKit.BrushFromArgb(config.NetworkNGColorArgb);
        LegendPending.Background = UiKit.BrushFromArgb(config.DefaultColorArgb);
        foreach (var block in Blocks) block.RefreshColors(_colors);
    }

    private void PopulateAdapters()
    {
        var adapters = AppServices.Current.AdapterList;
        if (adapters.Count == 0) adapters = AppServices.Current.RefreshAdapters();

        AdapterCombo.Items.Clear();
        foreach (var adapter in adapters)
        {
            var segment = IpMath.GetSegment(adapter.IP);
            if (!IpMath.IsValidSegment(segment)) continue;
            AdapterCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{adapter.Name}  ({adapter.IP})",
                Tag = segment,
            });
        }

        // Highlight the entry matching whatever is currently in the box.
        foreach (var item in AdapterCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag as string == SegmentBox.Text) { AdapterCombo.SelectedItem = item; break; }
        }
    }

    private void OnAdapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterCombo.SelectedItem is ComboBoxItem { Tag: string segment })
        {
            SegmentBox.Text = segment;
            Resegment();
        }
    }

    /// <summary>Re-points every block at the current segment and clears results.</summary>
    private void Resegment()
    {
        var segment = IpMath.GetSegment(SegmentBox.Text);
        if (!IpMath.IsValidSegment(segment)) return;

        SegmentBox.Text = segment;
        var localAddresses = AppServices.Current.AdapterList
            .Select(a => a.IP)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var block in Blocks)
        {
            block.Segment = segment;
            block.Status = HostStatus.Pending;
            block.HostName = string.Empty;
            block.Mac = string.Empty;
            block.Memo = AppServices.Current.Memo.Lookup(null, block.Ip);
            block.IsLocalMachine = localAddresses.Contains(block.Ip);
            block.RefreshColors(_colors);
        }

        _byIp.Clear();
        foreach (var block in Blocks) _byIp[block.Ip] = block;
        RefreshLegend();
    }

    // =====================================================================
    // scanning
    // =====================================================================

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        var segment = IpMath.GetSegment(SegmentBox.Text);
        if (!IpMath.IsValidSegment(segment))
        {
            await UiKit.InfoAsync(XamlRoot, "局域网IP扫描工具", "请输入IP段信息，比如192.168.1");
            return;
        }

        SegmentBox.Text = segment;
        Resegment();

        var config = AppServices.Current.Config;
        var targets = IpMath.GetSegmentHosts(segment, FirstHost, LastHost);

        _running = true;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SegmentBox.IsEnabled = false;
        _icmpFailures = 0;
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        var progress = new Progress<ScanProgress>(p =>
            ProgressText.Text = $"正在扫描 {p.CurrentIP}  ({p.Completed}/{p.Total})");

        try
        {
            await AppServices.Current.Scanner.RunAsync(
                targets,
                config,
                result =>
                {
                    // ScanEngine reports from background threads; marshal to the UI.
                    DispatcherQueue.TryEnqueue(() => ApplyResult(result, cts));
                    return Task.CompletedTask;
                },
                progress,
                ScanEngine.DefaultConcurrency,
                cts.Token);

            ProgressText.Text = cts.IsCancellationRequested ? "用户取消了操作" : "扫描完毕";

            // A machine-wide ICMP block (some endpoint-security products) makes
            // every ping raise, so the whole segment looks offline. The v1.28
            // changelog added the ARP/TCP fallbacks for exactly this situation,
            // but both default to off — so say something instead of quietly
            // reporting 254 dead hosts.
            if (!cts.IsCancellationRequested) await WarnIfIcmpBlockedAsync(config, targets.Count);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "用户取消了操作";
        }
        catch (Exception ex)
        {
            Core.Logging.AppLog.Instance.Log(nameof(ScanPage), "扫描失败: " + ex.Message);
            ProgressText.Text = "扫描出错：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                _scanCts = null;
                _running = false;
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                SegmentBox.IsEnabled = true;
            }
            cts.Dispose();
        }
    }

    private void ApplyResult(HostResult result, CancellationTokenSource owner)
    {
        // Discard results from a superseded run.
        if (!ReferenceEquals(_scanCts, owner) || owner.IsCancellationRequested) return;

        // IPStatus.Unknown means the Ping call threw rather than replying.
        if (result.PingStatus == System.Net.NetworkInformation.IPStatus.Unknown) _icmpFailures++;

        if (!_byIp.TryGetValue(result.IP, out var block)) return;

        result.Memo = AppServices.Current.Memo.Lookup(result.Mac, result.IP);
        block.Apply(result, _colors);
        RefreshLegend();
    }

    /// <summary>
    /// Explains a sweep in which ICMP never worked at all and no fallback is
    /// enabled, because the result (every host 不通) is otherwise indistinguishable
    /// from a genuinely empty network.
    /// </summary>
    private async Task WarnIfIcmpBlockedAsync(AppConfig config, int targetCount)
    {
        if (_icmpWarningShown) return;
        if (targetCount == 0 || _icmpFailures < targetCount) return;
        if (config.PortInsteadPingEnabled || config.ARPInsteadPingEnabled) return;

        _icmpWarningShown = true;
        ProgressText.Text = "ICMP 被系统或安全软件拦截，全部主机显示为不通";

        var enable = await UiKit.ConfirmAsync(
            XamlRoot,
            "无法发送 Ping 请求",
            $"本机向全部 {targetCount} 个地址发送 ICMP 请求均失败（并非对端不回包，而是本机 Ping 调用被拦截，"
            + "常见于安装了安全软件或限制了原始套接字的环境）。\n\n"
            + "旧版同样的处理方式是启用备选侦测：Ping 失败时改用 TCP 端口或 ARP 表判断在线。"
            + "你的配置文件已保存该选项，是否现在启用【Ping失败时侦测端口】？",
            primaryText: "启用并重新扫描",
            closeText: "暂不");

        if (!enable) return;

        var clone = config.Clone();
        clone.PortInsteadPingEnabled = true;
        AppServices.Current.ApplyConfig(clone);
        OnStartClick(this, new RoutedEventArgs());
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopScan();

    private void StopScan()
    {
        try { _scanCts?.Cancel(); }
        catch { /* already gone */ }
    }

    private void OnHostNameToggled(object sender, RoutedEventArgs e)
    {
        var config = AppServices.Current.Config.Clone();
        config.QueryHostNameEnabled = HostNameToggle.IsChecked == true;
        AppServices.Current.ApplyConfig(config);
    }

    private void OnListViewToggled(object sender, RoutedEventArgs e)
    {
        var listMode = ListViewToggle.IsChecked == true;
        ResultList.Visibility = listMode ? Visibility.Visible : Visibility.Collapsed;
        BlockScroll.Visibility = listMode ? Visibility.Collapsed : Visibility.Visible;

        if (listMode) ResultList.ItemsSource = Blocks;
    }

    private void OnClearCachesClick(object sender, RoutedEventArgs e)
    {
        AppServices.Current.ClearCaches();
        ProgressText.Text = "已清除主机名与MAC缓存";
    }

    private void OnOpenMemoClick(object sender, RoutedEventArgs e) =>
        App.MainWindow?.NavigateTo("memo");

    // =====================================================================
    // legend
    // =====================================================================

    private void RefreshLegend()
    {
        var online = Blocks.Count(b => b.Status == HostStatus.Online);
        var offline = Blocks.Count(b => b.Status == HostStatus.Offline);
        var pending = Blocks.Count - online - offline;

        OnlineLink.Content = $"正常 {online}";
        OfflineLink.Content = $"不通 {offline}";
        PendingText.Text = $"待检测 {pending}";
        SummaryText.Text = pending == Blocks.Count
            ? "尚未扫描"
            : $"数量合计：正常 {online}，不通 {offline}";
    }

    private async void OnCopyOnlineClick(object sender, RoutedEventArgs e) =>
        await CopyByStatusAsync(HostStatus.Online, "正常");

    private async void OnCopyOfflineClick(object sender, RoutedEventArgs e) =>
        await CopyByStatusAsync(HostStatus.Offline, "不通");

    private async Task CopyByStatusAsync(HostStatus status, string label)
    {
        var matches = Blocks.Where(b => b.Status == status).ToList();
        if (matches.Count == 0)
        {
            await UiKit.InfoAsync(XamlRoot, "局域网IP扫描工具", $"当前没有状态为【{label}】的地址。");
            return;
        }

        var text = string.Join(Environment.NewLine, matches.Select(b =>
        {
            var memo = string.IsNullOrEmpty(b.Memo) ? string.Empty : "  " + b.Memo.Replace("\r", "").Replace("\n", "");
            return b.Ip + memo;
        }));

        UiKit.CopyToClipboard(text);
        await UiKit.InfoAsync(XamlRoot, "局域网IP扫描工具",
            $"IP段: {IpMath.GetSegment(SegmentBox.Text)}, 状态【{label}】地址共有{matches.Count}个，已复制剪贴板");
    }

    // =====================================================================
    // block interaction
    // =====================================================================

    private static IpBlock? ResolveBlock(object sender) =>
        (sender as FrameworkElement)?.DataContext as IpBlock;

    private void OnBlockTapped(object sender, TappedRoutedEventArgs e)
    {
        var block = ResolveBlock(sender);
        if (block is null) return;

        // A second Tapped for the same block means a double click is in flight;
        // DoubleTapped will handle it.
        if (ReferenceEquals(_pendingClickBlock, block) && _singleClickTimer.IsEnabled) return;

        _pendingClickBlock = block;
        _singleClickTimer.Interval =
            TimeSpan.FromMilliseconds(Math.Clamp(AppServices.Current.Config.DoubleClickTime, 100, 500));
        _singleClickTimer.Start();
    }

    private void OnSingleClickElapsed(object? sender, object e)
    {
        _singleClickTimer.Stop();
        var block = _pendingClickBlock;
        _pendingClickBlock = null;
        if (block is not null) _ = ProbeSingleBlockAsync(block);
    }

    /// <summary>Single click: probe this host now and copy its address.</summary>
    private async Task ProbeSingleBlockAsync(IpBlock block)
    {
        UiKit.CopyToClipboard(block.Ip);

        try
        {
            var config = AppServices.Current.Config;
            var result = await AppServices.Current.Scanner
                .ProbeOnceAsync(block.Ip, config, CancellationToken.None)
                .ConfigureAwait(true);

            result.Memo = AppServices.Current.Memo.Lookup(result.Mac, result.IP);
            block.Apply(result, _colors);
            RefreshLegend();
        }
        catch (Exception ex)
        {
            Core.Logging.AppLog.Instance.Log(nameof(ScanPage), "单点探测失败: " + ex.Message);
        }
    }

    private void OnBlockDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _singleClickTimer.Stop();
        _pendingClickBlock = null;

        var block = ResolveBlock(sender);
        if (block is null) return;

        var config = AppServices.Current.Config;
        AppServices.Current.Shell.RunHostAction(config.DoubleEvent, block.Ip, config.PingCount);
    }

    private void OnListItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is IpBlock block) _ = ProbeSingleBlockAsync(block);
    }

    // =====================================================================
    // context menu
    // =====================================================================

    private void BuildBlockMenu()
    {
        _blockMenu = new MenuFlyout();

        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "复制IP", Tag = "copy-ip" });
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "复制计算机名", Tag = "copy-name" });
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "复制MAC", Tag = "copy-mac" });
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "复制备注", Tag = "copy-memo" });
        _blockMenu.Items.Add(new MenuFlyoutSeparator());
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "Ping 命令", Tag = "ping" });
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "Tracert 命令", Tag = "tracert" });
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "Telnet 命令", Tag = "telnet" });
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "Netstat 命令", Tag = "netstat" });
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "ARP 命令", Tag = "arp" });
        _blockMenu.Items.Add(new MenuFlyoutSeparator());
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "浏览 http 网页", Tag = "web" });
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "访问共享目录", Tag = "share" });
        _blockMenu.Items.Add(new MenuFlyoutSeparator());
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "快速端口扫描", Tag = "portscan" });
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "设置备注…", Tag = "memo" });
        _blockMenu.Items.Add(new MenuFlyoutItem { Text = "重新检测", Tag = "probe" });

        foreach (var item in _blockMenu.Items.OfType<MenuFlyoutItem>())
        {
            item.Click += OnBlockMenuItemClick;
        }
    }

    private void OnBlockRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var block = ResolveBlock(sender);
        if (block is null || _blockMenu is null) return;

        _menuTargetBlock = block;

        // Enable copy actions only when there is something to copy.
        foreach (var item in _blockMenu.Items.OfType<MenuFlyoutItem>())
        {
            item.IsEnabled = item.Tag switch
            {
                "copy-name" => !string.IsNullOrEmpty(block.HostName),
                "copy-mac" => !string.IsNullOrEmpty(block.Mac),
                "copy-memo" => !string.IsNullOrEmpty(block.Memo),
                _ => true,
            };
        }

        _blockMenu.ShowAt((FrameworkElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
        e.Handled = true;
    }

    private async void OnBlockMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (_menuTargetBlock is not { } block) return;
        if (sender is not MenuFlyoutItem { Tag: string tag }) return;

        var config = AppServices.Current.Config;
        var shell = AppServices.Current.Shell;

        switch (tag)
        {
            case "copy-ip":
                UiKit.CopyToClipboard(block.Ip);
                break;
            case "copy-name":
                UiKit.CopyToClipboard(block.HostName);
                break;
            case "copy-mac":
                UiKit.CopyToClipboard(block.Mac);
                break;
            case "copy-memo":
                UiKit.CopyToClipboard(block.Memo);
                break;

            case "ping": shell.RunHostAction(EventName.Ping, block.Ip, config.PingCount); break;
            case "tracert": shell.RunHostAction(EventName.Tracert, block.Ip); break;
            case "telnet": shell.RunHostAction(EventName.Telnet, block.Ip); break;
            case "netstat": shell.RunHostAction(EventName.Netstat, block.Ip); break;
            case "arp": shell.RunHostAction(EventName.ARP, block.Ip); break;
            case "web": shell.RunHostAction(EventName.ViewWeb, block.Ip); break;
            case "share": shell.RunHostAction(EventName.Share, block.Ip); break;

            case "portscan":
                NavigationArgs.PendingPortScanHost = block.Ip;
                App.MainWindow?.NavigateTo("portscan");
                break;

            case "memo":
                await EditMemoAsync(block);
                break;

            case "probe":
                await ProbeSingleBlockAsync(block);
                break;
        }
    }

    private async Task EditMemoAsync(IpBlock block)
    {
        var input = new TextBox
        {
            Text = block.Memo,
            PlaceholderText = "例如：财务部打印机",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            Width = 320,
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = block.Ip, Opacity = 0.7 });
        panel.Children.Add(input);

        var result = await UiKit.ShowContentAsync(
            XamlRoot, "设置备注", panel, primaryText: "保存", closeText: "取消");

        if (result != ContentDialogResult.Primary) return;

        // The original stored a note against the MAC when one was known, so the
        // note follows the machine across DHCP leases (v1.27 behaviour).
        var key = !string.IsNullOrEmpty(block.Mac) ? block.Mac : block.Ip;
        AppServices.Current.Memo.Set(key, input.Text.Trim());
        AppServices.Current.Memo.Save();
        block.Memo = input.Text.Trim();
        block.RefreshColors(_colors);
    }

    // =====================================================================
    // export
    // =====================================================================

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = ExportFolder();
            Directory.CreateDirectory(folder);

            var headers = new[] { "IP", "状态", "主机名", "MAC", "备注" };
            var rows = Blocks
                .Select(b => new[] { b.Ip, HostStatusText.Short(b.Status), b.HostName, b.Mac, b.Memo })
                .ToList();

            var csvName = TableExporter.BuildFileName("IP扫描结果", ".csv");
            var csvPath = Path.Combine(folder, csvName);
            TableExporter.WriteCsv(csvPath, headers, rows);

            var xlsxPath = Path.Combine(folder, TableExporter.BuildFileName("IP扫描结果", ".xlsx"));
            TableExporter.WriteXlsx(xlsxPath, "IP扫描结果", headers, rows);

            var open = await UiKit.ConfirmAsync(XamlRoot, "导出成功",
                $"已导出 {rows.Count} 条记录到：\n{folder}\n\n是否打开所在文件夹？",
                "打开文件夹", "关闭");
            if (open) AppServices.Current.Shell.OpenFolder(folder);
        }
        catch (Exception ex)
        {
            Core.Logging.AppLog.Instance.Log(nameof(ScanPage), "导出失败: " + ex.Message);
            await UiKit.InfoAsync(XamlRoot, "导出失败", ex.Message);
        }
    }

    /// <summary>
    /// Prefers a 导出 folder beside the executable, falling back to the writable
    /// data directory (installed builds live under Program Files) and finally to
    /// Documents.
    /// </summary>
    private static string ExportFolder()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "导出"),
                     Path.Combine(IPScaner.Core.Storage.AppPaths.DataDirectory, "导出"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IPScaner", "导出"),
                 })
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".writetest");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                // try the next candidate
            }
        }

        return Path.Combine(Path.GetTempPath(), "IPScaner", "导出");
    }
}
