using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;
using IPScaner.Core.Net;
using IPScaner.WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IPScaner.WinUI.Views;

/// <summary>
/// 目标端口扫描 — the four scan shapes of the original <c>FormPortScan</c>:
/// 单IP + 端口范围, 单IP + 端口列表, IP段 + 端口列表 and IP/掩码位 + 端口列表.
/// </summary>
/// <remarks>
/// <para>Fidelity kept from the original: the three group captions and the exact
/// result-line vocabulary (<c>开始扫描【…】…</c>, <c>ip:【…】 发现开放的端口：…</c>,
/// <c>端口扫描完毕</c>), the <c>HH:mm:ss </c> line prefix, the <c>,</c>/<c>，</c> port
/// grammar, the ascending de-duplicated port normalisation, the
/// <c>%TEMP%\IPScaner.FormPortScan.txt</c> batch-port history, the green/red
/// 扫描/停止 pairs, and the 复制 payload (every log line joined with CRLF).</para>
/// <para>Fixes and improvements over the original, all of them deliberate:</para>
/// <list type="bullet">
/// <item>Port parsing accepts ranges (<c>80,443,1000-2000</c>), <c>、</c>/space
/// separators and the <c>all</c> / <c>全部端口</c> keyword through
/// <see cref="PortScanner.ParsePorts"/>. V1.28.2 silently dropped every range token
/// and had no "all" keyword at all, and its <c>KeyDown</c> filter even made <c>-</c>
/// untypable — that filter is intentionally not reproduced.</item>
/// <item>Target parsing for the IP段 and CIDR shapes goes through
/// <see cref="PortScanner.ParseTargets"/>, which also accepts
/// <c>A.B.C.1-A.B.C.50</c> and <c>A.B.C.0/24</c> and range-checks every octet.</item>
/// <item>Validation happens <em>before</em> the buttons are disabled. In the original
/// <c>btnScanBatch_Click</c> the 停止/扫描 swap ran before the port check, so the
/// <c>请填写1~65535之间的端口</c> early return left the whole window stuck with every
/// 扫描 button disabled until the user pressed 停止.</item>
/// <item>The cancellation token is per page, not the original's <c>static</c> field:
/// stopping (or closing) one window no longer cancels every other scan.</item>
/// <item>Results are queued from the probe threads and drained into the grid by a
/// dispatcher timer, so the UI thread is never written to from a worker and a large
/// scan cannot flood the visual tree.</item>
/// <item>The original cleared its list once it passed 500 items, silently destroying
/// earlier results and part of the 复制 payload; the grid keeps everything. The
/// grid is a real multi-column <see cref="ListView"/> (时间/IP/端口/服务/状态) instead
/// of a single-column string list, while 复制 still emits the original text log.</item>
/// <item>进度 is a real x/y counter (the original only mutated the group caption when
/// it found something); 显示关闭端口 is a new toggle wired to
/// <see cref="PortScanRequest.ReportClosed"/> because the original probe was a bare
/// boolean that could not distinguish 关闭 from 过滤.</item>
/// </list>
/// </remarks>
public sealed partial class PortScanPage : Page
{
    /// <summary>Form caption, reused as every dialog title like the original did.</summary>
    private const string WindowTitle = "端口扫描";

    /// <summary>
    /// Ports are written back into the input box in ascending CSV form, exactly as the
    /// original did — but only while that text stays readable. "all" / "全部端口" or a
    /// huge range would otherwise expand into 65 535 numbers inside the box (and inside
    /// the 复制 log).
    /// </summary>
    private const int MaxInlinePorts = 256;

    private const int FlushBatchSize = 400;

    /// <summary>%TEMP%\IPScaner.FormPortScan.txt — the batch port history.</summary>
    private static readonly string HistoryPath =
        Path.Combine(Path.GetTempPath(), "IPScaner.FormPortScan.txt");

    private readonly ConcurrentQueue<ResultRow> _pending = new();
    private readonly List<string> _log = [];
    private readonly object _logGate = new();
    private readonly DispatcherQueueTimer _flushTimer;

    private CancellationTokenSource? _scanCts;
    private bool _scanning;
    private bool _preparing;
    private int _openCount;
    private int _closedCount;

    /// <summary>The result rows bound to the grid. Appended on the UI thread only.</summary>
    public ObservableCollection<ResultRow> Rows { get; } = [];

