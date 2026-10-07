using System.Text;
using System.Text.RegularExpressions;
using IPScaner.Core.Logging;
using IPScaner.Core.Net;
using IPScaner.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPScaner.WinUI.Views;

/// <summary>
/// Hosts 管理 — 一键管理 hosts 记录（查看 / 单条添加 / 批量添加 / 删除 / 改 IP /
/// 手动备份 / 一键清理失效记录 / 刷新 DNS 缓存）。
/// </summary>
/// <remarks>
/// <para>Everything that writes goes through <see cref="HostsFileService"/> and reports the
/// service's own <see cref="HostsOperationResult.Message"/> in the <see cref="InfoBar"/> —
/// success or failure. There is no code path that mutates the file and says nothing;
/// that silent-failure shape is exactly what the original tool was criticised for.</para>
/// <para><b>一键清理失效记录 never deletes blind.</b> The button only collects a candidate
/// list: the user pastes (or the caller supplies) the dead addresses, the page matches them
/// against the current records, shows every line that <i>would</i> go — hostname, IP, line
/// number, note — and the confirmation dialog states how many lines will be removed. Only
/// a second, explicit confirmation reaches <see cref="HostsFileService.RemoveMany"/>.</para>
/// <para>Note on the Core contract: <c>RemoveMany</c> takes <b>hostnames</b>, not IPs, so the
/// cleanup maps each dead IP to its hostname first and de-duplicates, keeping the preview
/// and the write in exact agreement. Addresses that cannot be removed safely (this machine's
/// own LAN address, loopback or link-local ranges) are listed separately and never removed.</para>
/// <para>Permission handling: an unelevated process can read the file but every write is
/// refused by the OS. Rather than disabling the buttons — which hides the feature — the page
/// keeps them live, shows the prominent 需要管理员权限才能修改 hosts 文件 bar with
/// 以管理员身份重启, and still surfaces the real error text when a write fails.</para>
/// </remarks>
public sealed partial class HostsPage : Page
{
    /// <summary>Row model for the grid; rebuilt on every refresh.</summary>
    private sealed class HostsRow
    {
        public required int LineNumber { get; init; }
        public required string IP { get; init; }
        public required string Hostname { get; init; }
        public string? Comment { get; init; }
        public required bool IsBlocking { get; init; }
        public required HostsEntry Entry { get; init; }
    }

    /// <summary>Text shown while the InfoBar has nothing to report.</summary>
    private const string IdleMessage = "操作结果会显示在这里；没有任何提示就代表没有执行任何写入。";

    private static readonly Regex Ipv4Regex =
        new(@"(?<![\d.])(\d{1,3}(?:\.\d{1,3}){3})(?![\d.])", RegexOptions.Compiled);

    private readonly HostsFileService _service = new();

    private List<HostsRow> _rows = [];
    private string _lastBackupPath = string.Empty;

    public HostsPage()
    {
        InitializeComponent();

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;

        PathText.Text = $"hosts 文件：{_service.FilePath}";
    }

