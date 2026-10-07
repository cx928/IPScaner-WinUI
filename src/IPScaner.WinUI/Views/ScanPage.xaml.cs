using System.Collections.ObjectModel;
using IPScaner.Core.Configuration;
using IPScaner.Core.Export;
using IPScaner.Core.Logging;
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
/// 主界面 — the /24 colour-block scanner, in four presentations.
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
///
/// <para>
/// Layout is adaptive in two layers, because the two have different notions of width:
/// </para>
/// <list type="number">
/// <item>the <c>VisualStateManager</c> / <c>AdaptiveTrigger</c> states in the XAML
/// react to the <i>window</i> width (&lt;640 / 640-1000 / &gt;1000) and reflow the
/// page frame, the subtitle and the legend;</item>
/// <item><see cref="ApplyResponsiveLayout"/> reacts to the width actually measured
/// for the results area, which is what the block columns, the tile size and the
/// table column set have to follow — the navigation pane takes a fixed 228 px, so
/// window-keyed breakpoints alone would overflow the toolbar on this display.</item>
/// </list>
/// </remarks>
public sealed partial class ScanPage : Page
{
    private const int FirstHost = 1;
    private const int LastHost = 254;

    // ---- measured-width thresholds (logical px), see ApplyResponsiveLayout ----
    // 展示方式 needs the SelectorBar's four labels plus the theme button.
    private const double SelectorBarMinWidth = 430;
    // The secondary actions are ~380 px of buttons and gaps.
    private const double ActionsInlineWidth = 700;
    private const double ActionsInlineTightWidth = 480;
    private const double ActionsInlineHeight = 540;
    // "IP段" is decoration once the box is the only thing on that row.
    private const double SegmentLabelMinWidth = 400;
    // Columns are dropped before they squeeze the flexible ones to nothing.
    private const double MacColumnMinWidth = 500;
    private const double SourceColumnMinWidth = 640;

    /// <summary>
    /// Width handed to the overlay scrollbar inside a results ScrollViewer. The
    /// grids are laid out from the measured width, so this has to be subtracted
    /// first — otherwise the layout rounds down to one fewer column.
    /// </summary>
    private const double ScrollbarAllowance = 16;

    private const double CardSpacing = 10;
    private const double TargetCardWidth = 240;

    private readonly Dictionary<string, IpBlock> _byIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _singleClickTimer = new();

    private AppConfigColors _colors;
    private CancellationTokenSource? _scanCts;
    private bool _running;
    private bool _hasResults;
    private bool _subscribed;
    private int _icmpFailures;
    private bool _icmpWarningShown;
    private IpBlock? _pendingClickBlock;
    private IpBlock? _menuTargetBlock;
    private MenuFlyout? _blockMenu;

    private ViewMode _viewMode = ViewMode.Blocks;
    private bool _syncingView;
    private bool? _colHostName;
    private bool? _colMac;
    private bool? _colMemo;
    private bool? _colSource;

    /// <summary>The 254 colour blocks; the single source for all four views.</summary>
    public ObservableCollection<IpBlock> Blocks { get; } = [];