    public PortScanPage()
    {
        InitializeComponent();

        // The original painted 扫描 LimeGreen (#32CD32) and 停止 IndianRed (#CD5C5C,
        // white text); the same signed ARGB values the config file uses.
        var scanBrush = UiKit.BrushFromArgb(-13447886);
        var scanTextBrush = new SolidColorBrush(UiKit.ContrastingTextColor(UiKit.ColorFromArgb(-13447886)));
        foreach (var button in new[] { ScanRangeButton, ScanListButton, ScanSegmentButton, ScanCidrButton })
        {
            button.Background = scanBrush;
            button.Foreground = scanTextBrush;
        }

        var stopBrush = UiKit.BrushFromArgb(-3318692);
        var stopTextBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
        foreach (var button in new[] { StopRangeButton, StopListButton, StopSegmentButton, StopCidrButton })
        {
            button.Background = stopBrush;
            button.Foreground = stopTextBrush;
        }

        _flushTimer = DispatcherQueue.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(120);
        _flushTimer.IsRepeating = true;
        _flushTimer.Tick += OnFlushTick;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // =====================================================================
    // page lifecycle
    // =====================================================================

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        UpdateTimeoutLabel();
        UpdateFormHeight(ActualHeight);

        // Set by the main grid's 快速端口扫描 context item.
        var pendingHost = NavigationArgs.TakePortScanHost();
        if (!string.IsNullOrWhiteSpace(pendingHost)) HostBox.Text = pendingHost.Trim();

        LoadHistoryPorts();
    }

    /// <summary>
    /// Keeps the scan form fully usable in a short window without leaving a gap under it
    /// in a tall one: the form area may grow to its natural height, but never past ~60%
    /// of the page, and it scrolls internally when it is capped.
    /// </summary>
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFormHeight(e.NewSize.Height);

    private void UpdateFormHeight(double pageHeight)
    {
        if (double.IsNaN(pageHeight) || pageHeight <= 0) return;
        FormScroller.MaxHeight = Math.Max(180, pageHeight * 0.62);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        // Per-page cancellation: the original cancelled a static token, so closing one
        // window stopped the scans of every other open window.
        try { _scanCts?.Cancel(); }
        catch (ObjectDisposedException) { /* scan already finished */ }

        _flushTimer.Stop();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        try { _scanCts?.Cancel(); }
        catch (ObjectDisposedException) { /* scan already finished */ }
    }

    private void UpdateTimeoutLabel() =>
        TimeoutText.Text = $"TCP超时毫秒：{Math.Clamp(AppServices.Current.Config.PortTimeout, 10, 2000)}";

    private Task InfoAsync(string message) => UiKit.InfoAsync(XamlRoot, WindowTitle, message);