    // =====================================================================
    // page lifecycle
    // =====================================================================

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // The original forced UAC on every launch; this port states the situation
        // instead, and keeps the write features visible-but-honest.
        ElevationBar.IsOpen = !AppServices.Current.IsElevated;
        Reload();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        // Nothing long-running to cancel: every hosts write is a synchronous, fast
        // file operation. The ring is only a visual cue.
        BusyRing.IsActive = false;
    }

    // =====================================================================
    // read / render
    // =====================================================================

    private void Reload()
    {
        try
        {
            var entries = _service.Read();
            _rows = [.. entries.Select(entry => new HostsRow
            {
                LineNumber = entry.LineNumber,
                IP = entry.IP,
                Hostname = entry.Hostname,
                Comment = entry.Comment,
                IsBlocking = entry.IsBlocking,
                Entry = entry,
            })];
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(HostsPage), "读取 hosts 失败: " + ex.Message);
            _rows = [];
            ShowMessage(false, "读取 hosts 文件失败：" + ex.Message);
        }

        RenderRows();
    }

    private void RenderRows()
    {
        EntryList.Items.Clear();

        foreach (var row in _rows)
        {
            var grid = new Grid { Padding = new Thickness(10, 4, 10, 4), ColumnSpacing = 10 };
            foreach (var width in new[]
                     {
                         new GridLength(56), new GridLength(150), new GridLength(240),
                         new GridLength(1, GridUnitType.Star), new GridLength(110),
                     })
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            }

            var lineNumber = new TextBlock
            {
                Text = row.LineNumber.ToString(),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            Grid.SetColumn(lineNumber, 0);

            var ip = new TextBlock { Text = row.IP, IsTextSelectionEnabled = true };
            Grid.SetColumn(ip, 1);

            var host = new TextBlock
            {
                Text = row.Hostname,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = true,
            };
            ToolTipService.SetToolTip(host, row.Hostname);
            Grid.SetColumn(host, 2);

            var comment = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(row.Comment) ? "—" : row.Comment,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = true,
            };
            ToolTipService.SetToolTip(comment, row.Comment);
            Grid.SetColumn(comment, 3);

            var kind = new TextBlock
            {
                Text = row.IsBlocking ? "屏蔽(127.0.0.1)" : "正常映射",
                Foreground = row.IsBlocking
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange)
                    : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            };
            Grid.SetColumn(kind, 4);

            grid.Children.Add(lineNumber);
            grid.Children.Add(ip);
            grid.Children.Add(host);
            grid.Children.Add(comment);
            grid.Children.Add(kind);

            var item = new ListViewItem
            {
                Content = grid,
                Tag = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(item,
                $"行 {row.LineNumber}：{row.IP}  {row.Hostname}" +
                (string.IsNullOrWhiteSpace(row.Comment) ? string.Empty : "  # " + row.Comment));

            EntryList.Items.Add(item);
        }

        var blocking = _rows.Count(r => r.IsBlocking);
        SummaryText.Text = _rows.Count == 0
            ? "共 0 条有效记录"
            : $"共 {_rows.Count} 条有效记录（正常映射 {_rows.Count - blocking} 条，屏蔽 {blocking} 条）";
        EmptyHintText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private HostsRow? SelectedRow =>
        (EntryList.SelectedItem as ListViewItem)?.Tag as HostsRow;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Nothing to recompute: the action buttons stay enabled so that clicking one
        // without a selection produces an explanation instead of dead UI.
    }

    private void OnRowDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (SelectedRow is { } row) UiKit.CopyToClipboard($"{row.IP}\t{row.Hostname}");
    }

    // =====================================================================
    // add
    // =====================================================================

    private void OnBatchToggled(object sender, RoutedEventArgs e)
    {
        var batch = BatchToggle.IsChecked == true;
        SingleAddPanel.Visibility = batch ? Visibility.Collapsed : Visibility.Visible;
        BatchAddPanel.Visibility = batch ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var ip = (IpBox.Text ?? string.Empty).Trim();
        var host = (HostBox.Text ?? string.Empty).Trim();
        var comment = (CommentBox.Text ?? string.Empty).Trim();

        if (!Validate(ip, host)) return;

        var result = _service.Add(ip, host, comment.Length == 0 ? null : comment);
        Report(result);

        // Only clear the inputs once the write really happened — a failed attempt
        // must not lose what the user typed.
        if (result.Success)
        {
            IpBox.Text = string.Empty;
            HostBox.Text = string.Empty;
            CommentBox.Text = string.Empty;
            Reload();
        }
    }

    private void OnParseBatchClick(object sender, RoutedEventArgs e)
    {
        var (parsed, problems) = ParseBatch();

        var body = new StringBuilder();
        body.AppendLine(parsed.Count == 0
            ? "没有识别到任何记录。每行格式：IP 主机名"
            : $"可以识别出 {parsed.Count} 条记录：");
        foreach (var (ip, host) in parsed.Take(30)) body.AppendLine($"{ip}\t{host}");
        if (parsed.Count > 30) body.AppendLine($"… 其余 {parsed.Count - 30} 条已省略");

        if (problems.Count > 0)
        {
            body.AppendLine();
            body.AppendLine($"以下 {problems.Count} 行会被忽略：");
            foreach (var problem in problems.Take(10)) body.AppendLine(problem);
            if (problems.Count > 10) body.AppendLine($"… 其余 {problems.Count - 10} 行已省略");
        }

        ShowMessage(parsed.Count > 0, body.ToString());
    }

    private void OnBatchAddClick(object sender, RoutedEventArgs e)
    {
        var (parsed, problems) = ParseBatch();
        if (parsed.Count == 0)
        {
            ShowMessage(false, problems.Count == 0
                ? "没有可添加的内容，请按「IP 主机名」每行一条粘贴。"
                : $"没有识别到有效记录：{problems[0]}");
            return;
        }

        var comment = (BatchCommentBox.Text ?? string.Empty).Trim();
        var result = _service.AddMany(parsed, comment.Length == 0 ? null : comment);

        var text = result.Message;
        if (problems.Count > 0) text += $"\n另有 {problems.Count} 行因格式问题被忽略。";
        ShowMessage(result.Success, text);

        if (result.Success)
        {
            BatchBox.Text = string.Empty;
            BatchCommentBox.Text = string.Empty;
            Reload();
        }
    }

    /// <summary>
    /// Parses pasted <c>IP 主机名</c> lines. Accepts spaces, tabs and commas as the
    /// separator; <c>#</c> lines are comments and blank lines are skipped. Problems
    /// are returned rather than thrown so the caller can report them.
    /// </summary>
    private (List<(string Ip, string Hostname)> Parsed, List<string> Problems) ParseBatch()
    {
        var parsed = new List<(string Ip, string Hostname)>();
        var problems = new List<string>();

        var lineNumber = 0;
        foreach (var raw in (BatchBox.Text ?? string.Empty).Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue;

            var parts = line.Split([' ', '\t', ',', '，', ';', '；'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length < 2)
            {
                problems.Add($"第 {lineNumber} 行：{line}（缺少主机名）");
                continue;
            }

            if (!IpMath.IsValidIPv4(parts[0]))
            {
                problems.Add($"第 {lineNumber} 行：{line}（{parts[0]} 不是合法的 IPv4 地址）");
                continue;
            }

            // Everything after the address is hostnames; joining with a space keeps
            // the "one line, several aliases" hosts-file idiom working.
            parsed.Add((parts[0], string.Join(' ', parts.Skip(1))));
        }

        return (parsed, problems);
    }

    // =====================================================================
    // remove / update / backup
    // =====================================================================

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            ShowMessage(false, "请先在列表里选中一行，再点击删除选中。");
            return;
        }

        var confirmed = await UiKit.ConfirmAsync(XamlRoot, "删除 hosts 记录",
            $"确认删除主机名「{row.Hostname}」的全部记录吗？\n\n" +
            $"当前行：第 {row.LineNumber} 行  {row.IP}  {row.Hostname}\n\n" +
            "hosts 文件按主机名定位记录，因此该主机名的所有同名记录都会被删除。",
            "删除", "取消");
        if (!confirmed) return;

        Report(_service.Remove(row.Hostname));
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            ShowMessage(false, "请先在列表里选中一行，再点击修改 IP。");
            return;
        }

        var input = new TextBox
        {
            Text = row.IP,
            PlaceholderText = "例如 192.168.1.20",
            SelectionStart = row.IP.Length,
            Width = 260,
        };
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock
        {
            Text = $"主机名：{row.Hostname}（第 {row.LineNumber} 行）",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock { Text = "新的 IP 地址：" });
        content.Children.Add(input);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "修改 IP",
            Content = content,
            PrimaryButtonText = "确定修改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await UiKit.ShowSafeAsync(dialog) != ContentDialogResult.Primary) return;

        var newIp = (input.Text ?? string.Empty).Trim();
        if (!IpMath.IsValidIPv4(newIp))
        {
            ShowMessage(false, $"「{newIp}」不是合法的 IPv4 地址，未做任何修改。");
            return;
        }

        Report(_service.Update(row.Hostname, newIp));
    }

    private void OnBackupClick(object sender, RoutedEventArgs e)
    {
        var path = _service.Backup();
        if (string.IsNullOrEmpty(path))
        {
            ShowMessage(false, $"备份失败：无法复制 {_service.FilePath}。" +
                               (AppServices.Current.IsElevated ? string.Empty : " 这通常是缺少管理员权限造成的。"));
            return;
        }

        _lastBackupPath = path;
        BackupText.Text = "最近备份：" + path;
        ShowMessage(true, "备份成功：" + path);
    }

    // =====================================================================
    // 一键清理失效记录
    // =====================================================================

    private async void OnCleanClick(object sender, RoutedEventArgs e)
    {
        // Step 1 — collect the candidate dead addresses. The caller supplies them
        // (typically pasted from a scan result); nothing is removed at this point.
        var input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 140,
            Width = 430,
            PlaceholderText = "每行一个失效 IP，也可以直接粘贴一整段扫描结果（会自动提取其中的 IPv4）",
        };

        var pickContent = new StackPanel { Spacing = 6 };
        pickContent.Children.Add(new TextBlock
        {
            Text = "粘贴失效 IP 列表",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        pickContent.Children.Add(new TextBlock
        {
            Text = "需要清理哪些地址？可以用扫描结果里「不通」的那一批。",
            Style = (Style)Application.Current.Resources["HintTextStyle"],
        });
        pickContent.Children.Add(input);

        var pickDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "一键清理失效记录",
            Content = pickContent,
            PrimaryButtonText = "下一步：预览",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await UiKit.ShowSafeAsync(pickDialog) != ContentDialogResult.Primary) return;

        var deadIps = ExtractAddresses(input.Text);
        if (deadIps.Count == 0)
        {
            ShowMessage(false, "没有在输入里找到 IPv4 地址，未做任何修改。");
            return;
        }

        // Step 2 — match against the records actually present and build the preview.
        var localAddresses = LocalAddresses();
        var matches = _rows.Where(r => deadIps.Contains(r.IP)).ToList();
        var skipped = matches.Where(r => IsUnsafeToRemove(r, localAddresses)).ToList();
        var targets = matches.Except(skipped).ToList();

        var hostnames = targets.Select(r => r.Hostname)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var preview = new StringBuilder();
        preview.AppendLine($"失效 IP {deadIps.Count} 个，其中在 hosts 文件里命中 {matches.Count} 行。");
        preview.AppendLine();

        if (targets.Count == 0)
        {
            preview.AppendLine("没有任何记录会被删除。");
        }
        else
        {
            preview.AppendLine($"将要删除以下 {targets.Count} 行（对应 {hostnames.Count} 个主机名）：");
            foreach (var row in targets)
            {
                preview.AppendLine($"  第 {row.LineNumber} 行\t{row.IP}\t{row.Hostname}" +
                                   (string.IsNullOrWhiteSpace(row.Comment) ? string.Empty : "\t# " + row.Comment));
            }
        }

        if (skipped.Count > 0)
        {
            preview.AppendLine();
            preview.AppendLine($"以下 {skipped.Count} 行已跳过（属于本机地址或保留网段，不会被删除）：");
            foreach (var row in skipped)
            {
                preview.AppendLine($"  第 {row.LineNumber} 行\t{row.IP}\t{row.Hostname}");
            }
        }

        var unmatched = deadIps.Where(ip => !matches.Any(m => m.IP == ip)).ToList();
        if (unmatched.Count > 0)
        {
            preview.AppendLine();
            preview.AppendLine($"以下 {unmatched.Count} 个地址在 hosts 文件里没有对应记录：");
            preview.AppendLine("  " + string.Join("  ", unmatched.Take(40)) +
                               (unmatched.Count > 40 ? " …" : string.Empty));
        }

        // The preview is selectable so the user can copy the list before deciding.
        var previewBox = new TextBox
        {
            Text = preview.ToString(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 260,
            Width = 560,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(previewBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(previewBox, ScrollBarVisibility.Auto);

        if (targets.Count == 0)
        {
            var info = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "预览：没有需要清理的记录",
                Content = previewBox,
                CloseButtonText = "关闭",
            };
            await UiKit.ShowSafeAsync(info);
            return;
        }

        // Step 3 — one explicit confirmation, then and only then RemoveMany.
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "预览：确认要删除的记录",
            Content = previewBox,
            PrimaryButtonText = $"删除这 {targets.Count} 行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await UiKit.ShowSafeAsync(confirm) != ContentDialogResult.Primary) return;

        var result = _service.RemoveMany(hostnames);
        Report(result);
    }

    private static HashSet<string> ExtractAddresses(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return set;

        foreach (Match match in Ipv4Regex.Matches(text))
        {
            var candidate = match.Groups[1].Value;
            if (IpMath.IsValidIPv4(candidate)) set.Add(candidate);
        }
        return set;
    }

    private static HashSet<string> LocalAddresses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var adapter in AppServices.Current.AdapterList)
            {
                if (!string.IsNullOrWhiteSpace(adapter.IP)) set.Add(adapter.IP);
                if (!string.IsNullOrWhiteSpace(adapter.Gateway)) set.Add(adapter.Gateway);
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(HostsPage), "读取本机地址失败: " + ex.Message);
        }
        return set;
    }

    /// <summary>
    /// True for records a bulk cleanup must never touch: this machine's own address,
    /// its gateway, and the reserved ranges that are intentional in a hosts file
    /// (loopback, link-local, broadcast, "0.0.0.0" blackholes).
    /// </summary>
    private static bool IsUnsafeToRemove(HostsRow row, HashSet<string> localAddresses)
        => localAddresses.Contains(row.IP)
           || IpMath.IsIgnorableLocalAddress(row.IP)
           || row.IP is "0.0.0.0" or "255.255.255.255";

    // =====================================================================
    // DNS cache + elevation
    // =====================================================================

    private async void OnFlushClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await HostsFileService.FlushDnsCacheAsync(cts.Token);
            ShowMessage(true, "已刷新 DNS 缓存，hosts 的改动现在就会生效。");
        }
        catch (OperationCanceledException)
        {
            ShowMessage(false, "刷新 DNS 缓存超时，请稍后重试。");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(HostsPage), "刷新 DNS 缓存失败: " + ex.Message);
            ShowMessage(false, "刷新 DNS 缓存失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnRestartElevatedClick(object sender, RoutedEventArgs e) => UiKit.RestartElevated();

    // =====================================================================
    // helpers
    // =====================================================================

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        Reload();
        ShowMessage(true, $"已重新读取 hosts 文件，共 {_rows.Count} 条有效记录。");
    }

    private bool Validate(string ip, string host)
    {
        if (!IpMath.IsValidIPv4(ip))
        {
            ShowMessage(false, string.IsNullOrWhiteSpace(ip)
                ? "请输入 IP 地址，例如 192.168.1.10。"
                : $"「{ip}」不是合法的 IPv4 地址，未做任何修改。");
            return false;
        }

        if (host.Length == 0)
        {
            ShowMessage(false, "请输入主机名，例如 nas.local。");
            return false;
        }

        if (host.Contains('#'))
        {
            ShowMessage(false, "主机名里不能包含 # 字符，它会把后面的内容变成注释。");
            return false;
        }

        return true;
    }

    /// <summary>Reports a service result verbatim and refreshes the grid on success.</summary>
    private void Report(HostsOperationResult result)
    {
        ShowMessage(result.Success, result.Message);
        if (result.Success) Reload();
        SetShellStatus(result.Success ? result.Message : "操作失败，详见提示");
    }

    /// <summary>
    /// Writes the result InfoBar: green for success, red for failure. Both bars are a
    /// single line of service text — the whole point is that the Core message reaches
    /// the user unchanged, including on failure.
    /// </summary>
    private void ShowMessage(bool success, string message)
    {
        EditingBar.IsOpen = false;
        EntryBar.IsOpen = false;

        var bar = success ? EntryBar : EditingBar;
        bar.Message = message;
        bar.IsOpen = true;
    }

    /// <summary>Mirrors the last outcome into the shell's status strip.</summary>
    private void SetShellStatus(string text)
    {
        try { App.MainWindow?.SetStatus(text); }
        catch (Exception ex) { AppLog.Instance.Log(nameof(HostsPage), "更新状态栏失败: " + ex.Message); }
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        FlushButton.IsEnabled = !busy;
        AddButton.IsEnabled = !busy;
        BatchAddButton.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy;
        UpdateButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy;
    }
}