    public ScanPage()
    {
        InitializeComponent();

        _colors = AppConfigColors.From(AppServices.Current.Config);
        BuildBlocks();
        BuildBlockMenu();
        BuildViewSelector();
        BuildThemeMenu();

        _singleClickTimer.Tick += OnSingleClickElapsed;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
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
            block.RefreshColors(_colors);
            Blocks.Add(block);
            _byIp[block.Ip] = block;
        }
    }

    /// <summary>
    /// Builds the 展示方式 pickers from <see cref="ViewModeText.All"/>, so the labels
    /// stay in step with the configuration enum instead of being retyped.
    /// </summary>
    private void BuildViewSelector()
    {
        foreach (var (value, text, _) in ViewModeText.All)
        {
            ViewSelector.Items.Add(new SelectorBarItem { Text = text, Tag = value });
            ViewCombo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        }
    }

    private void BuildThemeMenu()
    {
        var flyout = new MenuFlyout();
        foreach (var (value, text) in ThemeModeText.All)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "theme", Tag = value };
            item.Click += OnThemeItemClick;
            flyout.Items.Add(item);
        }

        ThemeButton.Flyout = flyout;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        ApplyConfigToUi();
        ApplyTheme(AppServices.Current.Config.ThemeMode, persist: false);

        // Restore the saved presentation before the first layout pass, so the page is
        // never shown in the wrong one.
        ApplyViewMode(AppServices.Current.Config.ViewMode, persist: false);

        PopulateAdapters();
        RefreshLegend();
        UpdateStatusUi();
        ApplyResponsiveLayout(ResultsHost.ActualWidth, ResultsHost.ActualHeight);

        if (string.IsNullOrWhiteSpace(SegmentBox.Text))
        {
            SegmentBox.Text = AppServices.Current.GetDefaultSegment();
            Resegment();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        StopScan();
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        AppServices.Current.ConfigChanged += OnConfigChanged;
        ThemeService.Changed += OnThemeChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        AppServices.Current.ConfigChanged -= OnConfigChanged;
        ThemeService.Changed -= OnThemeChanged;
    }

    /// <summary>
    /// Re-reads everything the configuration controls. Called on load and on any
    /// <see cref="AppServices.ConfigChanged"/>, so the 选项配置 page is reflected
    /// here without a restart.
    /// </summary>
    private void ApplyConfigToUi()
    {
        var config = AppServices.Current.Config;
        _colors = AppConfigColors.From(config);

        HostNameToggle.IsChecked = config.QueryHostNameEnabled;
        LegendOnline.Background = UiKit.BrushFromArgb(config.NetworkOKColorArgb);
        LegendOffline.Background = UiKit.BrushFromArgb(config.NetworkNGColorArgb);
        LegendPending.Background = UiKit.BrushFromArgb(config.DefaultColorArgb);
        foreach (var block in Blocks) block.RefreshColors(_colors);

        ApplyResponsiveLayout(ResultsHost.ActualWidth, ResultsHost.ActualHeight);
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        ApplyConfigToUi();
        if (config.ViewMode != _viewMode) ApplyViewMode(config.ViewMode, persist: false);
        if (config.ThemeMode != ThemeService.Current) ApplyTheme(config.ThemeMode, persist: false);
        RefreshLegend();
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
        if (AdapterCombo.SelectedItem is not ComboBoxItem { Tag: string segment }) return;

        // Re-selecting the segment already being scanned must not wipe the results —
        // PopulateAdapters() re-creates the items every time the page is re-loaded.
        if (string.Equals(IpMath.GetSegment(SegmentBox.Text), segment, StringComparison.OrdinalIgnoreCase)) return;

        SegmentBox.Text = segment;
        Resegment();
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
            block.Source = LivenessSource.None;
            block.TimeText = "Timeout";
            block.HostName = string.Empty;
            block.Mac = string.Empty;
            block.Memo = AppServices.Current.Memo.Lookup(null, block.Ip);
            block.IsLocalMachine = localAddresses.Contains(block.Ip);
            block.RefreshColors(_colors);
        }

        _byIp.Clear();
        foreach (var block in Blocks) _byIp[block.Ip] = block;

        _hasResults = false;
        RefreshLegend();
    }

    // =====================================================================
    // adaptive layout
    // =====================================================================

    private void OnResultsHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);

    /// <summary>
    /// Sizes the results surfaces from the width that is really available.
    /// </summary>
    /// <remarks>
    /// The block grid, the card tiles and the table columns all derive from the
    /// measured width rather than from a window breakpoint, because the navigation
    /// pane takes a fixed 228 px: at the default window size the page only receives
    /// ~560 logical px even though the window is ~787 logical px wide.
    /// <para>
    /// Column counts are computed here and handed to <c>UniformGridLayout</c> as a
    /// minimum item size; the layout then stretches the cells to fill the row, so
    /// the grid always ends flush with the right edge and never overflows.
    /// </para>
    /// </remarks>
    private void ApplyResponsiveLayout(double width, double height)
    {
        if (width <= 1) return;

        var config = AppServices.Current.Config;

        // ---- toolbar: selector style, overflow menu, decoration ------------
        var useSelectorBar = width >= SelectorBarMinWidth;
        ViewSelector.Visibility = Vis(useSelectorBar);
        ViewCombo.Visibility = Vis(!useSelectorBar);

        var inlineActions = width >= ActionsInlineWidth
                            || (width >= ActionsInlineTightWidth && height >= ActionsInlineHeight);
        ActionsPanel.Visibility = Vis(inlineActions);
        MoreButton.Visibility = Vis(!inlineActions);
        SegmentLabel.Visibility = Vis(width >= SegmentLabelMinWidth);

        // What a scrolling surface can really use: the overlay scrollbar sits on top
        // of the last few pixels, so every item-size calculation stays clear of it.
        var gridWidth = Math.Max(120, width - ScrollbarAllowance);

        // ---- 色块网格: columns first, block size derived from them ----------
        var blockWidth = Math.Clamp(gridWidth / 11.0, 38, 64);
        if (config.BlockSize > 0)
        {
            // A non-zero BlockSize is a percentage scale on the automatic size. Both
            // the scale and the result are clamped because the .cfg is hand-editable.
            blockWidth = Math.Clamp(blockWidth * Math.Clamp(config.BlockSize, 50, 200) / 100.0, 26, 96);
        }

        var blockHeight = Math.Clamp(Math.Round(blockWidth * 0.62), 22, 46);
        if (Math.Abs(BlockLayout.MinItemWidth - blockWidth) > 0.4
            || Math.Abs(BlockLayout.MinItemHeight - blockHeight) > 0.4)
        {
            BlockLayout.MinItemWidth = Math.Max(20, blockWidth - 2);
            BlockLayout.MinItemHeight = blockHeight;

            // The label inherits this FontSize (scroll viewer → button → text), so it
            // scales with the block. BtnFontSize stays the user's floor.
            var smallest = Math.Clamp(config.BtnFontSize, 7, 12);
            BlockScroll.FontSize = Math.Clamp(Math.Round(blockHeight * 0.42), smallest, 20);
        }

        // ---- 卡片视图: 1..4 tiles per row ---------------------------------
        var cardColumns = Math.Clamp((int)(gridWidth / TargetCardWidth), 1, 4);
        var cardWidth = (gridWidth - ((cardColumns - 1) * CardSpacing)) / cardColumns;
        if (Math.Abs(CardLayout.MinItemWidth - cardWidth) > 0.4)
        {
            CardLayout.MinItemWidth = Math.Max(180, cardWidth - 2);
            CardLayout.MinItemHeight = 108;
        }

        // ---- 详细列表 / 紧凑表格: drop the least useful columns first ------
        ApplyColumnVisibility(
            hostName: config.ShowHostNameColumn,
            mac: config.ShowMacColumn && width >= MacColumnMinWidth,
            memo: config.ShowMemoColumn,
            source: config.ShowSourceColumn && width >= SourceColumnMinWidth);
    }

    /// <summary>
    /// Applies one column set to the sticky header and to every host at once.
    /// </summary>
    /// <remarks>
    /// Keeping the decision in one place is what stops the header and the rows from
    /// drifting apart; a collapsed cell gives its width back to the row, because
    /// every fixed column is an <c>Auto</c> column wrapped around a sized cell.
    /// </remarks>
    private void ApplyColumnVisibility(bool hostName, bool mac, bool memo, bool source)
    {
        if (_colHostName == hostName && _colMac == mac && _colMemo == memo && _colSource == source) return;

        _colHostName = hostName;
        _colMac = mac;
        _colMemo = memo;
        _colSource = source;

        HeaderHost.Visibility = Vis(hostName);
        HeaderMac.Visibility = Vis(mac);
        HeaderMemo.Visibility = Vis(memo);
        HeaderSource.Visibility = Vis(source);

        foreach (var block in Blocks) block.ApplyColumns(hostName, mac, memo, source);
    }

    // =====================================================================
    // view mode  (色块网格 / 详细列表 / 紧凑表格 / 卡片视图)
    // =====================================================================

    private void OnViewSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingView) return;
        if (sender.SelectedItem?.Tag is ViewMode mode && mode != _viewMode) ApplyViewMode(mode, persist: true);
    }

    private void OnViewComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingView) return;
        if (ViewCombo.SelectedItem is ComboBoxItem { Tag: ViewMode mode } && mode != _viewMode)
        {
            ApplyViewMode(mode, persist: true);
        }
    }

    private void ApplyViewMode(ViewMode mode, bool persist)
    {
        _viewMode = mode;

        BlockScroll.Visibility = Vis(mode == ViewMode.Blocks);
        CardScroll.Visibility = Vis(mode == ViewMode.Cards);
        RowsHost.Visibility = Vis(mode is ViewMode.List or ViewMode.Table);

        if (mode is ViewMode.List or ViewMode.Table)
        {
            var dense = mode == ViewMode.Table;
            RowList.ItemContainerStyle = (Style)Resources[dense ? "TableRowContainerStyle" : "ListRowContainerStyle"];
            RowList.FontSize = dense ? 12 : 13.5;

            // Re-realise the containers so the new density shows immediately.
            RowList.ItemsSource = null;
            RowList.ItemsSource = Blocks;
        }

        SyncViewSelector(mode);
        UpdateEmptyState();
        if (persist) PersistViewMode(mode);
    }

    private void SyncViewSelector(ViewMode mode)
    {
        _syncingView = true;
        try
        {
            foreach (var item in ViewSelector.Items.OfType<SelectorBarItem>())
            {
                if (item.Tag is ViewMode value && value == mode) { ViewSelector.SelectedItem = item; break; }
            }

            foreach (var item in ViewCombo.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is ViewMode value && value == mode) { ViewCombo.SelectedItem = item; break; }
            }
        }
        finally
        {
            _syncingView = false;
        }
    }

    private void PersistViewMode(ViewMode mode)
    {
        var config = AppServices.Current.Config;
        if (config.ViewMode == mode) return;

        var clone = config.Clone();
        clone.ViewMode = mode;
        AppServices.Current.ApplyConfig(clone);
    }

    // =====================================================================
    // theme  (跟随系统 / 浅色 / 深色)
    // =====================================================================

    private void OnThemeItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: ThemeMode mode }) ApplyTheme(mode, persist: true);
    }

    private void ApplyTheme(ThemeMode mode, bool persist)
    {
        // The page stamps its own root; ThemeService also stamps the shell root, so
        // the navigation pane, title bar and status strip follow along.
        var theme = ThemeService.ElementFor(mode);
        RequestedTheme = theme;
        PageRoot.RequestedTheme = theme;
        ThemeService.Apply(mode);
        UpdateThemeMenu(mode);

        if (!persist) return;

        var config = AppServices.Current.Config;
        if (config.ThemeMode == mode) return;

        var clone = config.Clone();
        clone.ThemeMode = mode;
        AppServices.Current.ApplyConfig(clone);
    }

    private void OnThemeChanged(object? sender, ThemeMode mode)
    {
        var theme = ThemeService.ElementFor(mode);
        RequestedTheme = theme;
        PageRoot.RequestedTheme = theme;
        UpdateThemeMenu(mode);
    }

    private void UpdateThemeMenu(ThemeMode mode)
    {
        if (ThemeButton.Flyout is MenuFlyout flyout)
        {
            foreach (var item in flyout.Items.OfType<RadioMenuFlyoutItem>())
            {
                item.IsChecked = item.Tag is ThemeMode value && value == mode;
            }
        }

        ToolTipService.SetToolTip(ThemeButton, "界面主题：" + ThemeModeText.Describe(mode));
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
        _hasResults = true;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SegmentBox.IsEnabled = false;
        BusyRing.IsActive = true;
        BusyRing.Visibility = Visibility.Visible;
        UpdateEmptyState();
        _icmpFailures = 0;

        ScanProgress.Maximum = Math.Max(1, targets.Count);
        ScanProgress.Value = 0;

        var cts = new CancellationTokenSource();
        _scanCts = cts;

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressText.Text = $"正在扫描 {p.CurrentIP}  ({p.Completed}/{p.Total})";
            ScanProgress.Value = p.Completed;
        });

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
            AppLog.Instance.Log(nameof(ScanPage), "扫描失败: " + ex.Message);
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
                BusyRing.IsActive = false;
                BusyRing.Visibility = Visibility.Collapsed;
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
        _hasResults = true;
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

    private void OnClearCachesClick(object sender, RoutedEventArgs e)
    {
        AppServices.Current.ClearCaches();
        ProgressText.Text = "已清除主机名与MAC缓存";
    }

    private void OnOpenMemoClick(object sender, RoutedEventArgs e) =>
        App.MainWindow?.NavigateTo("memo");

    private void UpdateStatusUi()
    {
        StartButton.IsEnabled = !_running;
        StopButton.IsEnabled = _running;
        SegmentBox.IsEnabled = !_running;
        BusyRing.IsActive = _running;
        BusyRing.Visibility = Vis(_running);
        UpdateEmptyState();
    }

    /// <summary>Shows the 尚未扫描 card only while nothing has been probed at all.</summary>
    private void UpdateEmptyState() =>
        EmptyState.Visibility = Vis(!_running && !_hasResults);

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

        UpdateEmptyState();
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
    // block / row / card interaction — identical in all four views
    // =====================================================================

    /// <summary>
    /// Finds the host a tapped element belongs to. Blocks, table rows and cards all
    /// tag themselves with their IP address, which survives the segment changes that
    /// would make a cached DataContext stale.
    /// </summary>
    private IpBlock? ResolveBlock(object sender)
    {
        if (sender is not FrameworkElement element) return null;
        if (element.DataContext is IpBlock fromContext) return fromContext;
        if (element.Tag is string ip && _byIp.TryGetValue(ip, out var fromTag)) return fromTag;
        return null;
    }

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
            _hasResults = true;
            RefreshLegend();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ScanPage), "单点探测失败: " + ex.Message);
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
        if (block is null || _blockMenu is null || sender is not FrameworkElement target) return;

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

        _blockMenu.ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
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

    /// <summary>
    /// 导出结果 — asks for a format and destination with <see cref="ExportDialog"/>,
    /// then renders through <see cref="ReportWriter"/>.
    /// </summary>
    /// <remarks>
    /// Five formats are offered (TXT / CSV / XLSX / HTML / PDF) instead of the
    /// original's fixed CSV, and the columns follow the same visibility settings as
    /// the on-screen views, so what you export is what you were looking at.
    /// </remarks>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = AppServices.Current.Config;

            var headers = new List<string> { "IP", "状态", "时间", "来源" };
            if (config.ShowHostNameColumn) headers.Add("主机名");
            if (config.ShowMacColumn) headers.Add("MAC");
            if (config.ShowMemoColumn) headers.Add("备注");

            var rows = new List<IReadOnlyList<string>>(Blocks.Count);
            foreach (var block in Blocks)
            {
                var row = new List<string> { block.Ip, block.StatusText, block.TimeText, block.SourceText };
                if (config.ShowHostNameColumn) row.Add(block.HostName);
                if (config.ShowMacColumn) row.Add(block.Mac);
                if (config.ShowMemoColumn) row.Add(block.Memo);
                rows.Add(row);
            }

            var choice = await ExportDialog.ShowAsync(XamlRoot, "IP扫描结果", rows.Count);
            if (choice is null) return; // cancelled

            var (format, path) = choice.Value;
            var scanned = Blocks.Count(b => b.Status != HostStatus.Pending);

            ReportWriter.Write(new ReportRequest
            {
                Title = "IP扫描结果",
                Subtitle = $"网段 {IpMath.GetSegment(SegmentBox.Text)}  ·  共 {rows.Count} 条记录，已扫描 {scanned} 条",
                Headers = headers,
                Rows = rows,
                Format = format,
            }, path);

            var open = await UiKit.ConfirmAsync(XamlRoot, "导出成功",
                $"已导出 {rows.Count} 条记录到：\n{path}\n\n是否打开所在文件夹？",
                "打开文件夹", "关闭");
            if (open) AppServices.Current.Shell.OpenFolder(Path.GetDirectoryName(path) ?? path);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ScanPage), "导出失败: " + ex.Message);
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

    private static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
