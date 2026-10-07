using System.Globalization;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;
using IPScaner.Core.Net;
using IPScaner.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace IPScaner.WinUI.Views;

/// <summary>
/// 本机端口占用 — the original <c>FormPortByPid</c> rebuilt on
/// <see cref="LocalPortTable"/> instead of <c>cmd.exe /c netstat -ano</c>.
/// </summary>
/// <remarks>
/// <para>Fidelity kept: the five original columns 协议/端口/PID/进程名/进程所在路径 with the
/// DarkOrange PID column, the <c>系统端口列表(TCP: n, UDP: m, 合计: k)</c> caption, the
/// status strip wording, the exact kill confirmation, the 筛选 / 反向筛选 semantics
/// (端口 and PID exact, 进程名 and 路径 case-insensitive substring), and the full
/// re-read after a kill.</para>
/// <para>Fixes and improvements over the original, all of them deliberate:</para>
/// <list type="bullet">
/// <item><b>状态 column.</b> The original parsed netstat with
/// <c>\s+(\S+)\s+(\S+)\s+(\d+)</c> and threw the state token away, so a listener and
/// its established connections collapsed into one row and the grid could not answer
/// "who is connected to me". <see cref="LocalPortTable"/> reports the real TCP state
/// (localised by the Core), which is shown here as a new sixth column.</item>
/// <item><b>IPv6 + protocol toggles.</b> The original's regexes only matched
/// dotted-quad local addresses, so IPv6 rows were invisible; 包含IPv6 exposes them and
/// 包含TCP/包含UDP mirror the two netstat passes.</item>
/// <item><b>Rows with an unresolvable process are kept</b> (the original dropped them
/// silently). They render with an empty 进程名 / 进程所在路径 and
/// <see cref="LocalPortInfo.ProcessMissing"/> set; the footer explains the blank cells.</item>
/// <item><b>Failed kills are reported.</b> The original called
/// <c>Process.GetProcessById(pid).Kill()</c> outside any try/catch and swallowed the
/// exception in an empty global handler, so a failed kill did nothing visible;
/// <see cref="LocalPortTable.TryKillProcess"/> returns a Chinese reason that is shown.</item>
/// <item><b>Filter caption bug.</b> <c>RefreshStat</c> was called with the unfiltered
/// list whenever 筛选 was empty, so a reverse-only filter left stale totals; the caption
/// now always describes what the grid shows. The filters are also re-applied after a
/// refresh (the original dropped them from the view but kept the text).</item>
/// <item><b>No busy-wait refresh.</b> The original disabled 刷新 and re-enabled it from a
/// 50 ms <c>ThreadPool</c> polling loop; this is a plain awaited <c>Task</c>.</item>
/// <item><b>Double-click.</b> Per the port brief, double-clicking a row now prompts to
/// kill the owning process. The original dispatched on the column header
/// (<c>PID</c> → kill, <c>进程所在路径</c> → open the folder), so the open-folder action
/// moved to the row context menu and the 打开文件目录 button.</item>
/// </list>
/// </remarks>
public sealed partial class LocalPortPage : Page
{
    /// <summary>Form caption, reused as every dialog title like the original did.</summary>
    private const string WindowTitle = "系统端口查看工具";

    private IReadOnlyList<LocalPortInfo> _all = [];
    private CancellationTokenSource? _loadCts;
    private bool _loading;
    private bool _reloadQueued;
    private MenuFlyout? _rowMenu;
    private LocalPortInfo? _menuRow;