    /// <summary>
    /// A NumberBox whose text the user cleared reports <see cref="double.NaN"/>. The
    /// original's NumericUpDown kept its last <c>Value</c> in that case, so the caller
    /// passes that surviving value as the fallback.
    /// </summary>
    private static int IntValue(NumberBox box, int fallback)
    {
        var value = box.Value;
        return double.IsNaN(value) ? fallback : (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    // =====================================================================
    // the four scan shapes
    // =====================================================================

    private async void OnScanRangeClick(object sender, RoutedEventArgs e) =>
        await StartAsync(ScanShape.SingleRange);

    private async void OnScanListClick(object sender, RoutedEventArgs e) =>
        await StartAsync(ScanShape.SingleList);

    private async void OnScanSegmentClick(object sender, RoutedEventArgs e) =>
        await StartAsync(ScanShape.SegmentList);

    private async void OnScanCidrClick(object sender, RoutedEventArgs e) =>
        await StartAsync(ScanShape.CidrList);

    private async Task StartAsync(ScanShape shape)
    {
        if (_scanning || _preparing) return;
        _preparing = true;

        try
        {
            var plan = shape switch
            {
                ScanShape.SingleRange => await PrepareRangeAsync(),
                ScanShape.SingleList => await PrepareSingleListAsync(),
                ScanShape.SegmentList => await PrepareSegmentAsync(),
                _ => await PrepareCidrAsync(),
            };

            // Every Prepare method reports its own error and returns null; by the time we
            // get here the inputs are known-good, which is exactly the ordering the
            // original got wrong for the IP段 shape.
            if (plan is not null) await RunAsync(plan);
        }
        finally
        {
            _preparing = false;
        }
    }

    /// <summary>Shape 1 — one host (IP or domain) and an inclusive port range.</summary>
    private async Task<ScanPlan?> PrepareRangeAsync()
    {
        var host = await ResolveHostAsync();
        if (host is null) return null;

        var start = IntValue(StartPortBox, 1);
        var end = double.IsNaN(EndPortBox.Value) ? start : IntValue(EndPortBox, start);
        if (end < start) end = start;          // the original's "结束值可为空" fallback

        start = Math.Clamp(start, 1, 65535);
        end = Math.Clamp(end, 1, 65535);

        var ports = new List<int>(end - start + 1);
        for (var port = start; port <= end; port++) ports.Add(port);

        // 开始扫描【{txtIP.Text}】 端口范围 {num}-{num2} — one ASCII space, ASCII hyphen.
        return new ScanPlan([host], ports, Stamp($"开始扫描【{HostBox.Text.Trim()}】 端口范围 {start}-{end}"));
    }

    /// <summary>Shape 2 — one host and an explicit port list.</summary>
    private async Task<ScanPlan?> PrepareSingleListAsync()
    {
        var host = await ResolveHostAsync();
        if (host is null) return null;

        var ports = ParsePorts(PortListBox);
        if (ports.Count == 0)
        {
            await InfoAsync("请填写1~65535之间的端口");
            return null;
        }

        NormalizePortsBox(PortListBox, ports);

        // 开始扫描【{txtIP.Text}】的指定端口 {txtIPPorts.Text} — no space before 的.
        return new ScanPlan([host], ports, Stamp($"开始扫描【{HostBox.Text.Trim()}】的指定端口 {DescribePorts(ports)}"));
    }

    /// <summary>Shape 3 — a three-octet prefix plus a last-octet range.</summary>
    private async Task<ScanPlan?> PrepareSegmentAsync()
    {
        // The original matched ^(\d{1,3}\.\d{1,3}\.\d{1,3}) and discarded any fourth
        // octet, so "192.168.1.50" meant "192.168.1"; GetSegment does the same but
        // range-checks the octets instead of accepting "999.1.1".
        var prefix = IpMath.GetSegment(SegmentBox.Text);
        if (!IpMath.IsValidSegment(prefix))
        {
            await InfoAsync("请输入正确的IP扫描段");
            return null;
        }

        if (double.IsNaN(StartIpBox.Value))
        {
            await InfoAsync("请填写开始IP");
            return null;
        }

        var start = IntValue(StartIpBox, 1);
        var end = double.IsNaN(EndIpBox.Value) ? start : IntValue(EndIpBox, start);

        if (start > end)
        {
            await InfoAsync("开始IP不得大于结束IP");
            return null;
        }

        var ports = ParsePorts(SegmentPortsBox);
        if (ports.Count == 0)
        {
            await InfoAsync("请填写1~65535之间的端口");
            return null;
        }

        // ParseTargets owns the "A.B.C.1-A.B.C.50" range form, so the segment shape and
        // the CIDR shape can never drift apart.
        var targets = PortScanner.ParseTargets($"{prefix}.{start}-{prefix}.{end}");
        if (targets.Count == 0)
        {
            await InfoAsync("请输入正确的IP扫描段");
            return null;
        }

        NormalizePortsBox(SegmentPortsBox, ports);
        SaveHistoryPorts();

        return new ScanPlan(targets, ports,
            Stamp($"开始扫描【{targets.Count}】个IP的指定端口 {DescribePorts(ports)}"));
    }

    /// <summary>Shape 4 — IP/掩码位 (CIDR), with the original's &gt;1000-address prompt.</summary>
    private async Task<ScanPlan?> PrepareCidrAsync()
    {
        // Cleared text and bits outside the spinner's 1..30 range are the original's
        // 掩码位异常 path (CalNBFL also rejected /31 and /32).
        if (double.IsNaN(BitsBox.Value))
        {
            await InfoAsync("掩码位异常，请重新输入");
            return null;
        }

        var bits = IntValue(BitsBox, 24);
        if (bits is < 1 or > 30)
        {
            await InfoAsync("掩码位异常，请重新输入");
            return null;
        }

        // Octets keep the designer defaults when their box was cleared, matching the
        // surviving NumericUpDown values of the original.
        var seed = string.Join(".", new[]
        {
            IntValue(Ip1Box, 192),
            IntValue(Ip2Box, 168),
            IntValue(Ip3Box, 0),
            IntValue(Ip4Box, 1),
        });

        var targets = PortScanner.ParseTargets($"{seed}/{bits}");
        if (targets.Count == 0)
        {
            await InfoAsync("掩码位异常，请重新输入");
            return null;
        }

        // The only rate-limiting guard the original had, and it fires before the ports
        // are parsed — same order as btnScanBatch2_Click.
        if (targets.Count > 1000
            && !await UiKit.ConfirmAsync(XamlRoot, WindowTitle, "当前生成的IP地址已超过1000个，你确认要继续吗？"))
        {
            return null;
        }

        var ports = ParsePorts(SegmentPortsBox);
        if (ports.Count == 0)
        {
            await InfoAsync("请填写1~65535之间的端口");
            return null;
        }

        NormalizePortsBox(SegmentPortsBox, ports);
        SaveHistoryPorts();

        return new ScanPlan(targets, ports,
            Stamp($"开始扫描【{targets.Count}】个IP的指定端口 {DescribePorts(ports)}"));
    }

    /// <summary>
    /// Resolves the 单IP host. The original handed the raw string to
    /// <c>TcpClient.BeginConnect(host, …)</c>; <see cref="TcpProbe"/> is IPv4-only by
    /// design, so a domain is resolved once here instead of once per port.
    /// </summary>
    private async Task<string?> ResolveHostAsync()
    {
        var text = HostBox.Text.Trim();
        if (text.Length == 0)
        {
            await InfoAsync("请填写IP或域名");
            return null;
        }

        if (IpMath.IsValidIPv4(text)) return text;

        var address = await Task.Run(() => IpMath.TryResolve(text, out var resolved) ? resolved : null);
        if (address is null)
        {
            await InfoAsync($"无法解析主机名：{text}");
            return null;
        }

        return address.ToString();
    }

    /// <summary>
    /// The port grammar. <see cref="PortScanner.ParsePorts"/> accepts <c>,</c>/<c>，</c>/
    /// <c>、</c>/space separators, ranges and the <c>all</c> / <c>全部端口</c> keyword, and
    /// returns an ascending, de-duplicated list — the original's normalised order.
    /// </summary>
    private static List<int> ParsePorts(TextBox box) => PortScanner.ParsePorts(box.Text);

    /// <summary>
    /// Rewrites the box as ascending CSV exactly like the original, except that an
    /// expansion larger than <see cref="MaxInlinePorts"/> is left as the user typed it.
    /// </summary>
    private static void NormalizePortsBox(TextBox box, IReadOnlyList<int> ports)
    {
        if (ports.Count is > 0 and <= MaxInlinePorts) box.Text = string.Join(",", ports);
    }

    /// <summary>The port CSV the start line quotes, compacted when it is huge.</summary>
    private static string DescribePorts(IReadOnlyList<int> ports) =>
        ports.Count <= MaxInlinePorts ? string.Join(",", ports) : $"共 {ports.Count} 个端口";

    // =====================================================================
    // history: %TEMP%\IPScaner.FormPortScan.txt (batch shapes only)
    // =====================================================================

    private void LoadHistoryPorts()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            var text = File.ReadAllText(HistoryPath).Trim();
            if (text.Length > 0) SegmentPortsBox.Text = text;
        }
        catch (Exception ex)
        {
            // The original swallowed every IO exception here; log instead of failing.
            AppLog.Instance.Log(nameof(PortScanPage), "读取端口历史失败: " + ex.Message);
        }
    }

