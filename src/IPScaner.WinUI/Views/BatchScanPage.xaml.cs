using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using IPScaner.Core.Configuration;
using IPScaner.Core.Export;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;
using IPScaner.Core.Net;
using IPScaner.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPScaner.WinUI.Views;

/// <summary>
/// IP批量扫描 — the original's <c>FormIPSegment</c>.
/// </summary>
/// <remarks>
/// Three generators (单网段 / 连续IP范围 / 掩码位) all append into one
/// one-IP-per-line list, which is the single source of truth for what gets
/// scanned. The grid streams results from <see cref="ScanEngine"/>, colours each
/// row by status and can be sorted by clicking a column header.
/// <para>
/// Defects fixed relative to the original, each noted at the point of the fix:
/// sorting starts ascending and remembers its direction per column, sorting
/// survives incoming results, 定时扫描 can be cancelled immediately, generators
/// cannot inject a crashing prefix, and 导出 writes UTF-8 with a BOM.
/// </para>
/// </remarks>
public sealed partial class BatchScanPage : Page
{
    /// <summary>Caption the original used for every message box on this form.</summary>
    private const string ScanTitle = "IP批量扫描";

    /// <summary>The original warns before generating more than this many addresses.</summary>
    private const int LargeListThreshold = 1000;

    /// <summary>Grid headers, in the original's column order.</summary>
    private static readonly string[] ExportHeaders = ["IP", "主机名", "MAC", "备注", "状态", "时间"];

    /// <summary>
    /// Cell widths shared by the header row and every result row. Mirrors the six
    /// proportional columns declared on the header grid inside the ListView, so the
    /// grid always fits the window at any DPI and any pane width.
    /// </summary>
    private static readonly GridLength[] CellWidths =
    [
        new(1.3, GridUnitType.Star),
        new(1.3, GridUnitType.Star),
        new(1.4, GridUnitType.Star),
        new(1.2, GridUnitType.Star),
        new(0.7, GridUnitType.Star),
        new(0.8, GridUnitType.Star),
    ];

    /// <summary>
    /// The original's segment regex, kept verbatim: anchored at the start only, and
    /// the optional 4th octet is discarded. The result is range-checked afterwards,
    /// because feeding "999.999.999" to the original threw an OverflowException
    /// straight out of the click handler.
    /// </summary>
    private static readonly Regex SegmentPrefixRegex =
        new(@"^(\d{1,3}\.\d{1,3}\.\d{1,3})(\.\d{1,3})?", RegexOptions.Compiled);

    // Hard-coded LightGreen / LightCoral, exactly as the original painted them —
    // deliberately NOT the configurable NetworkOKColor / NetworkNGColor that the
    // main grid uses.
    private static readonly SolidColorBrush OnlineRowBrush = new(Color.FromArgb(0xFF, 0x90, 0xEE, 0x90));
    private static readonly SolidColorBrush OfflineRowBrush = new(Color.FromArgb(0xFF, 0xF0, 0x80, 0x80));
    private static readonly SolidColorBrush OnlineRowTextBrush = new(Colors.Black);
    private static readonly SolidColorBrush OfflineRowTextBrush = new(Colors.Black);

    private readonly ObservableCollection<BatchRow> _rows = [];
    private readonly Dictionary<string, BatchRow> _rowByIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _countdownTimer = new();
    private readonly SolidColorBrush _pendingRowBrush = new(Colors.Transparent);

    private Brush _pendingRowTextBrush = new SolidColorBrush(Colors.Black);
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _waitCts;
    private bool _running;
    private bool _ready;
    private bool _loadedOnce;
    private DateTime _waitUntil;

    /// <summary>Column key the grid is currently ordered by, or null for arrival order.</summary>
    private string? _sortColumn;
    private bool _sortAscending = true;