    public LocalPortPage()
    {
        InitializeComponent();
        BuildRowMenu();

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // =====================================================================
    // page lifecycle
    // =====================================================================

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0) await RefreshAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        try { _loadCts?.Cancel(); }
        catch (ObjectDisposedException) { /* read already finished */ }
    }

    private Task InfoAsync(string message) => UiKit.InfoAsync(XamlRoot, WindowTitle, message);

    // =====================================================================
    // enumeration
    // =====================================================================

    private async void OnReloadClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnProtocolToggled(object sender, RoutedEventArgs e)
    {
        // The toggles decide what is enumerated, so re-read immediately rather than
        // leaving the grid inconsistent with them. A toggle pressed while a read is in
        // flight queues one more pass instead of being dropped.
        if (_loading)
        {
            _reloadQueued = true;
            return;
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_loading) return;

        var includeTcp = TcpToggle.IsChecked == true;
        var includeUdp = UdpToggle.IsChecked == true;
        var includeIpv6 = Ipv6Toggle.IsChecked == true;

        if (!includeTcp && !includeUdp)
        {
            StatusText.Text = "请至少选择 TCP 或 UDP 中的一种协议";
            return;
        }

        _loading = true;
        ReloadButton.IsEnabled = false;
        FilterButton.IsEnabled = false;
        StatusText.Text = "正在读取端口数据";

        var cts = new CancellationTokenSource();
        _loadCts = cts;

        try
        {
            // GetExtendedTcpTable / GetExtendedUdpTable through the IP Helper API —
            // no cmd.exe, no netstat, and IPv6 plus the TCP state come for free.
            var rows = await AppServices.Current.LocalPorts.QueryAsync(includeTcp, includeUdp, includeIpv6, cts.Token);

            if (!ReferenceEquals(_loadCts, cts)) return;   // superseded by a newer read

            _all = rows;
            ApplyFilter();
            StatusText.Text = "端口数据加载完毕";

            if (rows.Count == 0)
                FilterHintText.Text = "未读取到任何端口（可能是权限不足）。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消读取";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(LocalPortPage), "读取本机端口失败: " + ex.Message);
            StatusText.Text = "读取端口数据失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                _loading = false;
                ReloadButton.IsEnabled = true;
                FilterButton.IsEnabled = true;
            }

            cts.Dispose();
        }

        // A toggle (or 刷新) pressed while this read was running gets its own pass.
        if (_reloadQueued)
        {
            _reloadQueued = false;
            await RefreshAsync();
        }
    }

    // =====================================================================
    // filtering
    // =====================================================================

    private void OnFilterClick(object sender, RoutedEventArgs e) => ApplyFilter();

    /// <summary>Enter in either box runs 筛选 — the original's <c>AcceptButton</c>.</summary>
    private void OnFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim().ToUpperInvariant();
        var except = ExceptFilterBox.Text.Trim().ToUpperInvariant();

        IEnumerable<LocalPortInfo> view = _all;

        if (filter.Length > 0)
        {
            // 端口/PID: exact equality on the decimal text. 进程名/路径: substring,
            // case-insensitive (ToUpperInvariant replaces the original's culture-sensitive
            // ToUpper()). The four fields are ORed inside the box, like the original.
            view = view.Where(r =>
                Number(r.LocalPort) == filter ||
                Number(r.Pid) == filter ||
                r.ProcessName.ToUpperInvariant().Contains(filter) ||
                r.ProcessPath.ToUpperInvariant().Contains(filter));
        }

        if (except.Length > 0)
        {
            // 反向筛选 drops a row when 端口 or PID equals the text, or 进程名/路径 contains it.
            view = view.Where(r =>
                Number(r.LocalPort) != except &&
                Number(r.Pid) != except &&
                !r.ProcessName.ToUpperInvariant().Contains(except) &&
                !r.ProcessPath.ToUpperInvariant().Contains(except));
        }

        var rows = view.ToList();

        // Full reset, the way the original rebound its DataGridView.
        PortList.ItemsSource = null;
        PortList.ItemsSource = rows;
        PortList.SelectedItem = null;

        RefreshStat(rows);
        UpdateActionButtons();

        FilterHintText.Text = filter.Length == 0 && except.Length == 0
            ? string.Empty
            : $"已筛选：显示 {rows.Count} / 共 {_all.Count} 条";

        EmptyHintText.Visibility = rows.Count == 0 && _all.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 系统端口列表(TCP: n, UDP: m, 合计: k) — group order is first appearance (TCP block
    /// first, then UDP) and 合计 counts rows, exactly like the original's RefreshStat.
    /// Always fed the filtered rows, which is the bug fix.
    /// </summary>
    private void RefreshStat(IReadOnlyList<LocalPortInfo> rows)
    {
        var parts = new List<string>();
        foreach (var group in rows.GroupBy(r => r.Protocol))
        {
            parts.Add($"{group.Key}: {group.Count()}");
        }

        if (rows.Count > 0) parts.Add($"合计: {rows.Count}");

        SummaryText.Text = "系统端口列表(" + string.Join(", ", parts) + ")";
    }

    // =====================================================================
    // row actions
    // =====================================================================

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionButtons();

    private void UpdateActionButtons()
    {
        var row = PortList.SelectedItem as LocalPortInfo;
        KillButton.IsEnabled = row is not null;
        OpenFolderButton.IsEnabled = row is not null && !string.IsNullOrEmpty(row.ProcessPath);
    }

    private static LocalPortInfo? ResolveRow(object sender) =>
        (sender as FrameworkElement)?.DataContext as LocalPortInfo;

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var row = ResolveRow(sender);
        if (row is null) return;

        PortList.SelectedItem = row;
        _ = KillRowAsync(row);
    }

    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var row = ResolveRow(sender);
        if (row is null || _rowMenu is null) return;

        _menuRow = row;
        PortList.SelectedItem = row;
        _rowMenu.ShowAt((FrameworkElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
        e.Handled = true;
    }

    private async void OnRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag }) return;

        var row = _menuRow;
        _menuRow = null;
        if (row is null) return;

        if (tag == "kill") await KillRowAsync(row);
        else await OpenFolderAsync(row);
    }

    private async void OnKillClick(object sender, RoutedEventArgs e) =>
        await KillRowAsync(PortList.SelectedItem as LocalPortInfo);

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e) =>
        await OpenFolderAsync(PortList.SelectedItem as LocalPortInfo);

    private void BuildRowMenu()
    {
        _rowMenu = new MenuFlyout();
        _rowMenu.Items.Add(new MenuFlyoutItem { Text = "结束进程", Tag = "kill" });
        _rowMenu.Items.Add(new MenuFlyoutItem { Text = "打开文件目录", Tag = "folder" });

        foreach (var item in _rowMenu.Items.OfType<MenuFlyoutItem>())
        {
            item.Click += OnRowMenuItemClick;
        }
    }

    /// <summary>
    /// 结束进程. Confirmation text is byte-for-byte the original's
    /// (<c>dgvProcess_CellDoubleClick</c>), including the <c>\r\n</c> break and the
    /// ASCII <c>?</c>.
    /// </summary>
    private async Task KillRowAsync(LocalPortInfo? row)
    {
        if (row is null)
        {
            await InfoAsync("请先选择一行。");
            return;
        }

        if (row.Pid <= 0)
        {
            await InfoAsync("该行没有有效的进程 ID，无法结束进程。");
            return;
        }

        // The original interpolated the 进程名 cell; when the process could not be read
        // there is no name, so the PID is used to keep the prompt unambiguous.
        var name = string.IsNullOrEmpty(row.ProcessName) ? $"PID {row.Pid}" : row.ProcessName;
        var message = $"确定要杀死进程【{name}】吗?\r\n请注意：杀死进程可能会导致应用异常，请谨慎操作。";

        if (!await UiKit.ConfirmAsync(XamlRoot, WindowTitle, message)) return;

        if (LocalPortTable.TryKillProcess(row.Pid, out var error))
        {
            StatusText.Text = $"已结束进程 {row.Pid}。";
        }
        else
        {
            // The original swallowed every failure (empty Application.ThreadException
            // handler); the Core's Chinese reason is surfaced instead.
            StatusText.Text = error;
            await InfoAsync(error);
        }

        // The original re-ran 刷新端口列表 unconditionally after the kill attempt.
        await RefreshAsync();
    }

    private async Task OpenFolderAsync(LocalPortInfo? row)
    {
        if (row is null)
        {
            await InfoAsync("请先选择一行。");
            return;
        }

        if (string.IsNullOrEmpty(row.ProcessPath))
        {
            await InfoAsync("该行没有可用的进程路径。");
            return;
        }

        string? folder = null;
        try
        {
            folder = Path.GetDirectoryName(row.ProcessPath);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(LocalPortPage), "解析进程目录失败: " + ex.Message);
        }

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            await InfoAsync($"找不到进程所在目录：{folder}");
            return;
        }

        AppServices.Current.Shell.OpenFolder(folder);
    }
}