    private void SaveHistoryPorts()
    {
        try
        {
            File.WriteAllText(HistoryPath, SegmentPortsBox.Text);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(PortScanPage), "保存端口历史失败: " + ex.Message);
        }
    }

    // =====================================================================
    // running a scan
    // =====================================================================

    private async Task RunAsync(ScanPlan plan)
    {
        var reportClosed = ShowClosedToggle.IsChecked == true;
        var timeoutMs = Math.Clamp(AppServices.Current.Config.PortTimeout, 10, 2000);

        var request = new PortScanRequest
        {
            Targets = plan.Targets,
            Ports = plan.Ports,
            TimeoutMs = timeoutMs,
            ReportClosed = reportClosed,
        };

        _scanning = true;
        SetScanButtonsEnabled(running: true);
        UpdateTimeoutLabel();

        AddLogLine(plan.StartLine);
        CopyButton.IsEnabled = true;
        ProgressText.Text =
            $"正在扫描 {plan.Targets.Count} 个地址 × {plan.Ports.Count} 个端口，共 {request.TotalProbes} 个探测…";

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _flushTimer.Start();

        // Progress<T> posts through the captured UI synchronization context.
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!ReferenceEquals(_scanCts, cts)) return;
            ProgressText.Text = $"正在扫描 {p.CurrentIP}  ({p.Completed}/{p.Total})" +
                                $"　开放 {Volatile.Read(ref _openCount)}，关闭 {Volatile.Read(ref _closedCount)}";
        });

        try
        {
            await AppServices.Current.PortScanner.RunAsync(request, OnResultFromWorker, progress, cts.Token);

            // The original still ran its completion sequence after 停止 (Cancel only
            // affects probes that have not started yet), so 端口扫描完毕 and the trailing
            // blank line are written either way.
            AddLogLine(Stamp("端口扫描完毕"));
            AddLogLine(string.Empty);
            ProgressText.Text = cts.IsCancellationRequested ? "用户取消了操作" : "扫描完毕";
            ProgressText.Text += $"：开放 {Volatile.Read(ref _openCount)}，关闭 {Volatile.Read(ref _closedCount)}";
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "用户取消了操作";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(PortScanPage), "端口扫描失败: " + ex.Message);
            ProgressText.Text = "扫描出错：" + ex.Message;
        }
        finally
        {
            FlushPending();
            _flushTimer.Stop();

            if (ReferenceEquals(_scanCts, cts))
            {
                _scanCts = null;
                _scanning = false;
                SetScanButtonsEnabled(running: false);
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Called from the scanner's worker threads for every result the request asked to
    /// see. It must not touch the UI: rows go into a queue that the flush timer drains.
    /// </summary>
    private Task OnResultFromWorker(PortScanResult result)
    {
        var time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        // Exact original wording for open ports; the closed variant is the same sentence
        // with 关闭 substituted, which only exists because 显示关闭端口 is a new toggle.
        var line = result.IsOpen
            ? $"ip:【{result.IP}】 发现开放的端口：{result.Port}"
            : $"ip:【{result.IP}】 发现关闭的端口：{result.Port}";

        lock (_logGate) _log.Add(time + " " + line);

        if (result.IsOpen) Interlocked.Increment(ref _openCount);
        else Interlocked.Increment(ref _closedCount);

        _pending.Enqueue(new ResultRow
        {
            Time = time,
            IP = result.IP,
            Port = result.Port.ToString(CultureInfo.InvariantCulture),
            Service = result.Service,
            Status = result.IsOpen ? "开放" : "关闭",
        });

        return Task.CompletedTask;
    }

    private void OnFlushTick(DispatcherQueueTimer sender, object args) => FlushPending();

    private void FlushPending()
    {
        var added = 0;
        while (added < FlushBatchSize && _pending.TryDequeue(out var row))
        {
            Rows.Add(row);
            added++;
        }

        if (added == 0) return;

        CopyButton.IsEnabled = true;
        if (Rows.Count > 0) ResultList.ScrollIntoView(Rows[^1]);   // the original's TopIndex
    }

    private void SetScanButtonsEnabled(bool running)
    {
        ScanRangeButton.IsEnabled = !running;
        ScanListButton.IsEnabled = !running;
        ScanSegmentButton.IsEnabled = !running;
        ScanCidrButton.IsEnabled = !running;

        StopRangeButton.IsEnabled = running;
        StopListButton.IsEnabled = running;
        StopSegmentButton.IsEnabled = running;
        StopCidrButton.IsEnabled = running;

        // The request is fixed when the scan starts, so the toggle is frozen with it.
        ShowClosedToggle.IsEnabled = !running;
    }

    // =====================================================================
    // result log / copy
    // =====================================================================

    /// <summary>HH:mm:ss prefix, applied to every non-empty line like the original.</summary>
    private static string Stamp(string text) =>
        DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + text;

    private void AddLogLine(string line)
    {
        lock (_logGate) _log.Add(line);
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string text;
        lock (_logGate) text = string.Join(Environment.NewLine, _log);

        // The original copied nothing and said nothing when the list was empty.
        if (text.Length == 0) return;

        if (!UiKit.CopyToClipboard(text))
        {
            await InfoAsync("复制失败：无法访问剪贴板。");
            return;
        }

        await InfoAsync("内容已复制到剪贴板！");
    }

    // =====================================================================
    // local types
    // =====================================================================

    private enum ScanShape
    {
        SingleRange,
        SingleList,
        SegmentList,
        CidrList,
    }

    /// <summary>A validated scan: what to probe, and the line that announces it.</summary>
    private sealed record ScanPlan(List<string> Targets, List<int> Ports, string StartLine);

    /// <summary>
    /// One grid line. The columns are display-only projections of
    /// <see cref="PortScanResult"/> plus the arrival time, so the grid can be a real
    /// multi-column list without changing the copy payload.
    /// </summary>
    public sealed class ResultRow
    {
        // Settable (not init-only): the XAML type-info generator emits property
        // setters for bound types, and init-only accessors fail that build step.
        public string Time { get; set; } = string.Empty;
        public string IP { get; set; } = string.Empty;
        public string Port { get; set; } = string.Empty;
        public string Service { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