    public BatchScanPage()
    {
        InitializeComponent();

        ResultList.ItemsSource = _rows;

        _countdownTimer.Interval = TimeSpan.FromSeconds(1);
        _countdownTimer.Tick += OnCountdownTick;

        _ready = true;

        Loaded += OnPageLoaded;
        // A single token source per run; cancelled by 停止 and by leaving the page.
        Unloaded += (_, _) => CancelAll();
    }

    // =====================================================================
    // setup
    // =====================================================================

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;

        // The row text colour has to track the theme; take it from a live control
        // rather than guessing.
        _pendingRowTextBrush = StatusText.Foreground ?? _pendingRowTextBrush;

        ApplySegment(NavigationArgs.TakeBatchSegment());
        UpdateRangeCaption();
        RefreshHeaderGlyphs();
    }

    /// <summary>
    /// Seeds the three mode panels from an <c>A.B.C</c> prefix — the role the
    /// original's constructor played when the main window handed over its segment.
    /// </summary>
    private void ApplySegment(string? raw)
    {
        var segment = IpMath.GetSegment(raw ?? string.Empty);
        if (!IpMath.IsValidSegment(segment)) segment = AppServices.Current.GetDefaultSegment();
        if (!IpMath.IsValidSegment(segment)) return;

        IpPartBox.Text = segment;
        StartIpBox.Text = segment + ".1";
        EndIpBox.Text = segment + ".254";

        // The original copied the three prefix octets into txtIP1..3 and left the
        // 4th at its default 1; it threw when the prefix was not three numbers.
        if (IpMath.TryParseOctets(segment + ".1", out var octets))
        {
            MaskIp1.Value = octets[0];
            MaskIp2.Value = octets[1];
            MaskIp3.Value = octets[2];
            MaskIp4.Value = octets[3];
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        PanelSingle.Visibility = ModeSingle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelRange.Visibility = ModeRange.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelMask.Visibility = ModeMask.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRangeTextChanged(object sender, TextChangedEventArgs e) => UpdateRangeCaption();

    private void UpdateRangeCaption()
    {
        // The original left this caption showing a stale count once the box became
        // empty; it is always recomputed here.
        RangeCaption.Text = $"IP扫描范围 ({GetTargetLines().Count})";
    }

    /// <summary>
    /// The scan list: one address per line, blank lines dropped, de-duplicated in
    /// first-seen order (the original de-duplicated generated ranges on append but
    /// happily accepted duplicate pasted lines).
    /// </summary>
    private List<string> GetTargetLines()
    {
        var list = new List<string>();
        var text = RangeBox.Text;
        if (string.IsNullOrEmpty(text)) return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length > 0 && seen.Add(line)) list.Add(line);
        }
        return list;
    }

    // =====================================================================
    // the three generators
    // =====================================================================

    private async void OnGenerateSingle(object sender, RoutedEventArgs e)
    {
        var match = SegmentPrefixRegex.Match(IpPartBox.Text ?? string.Empty);
        if (!match.Success)
        {
            await WarnAsync("请输入正确的IP扫描段");
            return;
        }

        var start = NumberValue(StartOctetBox, 1, 255, 1);
        var end = NumberValue(EndOctetBox, 1, 255, 254);
        if (start > end)
        {
            await WarnAsync("开始IP不得大于结束IP");
            return;
        }

        var segment = IpMath.GetSegment(match.Groups[1].Value);
        if (!IpMath.IsValidSegment(segment))
        {
            await WarnAsync("请输入正确的IP扫描段");
            return;
        }

        await ConfirmAndAppendAsync(IpMath.GetRange($"{segment}.{start}", $"{segment}.{end}"));
    }

    private async void OnGenerateRange(object sender, RoutedEventArgs e)
    {
        var startIp = (StartIpBox.Text ?? string.Empty).Trim();
        if (!IpMath.IsValidIPv4(startIp))
        {
            await WarnAsync("请输入正确的开始IP地址");
            return;
        }

        var endIp = (EndIpBox.Text ?? string.Empty).Trim();
        // The original showed "请输入正确的开始IP地址" for this branch too — a
        // copy/paste bug; the message now names the field that is actually wrong.
        if (!IpMath.IsValidIPv4(endIp))
        {
            await WarnAsync("请输入正确的结束IP地址");
            return;
        }

        // The original appended nothing at all when start > end, without a word.
        if (IpMath.ToUInt32(startIp) > IpMath.ToUInt32(endIp))
        {
            await WarnAsync("开始IP不得大于结束IP");
            return;
        }

        await ConfirmAndAppendAsync(IpMath.GetRange(startIp, endIp));
    }

    private async void OnGenerateMask(object sender, RoutedEventArgs e)
    {
        var seed = $"{NumberValue(MaskIp1, 0, 255, 192)}.{NumberValue(MaskIp2, 0, 255, 168)}." +
                   $"{NumberValue(MaskIp3, 0, 255, 0)}.{NumberValue(MaskIp4, 0, 255, 1)}";
        var bits = NumberValue(MaskBitsBox, 1, 30, 24);

        // The ported NetworkCalculator range. Unlike the original this keeps the
        // .0/.255 boundary hosts of a /23 or shorter prefix instead of dropping
        // them on the way through Utility.GetIPRange.
        var list = SubnetCalculator.HostsForMask(seed, bits);
        if (list.Count == 0)
        {
            await WarnAsync("掩码位异常，请重新输入");
            return;
        }

        await ConfirmAndAppendAsync(list);
    }

    private async Task ConfirmAndAppendAsync(IReadOnlyList<string> ips)
    {
        if (ips.Count == 0)
        {
            // The original silently appended nothing in this case.
            await WarnAsync("没有生成任何IP地址");
            return;
        }

        // The original asked this only in 掩码位 mode; a huge list is just as
        // unwelcome from the other two generators.
        if (ips.Count > LargeListThreshold &&
            !await UiKit.ConfirmAsync(
                XamlRoot, ScanTitle, "当前生成的IP地址已超过1000个，你确认要继续吗？", "确定", "取消"))
        {
            return;
        }

        AppendIps(ips);
    }

    private void AppendIps(IReadOnlyList<string> ips)
    {
        var existing = new HashSet<string>(GetTargetLines(), StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var ip in ips)
        {
            if (existing.Add(ip)) builder.AppendLine(ip);
        }
        if (builder.Length == 0) return;

        RangeBox.Text += builder.ToString();
        RangeBox.SelectionStart = RangeBox.Text.Length;
        UpdateRangeCaption();
    }

    // =====================================================================
    // scanning
    // =====================================================================

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        // The original had no re-entrancy guard: two workers could run at once and
        // clear each other's results.
        if (_running) return;

        var targets = GetTargetLines();
        if (targets.Count == 0)
        {
            await WarnAsync("请先输入或生成要扫描的IP地址");
            return;
        }

        var timed = IntervalCheck.IsChecked == true;
        var config = AppServices.Current.Config;
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _running = true;
        SyncEnabledState();

        try
        {
            do
            {
                PrepareRound(targets);
                StatusText.Text = $"准备批量扫描 共有{targets.Count}个IP";

                await RunRoundAsync(targets, config, cts.Token);

                if (cts.IsCancellationRequested) break;
                if (!timed || IntervalCheck.IsChecked != true) break;

                var minutes = CurrentIntervalMinutes();
                StatusText.Text = $"等待{minutes}分钟后，进行下一轮扫描";
                StatusNextText.Text = $"剩余时间{minutes * 60} 秒";

                // The original slept on the worker thread, so 停止 could not take
                // effect for up to 59 minutes. A cancellable delay fixes that.
                // Unchecking 定时扫描 cancels only this wait; 停止 cancels the run.
                try
                {
                    await WaitAsync(TimeSpan.FromMinutes(minutes), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            while (!cts.IsCancellationRequested && timed && IntervalCheck.IsChecked == true);

            StatusText.Text = cts.IsCancellationRequested ? "用户取消了操作" : "批量扫描完成";
            StatusNextText.Text = "-";
        }
        catch (OperationCanceledException)
        {
            // Only a 停止 (or leaving the page) cancels the run itself; a cancelled
            // wait is handled inside the loop above.
            StatusText.Text = cts.IsCancellationRequested ? "用户取消了操作" : "批量扫描完成";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(BatchScanPage), "批量扫描失败: " + ex.Message);
            StatusText.Text = "扫描出错：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                _scanCts = null;
                _running = false;
                SyncEnabledState();
            }
            cts.Dispose();
        }
    }

    /// <summary>Resets the grid and lists every target as 待检测 for this round.</summary>
    private void PrepareRound(IReadOnlyList<string> targets)
    {
        _rows.Clear();
        _rowByIp.Clear();

        foreach (var ip in targets)
        {
            // The original left the grid empty until the first reply landed; showing
            // the whole job up front makes a 1000-address sweep legible.
            if (_rowByIp.ContainsKey(ip)) continue;
            var row = new BatchRow(ip, CellWidths, _pendingRowBrush, _pendingRowTextBrush);
            row.SetMemo(AppServices.Current.Memo.Lookup(null, ip));
            _rowByIp[ip] = row;
            if (_sortColumn is null) _rows.Add(row);
            else PlaceRow(row);
        }

        UpdateSummary();
    }

    private async Task RunRoundAsync(IReadOnlyList<string> targets, AppConfig config, CancellationToken token)
    {
        StatusNextText.Text = "正在批量扫描中...";

        // Progress<T> is built on the UI thread, so these callbacks arrive there.
        var progress = new Progress<ScanProgress>(p => StatusText.Text = "正在扫描IP: " + p.CurrentIP);

        await AppServices.Current.Scanner.RunAsync(
            targets,
            config,
            result =>
            {
                // ScanEngine reports from worker threads; every touch of the grid
                // must go through the dispatcher.
                DispatcherQueue.TryEnqueue(() => ApplyResult(result));
                return Task.CompletedTask;
            },
            progress,
            ScanEngine.DefaultConcurrency,
            token);
    }

    private void ApplyResult(HostResult result)
    {
        _rowByIp.TryGetValue(result.IP, out var row);
        var isNew = row is null;
        if (row is null)
        {
            row = new BatchRow(result.IP, CellWidths, _pendingRowBrush, _pendingRowTextBrush);
            _rowByIp[result.IP] = row;
        }

        var background = result.Status switch
        {
            HostStatus.Online => OnlineRowBrush,
            HostStatus.Offline => OfflineRowBrush,
            _ => _pendingRowBrush,
        };
        var foreground = result.Status switch
        {
            HostStatus.Online => OnlineRowTextBrush,
            HostStatus.Offline => OfflineRowTextBrush,
            _ => _pendingRowTextBrush,
        };

        // 备注 follows the MAC when one is known and falls back to the IP — the
        // original's GetMemoByMacOrIp precedence.
        row.Apply(result, AppServices.Current.Memo.Lookup(result.Mac, result.IP), background, foreground);

        if (_sortColumn is null)
        {
            if (isNew) _rows.Add(row);
        }
        else
        {
            // Re-applying the active sort as rows arrive is what the original failed
            // to do: its next result rebound the grid into insertion order.
            PlaceRow(row);
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_rows.Count == 0)
        {
            ResultCaption.Text = "网络扫描结果";
            return;
        }

        var ok = 0;
        var ng = 0;
        foreach (var row in _rows)
        {
            if (row.State == HostStatus.Online) ok++;
            else if (row.State == HostStatus.Offline) ng++;
        }

        // e.g. 网络扫描结果 (OK:12, NG:254) — the original's exact shape.
        ResultCaption.Text = $"网络扫描结果 (OK:{ok}, NG:{ng})";
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken token)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _waitCts = waitCts;
        _waitUntil = DateTime.Now + delay;
        _countdownTimer.Start();

        try
        {
            await Task.Delay(delay, waitCts.Token);
        }
        finally
        {
            _countdownTimer.Stop();
            if (ReferenceEquals(_waitCts, waitCts)) _waitCts = null;
        }
    }

    private void OnCountdownTick(object? sender, object e)
    {
        var remaining = _waitUntil - DateTime.Now;
        // The original's countdown format was "m\分s" plus a literal 秒.
        StatusNextText.Text = remaining.TotalSeconds > 0
            ? "剩余时间" + remaining.ToString("m\\分s") + "秒"
            : "正在批量扫描中...";
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => CancelAll();

    /// <summary>Cancels the running round and any pending 定时扫描 wait.</summary>
    private void CancelAll()
    {
        _countdownTimer.Stop();
        try { _waitCts?.Cancel(); }
        catch { /* already gone */ }
        try { _scanCts?.Cancel(); }
        catch { /* already gone */ }
    }

    private void OnIntervalToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        // Unchecking drops out of the wait immediately instead of after the full
        // interval, and the worker re-reads the box between rounds.
        if (IntervalCheck.IsChecked != true)
        {
            try { _waitCts?.Cancel(); }
            catch { /* already gone */ }
        }

        SyncEnabledState();
    }

    private void SyncEnabledState()
    {
        // 定时扫描 froze the list in the original; disabling all three generators
        // (rather than only mode 1's) closes the hole that let modes 2 and 3 append
        // to a "read-only" list behind the worker's back.
        var frozen = _running || IntervalCheck.IsChecked == true;

        StartButton.IsEnabled = !_running;
        StopButton.IsEnabled = _running;
        RangeBox.IsReadOnly = frozen;
        GenerateSingleButton.IsEnabled = !frozen;
        GenerateRangeButton.IsEnabled = !frozen;
        GenerateMaskButton.IsEnabled = !frozen;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        RangeBox.Text = string.Empty;
        _rows.Clear();
        _rowByIp.Clear();

        // The original left both captions (and the status line) stale after a clear.
        UpdateRangeCaption();
        UpdateSummary();
        if (!_running)
        {
            StatusText.Text = "准备就绪";
            StatusNextText.Text = "-";
        }
    }

    // =====================================================================
    // sorting
    // =====================================================================

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string column }) return;

        if (string.Equals(_sortColumn, column, StringComparison.Ordinal))
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            // The original's single shared flag made the first click on any column
            // sort descending and carried that direction across columns.
            _sortColumn = column;
            _sortAscending = true;
        }

        ApplySort();
        RefreshHeaderGlyphs();
    }

    private void ApplySort()
    {
        if (_sortColumn is null) return;

        var ordered = _rows.OrderBy(row => row, Comparer<BatchRow>.Create(CompareRows)).ToList();
        for (var target = 0; target < ordered.Count; target++)
        {
            var current = _rows.IndexOf(ordered[target]);
            if (current != target) _rows.Move(current, target);
        }
    }

    /// <summary>
    /// Moves one row to its sorted position, leaving the rest of the (already
    /// ordered) list untouched — one notification instead of a full rebuild.
    /// </summary>
    private void PlaceRow(BatchRow row)
    {
        var current = _rows.IndexOf(row);

        var target = 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (i == current) continue;
            if (CompareRows(_rows[i], row) > 0) break;
            target++;
        }

        if (current < 0) _rows.Insert(target, row);
        else if (current != target) _rows.Move(current, target);
    }

    private int CompareRows(BatchRow a, BatchRow b)
    {
        var result = _sortColumn switch
        {
            "ip" => CompareIp(a.IP, b.IP),
            "host" => string.Compare(a.HostName, b.HostName, StringComparison.CurrentCulture),
            "mac" => string.Compare(a.Mac, b.Mac, StringComparison.CurrentCulture),
            "memo" => string.Compare(a.Memo, b.Memo, StringComparison.CurrentCulture),
            // A plain string sort on OK/NG, so ascending lists every NG first — the
            // original behaved the same way.
            "status" => string.Compare(a.Status, b.Status, StringComparison.CurrentCulture),
            // Numeric, so timeouts (int.MaxValue) sort last when ascending.
            "time" => a.RoundtripMs.CompareTo(b.RoundtripMs),
            _ => 0,
        };

        return _sortAscending ? result : -result;
    }

    private static int CompareIp(string a, string b)
    {
        // The original's ParseIPAddress threw on anything that was not four numeric
        // octets, killing the header click; malformed lines now fall back to text.
        var okA = IpMath.TryParseOctets(a, out var octetsA);
        var okB = IpMath.TryParseOctets(b, out var octetsB);
        if (!okA || !okB) return string.Compare(a, b, StringComparison.Ordinal);

        for (var i = 0; i < 4; i++)
        {
            var delta = octetsA[i].CompareTo(octetsB[i]);
            if (delta != 0) return delta;
        }
        return 0;
    }

    private void RefreshHeaderGlyphs()
    {
        SetHeader(HeadIp, "IP", "ip");
        SetHeader(HeadHost, "主机名", "host");
        SetHeader(HeadMac, "MAC", "mac");
        SetHeader(HeadMemo, "备注", "memo");
        SetHeader(HeadStatus, "状态", "status");
        SetHeader(HeadTime, "时间", "time");
    }

    private void SetHeader(Button button, string title, string column)
    {
        // The original never drew a sort glyph.
        button.Content = string.Equals(_sortColumn, column, StringComparison.Ordinal)
            ? title + (_sortAscending ? " ▲" : " ▼")
            : title;
    }

    // =====================================================================
    // export
    // =====================================================================

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var rows = _rows.Where(row => row.State != HostStatus.Pending).ToList();
        if (rows.Count == 0)
        {
            // The original returned silently here.
            await WarnAsync("当前没有可导出的扫描结果");
            return;
        }

        try
        {
            var folder = ExportFolder();
            Directory.CreateDirectory(folder);

            // Rows are exported in the order the operator sees them, so a sorted
            // grid produces a sorted file (the original always wrote arrival order).
            var data = rows
                .Select(row => new[] { row.IP, row.HostName, row.Mac, row.Memo, row.Status, row.Time })
                .ToList();

            var csvPath = Path.Combine(folder, TableExporter.BuildFileName("IP批量扫描", ".csv"));
            TableExporter.WriteCsv(csvPath, ExportHeaders, data);

            var xlsxPath = Path.Combine(folder, TableExporter.BuildFileName("IP批量扫描", ".xlsx"));
            TableExporter.WriteXlsx(xlsxPath, ScanTitle, ExportHeaders, data);

            if (await UiKit.ConfirmAsync(
                    XamlRoot, ScanTitle, "IP批量扫描导出成功。是否要打开Excel文档？", "打开文件夹", "取消"))
            {
                AppServices.Current.Shell.OpenFolder(folder);
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(BatchScanPage), "导出失败: " + ex.Message);
            await UiKit.InfoAsync(XamlRoot, "导出失败", ex.Message);
        }
    }

    /// <summary>Prefers the application directory, falling back to Documents when read-only.</summary>
    private static string ExportFolder()
    {
        var appDir = Path.Combine(AppContext.BaseDirectory, "导出");
        try
        {
            Directory.CreateDirectory(appDir);
            var probe = Path.Combine(appDir, ".writetest");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return appDir;
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IPScaner", "导出");
        }
    }

    // =====================================================================
    // helpers
    // =====================================================================

    private Task WarnAsync(string message) => UiKit.InfoAsync(XamlRoot, ScanTitle, message);

    private int CurrentIntervalMinutes() => NumberValue(IntervalMinutesBox, 1, 59, 1);

    /// <summary>Clamped NumberBox value; an empty box reads as NaN, not as a crash.</summary>
    private static int NumberValue(NumberBox box, int min, int max, int fallback)
    {
        var value = box.Value;
        if (double.IsNaN(value)) return fallback;
        return (int)Math.Clamp(Math.Round(value), min, max);
    }

    /// <summary>
    /// One result row. It <i>is</i> the grid row (rather than a view model behind a
    /// template) so that the status colour can be applied directly — the original
    /// painted rows from its RowStateChanged handler in exactly the same way.
    /// </summary>
    private sealed class BatchRow : Grid
    {
        private readonly TextBlock _hostText;
        private readonly TextBlock _macText;
        private readonly TextBlock _memoText;
        private readonly TextBlock _statusText;
        private readonly TextBlock _timeText;

        internal BatchRow(string ip, GridLength[] widths, Brush background, Brush foreground)
        {
            IP = ip;

            Padding = new Thickness(8, 3, 8, 3);
            ColumnSpacing = 6;
            Background = background;
            foreach (var width in widths) ColumnDefinitions.Add(new ColumnDefinition { Width = width });

            var ipText = AddCell(this, 0, foreground, false);
            ipText.Text = ip;
            _hostText = AddCell(this, 1, foreground, true);
            _macText = AddCell(this, 2, foreground, true);
            _memoText = AddCell(this, 3, foreground, true);
            _statusText = AddCell(this, 4, foreground, false);
            _timeText = AddCell(this, 5, foreground, false);
        }

        internal string IP { get; }

        internal string HostName { get; private set; } = string.Empty;

        internal string Mac { get; private set; } = string.Empty;

        internal string Memo { get; private set; } = string.Empty;

        /// <summary>OK / NG / 待检测 — <see cref="HostStatusText.Code"/>.</summary>
        internal string Status { get; private set; } = HostStatusText.Code(HostStatus.Pending);

        /// <summary>&lt;1ms / {n}ms / Timeout, blank until a probe has answered.</summary>
        internal string Time { get; private set; } = string.Empty;

        internal long RoundtripMs { get; private set; } = int.MaxValue;

        internal HostStatus State { get; private set; } = HostStatus.Pending;

        private static TextBlock AddCell(Grid view, int column, Brush foreground, bool trim)
        {
            var block = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
                Foreground = foreground,
            };
            SetColumn(block, column);
            view.Children.Add(block);
            return block;
        }

        /// <summary>Memo for a row that has not been probed yet.</summary>
        internal void SetMemo(string memo) => _memoText.Text = Memo = memo;

        /// <summary>
        /// The original's PingReplyInfo.Time: "&lt;1ms", "{n}ms" or "Timeout", and
        /// blank until the host has been probed. A host confirmed by the ARP or TCP
        /// fallback has no round-trip, so it reads "Timeout" — exactly what the
        /// original printed for a success with no measured time.
        /// </summary>
        private static string FormatTime(HostResult result)
        {
            if (result.Status == HostStatus.Pending) return string.Empty;
            var ms = result.RoundtripMs;
            if (ms < 0 || ms >= int.MaxValue) return "Timeout";
            return ms < 1 ? "<1ms" : ms + "ms";
        }

        internal void Apply(HostResult result, string memo, Brush background, Brush foreground)
        {
            State = result.Status;
            HostName = result.HostName;
            Mac = result.Mac;
            Memo = memo;
            Status = HostStatusText.Code(result.Status);
            RoundtripMs = result.RoundtripMs;
            Time = FormatTime(result);

            Background = background;
            foreach (var block in Children.OfType<TextBlock>()) block.Foreground = foreground;

            _hostText.Text = HostName;
            _macText.Text = Mac;
            _memoText.Text = Memo;
            _statusText.Text = Status;
            _timeText.Text = Time;
        }
    }
}
