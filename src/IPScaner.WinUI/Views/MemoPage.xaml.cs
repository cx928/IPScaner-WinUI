using System.Text.RegularExpressions;
using IPScaner.Core.Caching;
using IPScaner.Core.Logging;
using IPScaner.Core.Memo;
using IPScaner.Core.Net;
using IPScaner.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace IPScaner.WinUI.Views;

/// <summary>
/// 备注管理 — the memo manager (the original's <c>FormMemoManager</c> plus its
/// <c>FormIPMemo</c> editor, which is now the 新增/修改 dialog).
/// </summary>
/// <remarks>
/// One row per entry of <see cref="MemoStore"/>; a key is either an IPv4 address
/// or a MAC address. Faithful to the original:
/// <list type="bullet">
/// <item>the list is the single source of truth and every mutation is written
/// straight back to <c>IPScanerMemo.dat</c>,</item>
/// <item>rows are striped LightBlue on every second line and numbered from 1,</item>
/// <item>double-clicking a row opens the editor,</item>
/// <item>从剪贴板导入 reuses the store's parser and keeps the original's
/// <c>成功导入{N}条备注信息</c> / format-hint wording.</item>
/// </list>
/// Deliberate fixes, all documented in the porting notes:
/// <list type="bullet">
/// <item>key validation — the original's <c>Utility.ValidIP</c> was an unanchored
/// regex that accepted <c>999.999.999.999</c> and <c>10.0.0.1abc</c>, and MAC keys
/// were never validated at all; both are checked here and MAC keys are folded to
/// the ARP shape (upper case, '-'),</item>
/// <item>import shows a preview (recognised / new / already present) instead of
/// silently skipping duplicates and printing a bare count,</item>
/// <item>清空全部 really empties the file — the original's <c>SaveMemoInfo</c>
/// returned early on an empty dictionary and left a stale file behind.</item>
/// </list>
/// The MAC-wins precedence rule is honoured wherever this page resolves a note
/// (see <see cref="BuildResolveText"/>), because it is what lets a note follow a
/// machine across a DHCP lease change.
/// </remarks>
public sealed partial class MemoPage : Page
{
    /// <summary><c>Color.LightBlue.ToArgb()</c> — the original grid's stripe colour.</summary>
    private const int StripeArgb = -5383962;

    /// <summary>MAC key shape: six hex pairs separated by '-' or ':'.</summary>
    private const string MacPattern = @"^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$";

    private static readonly Regex MacKeyRegex = new(MacPattern, RegexOptions.Compiled);

    /// <summary>Verbatim failure text of the original 导入剪贴板数据 handler.</summary>
    private const string ImportFormatHint =
        "未识别到有效的IP备注信息。格式参照如下：\r\n" +
        "IP在前，备注信息在后，例如：\r\n" +
        "192.168.0.1 = 监控服务器";

    // Column widths — keep in sync with the header row in MemoPage.xaml.
    private const double IndexColumnWidth = 50;
    private const double KeyColumnWidth = 200;
    private const double TypeColumnWidth = 90;

    private readonly CancellationTokenSource _cts = new();

    /// <summary>Every entry of the store, in the order the grid shows it.</summary>
    private List<MemoRow> _rows = [];

    private string _filter = string.Empty;
    private string? _selectedKey;
    private string _flash = string.Empty;
    private int _visibleCount;

    public MemoPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // =====================================================================
    // lifecycle
    // =====================================================================

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _filter = (FilterBox.Text ?? string.Empty).Trim();

        // The store is shared with the scan page (设置备注 writes into it), so the
        // view is re-read from it on every activation.
        ReloadFromStore();
        LocatePendingKey();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        // Nothing here is long-running, but the contract's cancellation rule still
        // applies: work started by this page must stop when the page goes away.
        _cts.Cancel();
        _cts.Dispose();
    }

    // =====================================================================
    // model
    // =====================================================================

    /// <summary>One grid row.</summary>
    private sealed record MemoRow(string Key, string Value);

    /// <summary>MAC-shaped according to the store's rule <i>and</i> well formed.</summary>
    private static bool IsMacKey(string key) => MemoStore.IsMacKey(key) && MacKeyRegex.IsMatch(key);

    /// <summary>The 类型 column: MAC地址 / IP地址 (其他 only for keys the file already holds).</summary>
    private static string TypeLabel(string key) =>
        IsMacKey(key) ? "MAC地址" : IpMath.IsValidIPv4(key) ? "IP地址" : "其他";

    /// <summary>MAC keys are stored upper-case with '-' separators (the ARP shape).</summary>
    private static string NormaliseMac(string mac) => mac.Replace(':', '-').ToUpperInvariant();

    /// <summary>Looks a shared style up in the app resources (null when absent).</summary>
    private static Style? ThemeStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    /// <summary>Looks a theme brush up in the app resources (null when absent).</summary>
    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private static string SingleLine(string text) =>
        text.Replace("\r\n", "  ").Replace('\r', ' ').Replace('\n', ' ');

    // =====================================================================
    // grid
    // =====================================================================

    private void ReloadFromStore()
    {
        _rows = AppServices.Current.Memo.Entries
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new MemoRow(kv.Key, kv.Value))
            .ToList();

        RebuildList();
    }

    private bool Matches(MemoRow row)
    {
        if (_filter.Length == 0) return true;

        // Case-insensitive over the key (IP *or* MAC) and the note text. The
        // original searched the IP and the memo only, so MAC-keyed rows could not
        // be found at all.
        return row.Key.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || row.Value.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildList()
    {
        var visible = _rows.Where(Matches).ToList();
        _visibleCount = visible.Count;

        MemoList.Items.Clear();
        for (var i = 0; i < visible.Count; i++)
        {
            var row = visible[i];
            var item = new ListViewItem
            {
                Tag = row,
                Content = BuildRowContent(row, i),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                MinHeight = 0,
                Margin = new Thickness(0),
            };

            // Zebra striping: the original tinted rows 2, 4, 6… LightBlue.
            if (i % 2 == 1) item.Background = UiKit.BrushFromArgb(StripeArgb);

            // The row content is a Grid, so the automation name has to be set by
            // hand — without it a screen reader would announce an empty row.
            AutomationProperties.SetName(item, $"{row.Key}  {TypeLabel(row.Key)}  {SingleLine(row.Value)}");
            ToolTipService.SetToolTip(item, BuildRowTooltip(row));
            MemoList.Items.Add(item);
        }

        // Keep the selection when it survives the current filter.
        var keep = _selectedKey is null
            ? null
            : visible.FirstOrDefault(r => string.Equals(r.Key, _selectedKey, StringComparison.OrdinalIgnoreCase));

        _selectedKey = keep?.Key;
        MemoList.SelectedItem = keep is null ? null : FindItem(keep.Key);
        if (keep is not null) ScrollTo(keep.Key);

        EditButton.IsEnabled = keep is not null;
        DeleteButton.IsEnabled = keep is not null;

        EmptyHint.Text = _rows.Count == 0
            ? "暂无备注信息。点击【新增】创建单条备注，或用【从剪贴板导入】批量导入。"
            : "没有匹配的记录，请调整查询条件。";
        EmptyHint.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateStatus();
    }

    private Grid BuildRowContent(MemoRow row, int index)
    {
        var grid = new Grid { Padding = new Thickness(8, 6, 8, 6), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IndexColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TypeColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var number = new TextBlock
        {
            Text = (index + 1).ToString(),
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var key = new TextBlock
        {
            Text = row.Key,
            FontFamily = new FontFamily("Consolas"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var type = new TextBlock
        {
            Text = TypeLabel(row.Key),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var memo = new TextBlock
        {
            Text = row.Value.Length == 0 ? "（空备注）" : SingleLine(row.Value),
            Opacity = row.Value.Length == 0 ? 0.6 : 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(memo, row.Value.Length == 0
            ? "空备注：保存时会被丢弃"
            : row.Value);

        Grid.SetColumn(number, 0);
        Grid.SetColumn(key, 1);
        Grid.SetColumn(type, 2);
        Grid.SetColumn(memo, 3);
        grid.Children.Add(number);
        grid.Children.Add(key);
        grid.Children.Add(type);
        grid.Children.Add(memo);
        return grid;
    }

    private static string BuildRowTooltip(MemoRow row)
    {
        var kind = TypeLabel(row.Key);
        var text = $"键：{row.Key}\r\n类型：{kind}\r\n备注：{(row.Value.Length == 0 ? "（空）" : row.Value)}";
        if (kind == "其他")
            text += "\r\n注意：该键既不是合法的IP地址，也不是合法的MAC地址，扫描页无法解析它。";
        return text;
    }

    private ListViewItem? FindItem(string key) =>
        MemoList.Items.OfType<ListViewItem>().FirstOrDefault(
            item => item.Tag is MemoRow row && string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase));

    private void ScrollTo(string key)
    {
        var item = FindItem(key);
        if (item is null) return;
        try { MemoList.ScrollIntoView(item); }
        catch (Exception ex) { AppLog.Instance.Log(nameof(MemoPage), "滚动到备注行失败: " + ex.Message); }
    }

    private MemoRow? SelectedRow() => (MemoList.SelectedItem as ListViewItem)?.Tag as MemoRow;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedKey = SelectedRow()?.Key;
        EditButton.IsEnabled = _selectedKey is not null;
        DeleteButton.IsEnabled = _selectedKey is not null;
        UpdateStatus();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _filter = (FilterBox.Text ?? string.Empty).Trim();
        _flash = string.Empty;
        RebuildList();
    }

    private async void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        try { await ShowEditorAsync(SelectedRow()); }
        catch (Exception ex) { AppLog.Instance.Log(nameof(MemoPage), "打开备注编辑器失败: " + ex.Message); }
    }

    // =====================================================================
    // status line
    // =====================================================================

    private void UpdateStatus()
    {
        var memo = AppServices.Current.Memo;

        FilterHintText.Text = _filter.Length == 0
            ? $"共 {memo.Count} 条"
            : $"匹配 {_visibleCount} 条";

        // Counts first so a long message is what gets trimmed on a narrow window.
        var counts = $"共 {memo.Count} 条备注，当前显示 {_visibleCount} 条";
        StatusText.Text = _flash.Length > 0 ? $"{counts}    ·    {_flash}" : counts;

        PathText.Text = $"备注文件：{memo.FilePath}";
        ResolveText.Text = BuildResolveText(_selectedKey ?? SelectedRow()?.Key);
        App.MainWindow?.SetStatus($"备注管理：共 {memo.Count} 条，当前显示 {_visibleCount} 条");
    }

    /// <summary>
    /// Shows how the note of the selected key resolves, using the documented
    /// MAC-first rule: a host that has both an IP and a MAC entry is answered by
    /// the MAC entry, so the note survives a DHCP lease change.
    /// </summary>
    private static string BuildResolveText(string? key)
    {
        var memo = AppServices.Current.Memo;
        if (key is null)
            return "已选：无    ·    选中一行可查看解析结果（MAC条目优先于IP条目）。";

        var head = $"已选：{key}（{TypeLabel(key)}）    ·    ";

        if (IsMacKey(key))
        {
            var note = memo.Lookup(key, null);
            return note.Length == 0
                ? $"{head}备注解析（MAC 优先）：【{key}】暂无备注内容"
                : $"{head}备注解析（MAC 优先）：{SingleLine(note)}    ·    来源：MAC条目 {key}";
        }

        // The scan page caches the MAC it learned from the ARP table. Reading that
        // cache (no ARP call, no cache flush) keeps this page side-effect free.
        if (ScanCaches.MacAddresses.TryGet(key, out var mac) && !string.IsNullOrEmpty(mac))
        {
            var note = memo.Lookup(mac, key);
            var fromMac = memo.TryGet(mac, out _);
            return note.Length == 0
                ? $"{head}备注解析（MAC 优先）：【{key}】暂无备注内容"
                : $"{head}备注解析（MAC 优先）：{SingleLine(note)}    ·    来源：{(fromMac ? $"MAC条目 {mac}" : $"IP条目 {key}")}";
        }

        var byIp = memo.Lookup(null, key);
        return byIp.Length == 0
            ? $"{head}备注解析（MAC 优先）：【{key}】暂无备注内容"
            : $"{head}备注解析（MAC 优先）：{SingleLine(byIp)}    ·    来源：IP条目 {key}（本机尚未缓存此IP的MAC）";
    }

    // =====================================================================
    // add / edit
    // =====================================================================

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        try { await ShowEditorAsync(null); }
        catch (Exception ex) { AppLog.Instance.Log(nameof(MemoPage), "新增备注失败: " + ex.Message); }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        try { await ShowEditorAsync(SelectedRow()); }
        catch (Exception ex) { AppLog.Instance.Log(nameof(MemoPage), "修改备注失败: " + ex.Message); }
    }

    /// <summary>
    /// The 新增/修改 dialog (the original's <c>FormIPMemo</c>). Validation happens
    /// inside the dialog: a rejected key keeps it open, exactly like the original's
    /// <c>MessageBox</c> + <c>return</c> did, but without nesting two ContentDialogs.
    /// </summary>
    private async Task ShowEditorAsync(MemoRow? existing)
    {
        var keyBox = new TextBox
        {
            Header = "键（IP或MAC）",
            Text = existing?.Key ?? string.Empty,
            PlaceholderText = "192.168.0.1 或 AA-BB-CC-DD-EE-FF",
            Width = 380,
        };
        AutomationProperties.SetName(keyBox, "键（IP或MAC）");

        var memoBox = new TextBox
        {
            Header = "备注内容",
            Text = existing?.Value ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 150,
            Width = 380,
        };
        AutomationProperties.SetName(memoBox, "备注内容");
        ScrollViewer.SetVerticalScrollBarVisibility(memoBox, ScrollBarVisibility.Auto);

        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = ThemeBrush("SystemFillColorCriticalBrush") ?? UiKit.BrushFromArgb(-3318692),
        };

        var hint = new TextBlock { Text = "IP或MAC只填一个即可。若同时存在，以MAC为主。", TextWrapping = TextWrapping.Wrap };
        if (ThemeStyle("HintTextStyle") is { } hintStyle) hint.Style = hintStyle;

        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(keyBox);
        panel.Children.Add(memoBox);
        panel.Children.Add(hint);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            // The original titled the editor "备注 <ip>" (parameterless: "备注 ").
            Title = existing is null ? "备注" : "备注 " + existing.Key,
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (dialogSender, args) =>
        {
            _ = dialogSender;
            if (TryNormaliseKey(keyBox.Text, out _, out var message)) return;
            args.Cancel = true;          // keep the dialog open, like the original
            error.Text = message;
            error.Visibility = Visibility.Visible;
        };

        var result = await UiKit.ShowSafeAsync(dialog);
        if (result != ContentDialogResult.Primary || _cts.IsCancellationRequested) return;

        if (!TryNormaliseKey(keyBox.Text, out var key, out var failure))
        {
            _flash = failure ?? "键不合法";
            UpdateStatus();
            return;
        }

        // TrimEnd() only — the original kept leading whitespace and internal
        // newlines of a memo, and so do we.
        ApplyEdit(existing, key, NormaliseNewlines(memoBox.Text).TrimEnd());
    }

    /// <summary>
    /// Folds every line break a <see cref="TextBox"/> can hand us into CRLF.
    /// WinUI's TextBox returns a lone <c>\r</c> for Enter; <see cref="MemoStore.Save"/>
    /// only escapes CRLF and LF (a faithful copy of the original, which only
    /// escaped <c>\r\n</c>), so a raw <c>\r</c> would be written as a real line
    /// break and split one note into a bogus extra entry on the next load.
    /// </summary>
    private static string NormaliseNewlines(string text)
    {
        if (text.IndexOf('\r') < 0 && text.IndexOf('\n') < 0) return text;

        var sb = new System.Text.StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                sb.Append(Environment.NewLine);
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c == '\n')
            {
                sb.Append(Environment.NewLine);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Validates and normalises a user-entered key.
    /// The original used <c>new Regex("(^\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})")</c>
    /// — not end-anchored, so <c>999.999.999.999</c>, <c>10.0.0.1abc</c> and
    /// <c>10.0.0.1.5</c> were all accepted — and never validated a MAC at all.
    /// </summary>
    private static bool TryNormaliseKey(string? raw, out string key, out string? error)
    {
        key = (raw ?? string.Empty).Trim();
        error = null;

        if (key.Length == 0)
        {
            error = "IP地址或MAC地址不能同时为空";
            return false;
        }

        if (MemoStore.IsMacKey(key))
        {
            if (!MacKeyRegex.IsMatch(key))
            {
                error = "请填写正确的MAC地址";
                return false;
            }

            key = NormaliseMac(key);
            return true;
        }

        if (!IpMath.IsValidIPv4(key))
        {
            error = "请填写正确的IP地址";
            return false;
        }

        return true;
    }

    private void ApplyEdit(MemoRow? existing, string key, string memo)
    {
        var memoStore = AppServices.Current.Memo;
        try
        {
            // Renaming a key is allowed here; the original's 修改 dialog let the
            // user edit the key and then silently discarded it.
            if (existing is not null && !string.Equals(existing.Key, key, StringComparison.Ordinal))
                memoStore.Remove(existing.Key);

            if (memo.Length == 0)
            {
                // MemoStore.Set(key, "") removes the entry, and Save() skips empty
                // values anyway — so an emptied note deletes the row instead of
                // leaving an entry that would silently disappear on the next start.
                var removed = memoStore.Remove(key);
                _flash = removed
                    ? $"备注内容为空，已删除【{key}】的备注"
                    : $"备注内容为空，未创建条目【{key}】";
            }
            else
            {
                memoStore.Set(key, memo);
                _flash = existing is null ? $"已新增【{key}】" : $"已更新【{key}】";
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(MemoPage), "写入备注失败: " + ex.Message);
            _flash = "写入备注失败：" + ex.Message;
        }

        _selectedKey = key;
        var error = PersistMemo();
        if (error is not null) _flash += "；" + error;
        ReloadFromStore();
    }

    // =====================================================================
    // delete / clear
    // =====================================================================

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow();
        if (row is null) return;

        // Original wording: 确定要删除【<ip>】? — it interpolated the IP even for a
        // MAC-only row, so those read 确定要删除【】?. We interpolate the real key.
        var confirmed = await UiKit.ConfirmAsync(XamlRoot, "备注信息管理", $"确定要删除【{row.Key}】?");
        if (!confirmed || _cts.IsCancellationRequested) return;

        try
        {
            if (AppServices.Current.Memo.Remove(row.Key))
            {
                _selectedKey = null;
                _flash = $"已删除【{row.Key}】";
                var error = PersistMemo();
                if (error is not null) _flash += "；" + error;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(MemoPage), "删除备注失败: " + ex.Message);
            _flash = "删除备注失败：" + ex.Message;
        }

        ReloadFromStore();
    }

    private async void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        var memoStore = AppServices.Current.Memo;
        var count = memoStore.Count;
        if (count == 0)
        {
            await UiKit.InfoAsync(XamlRoot, "备注信息管理", "当前没有备注信息。");
            return;
        }

        var confirmed = await UiKit.ConfirmAsync(
            XamlRoot, "备注信息管理",
            $"确定要清空全部 {count} 条备注信息?\r\n清空后会立即把备注文件写为空文件（IPScanerMemo.dat）。");
        if (!confirmed || _cts.IsCancellationRequested) return;

        try
        {
            memoStore.Clear();
            _selectedKey = null;
            _flash = $"已清空全部备注（{count} 条）";

            // The original's SaveMemoInfo returned early when the dictionary was
            // empty, so the old entries came back on the next start; Save() here
            // always writes, which is what makes an emptied file stick.
            var error = PersistMemo();
            if (error is not null) _flash += "；" + error;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(MemoPage), "清空备注失败: " + ex.Message);
            _flash = "清空备注失败：" + ex.Message;
        }

        ReloadFromStore();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var memoStore = AppServices.Current.Memo;
        var error = PersistMemo();
        if (error is null)
        {
            _flash = $"已保存到备注文件（{memoStore.Count} 条）";
        }
        else
        {
            _flash = error;
            await UiKit.InfoAsync(XamlRoot, "备注信息管理", error);
        }

        ReloadFromStore();
    }

    /// <summary>Writes <c>IPScanerMemo.dat</c>; returns null on success, else the error text.</summary>
    private static string? PersistMemo()
    {
        try
        {
            AppServices.Current.Memo.Save();
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(MemoPage), "保存备注文件失败: " + ex.Message);
            return "保存备注文件失败：" + ex.Message;
        }
    }

    // =====================================================================
    // clipboard import
    // =====================================================================

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = await UiKit.ReadClipboardAsync();
            if (_cts.IsCancellationRequested) return;

            // The original returned silently for an empty clipboard; keep that.
            if (string.IsNullOrEmpty(text)) return;

            // Same parser the store uses for pasted data (KEY=VALUE, TAB / comma /
            // space separated, '#' comments; a bare key is ignored).
            var parsed = MemoStore.ParseClipboard(text);
            if (parsed.Count == 0)
            {
                await UiKit.InfoAsync(XamlRoot, "备注信息管理", ImportFormatHint);
                return;
            }

            var memoStore = AppServices.Current.Memo;
            var additions = new List<KeyValuePair<string, string>>();
            var duplicates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (rawKey, value) in parsed)
            {
                var key = NormaliseImportedKey(rawKey);
                if (!seen.Add(key) || memoStore.Entries.ContainsKey(key))
                {
                    duplicates.Add(key);     // first occurrence wins, nothing is overwritten
                    continue;
                }

                additions.Add(new KeyValuePair<string, string>(key, value));
            }

            if (additions.Count == 0)
            {
                _flash = $"剪贴板中的 {duplicates.Count} 条备注均已存在，未做修改";
                UpdateStatus();
                await UiKit.InfoAsync(XamlRoot, "从剪贴板导入",
                    $"识别到 {parsed.Count} 条备注信息，新增 0 条，已存在 {duplicates.Count} 条。\r\n" +
                    "已存在的记录不会被覆盖。");
                return;
            }

            // Preview: how many were recognised and what would actually change.
            var confirm = await UiKit.ShowContentAsync(
                XamlRoot, "从剪贴板导入",
                BuildImportPreview(parsed.Count, additions, duplicates.Count),
                "导入", null, "取消");
            if (confirm != ContentDialogResult.Primary || _cts.IsCancellationRequested) return;

            foreach (var (key, value) in additions) memoStore.Set(key, value);

            _selectedKey = additions[0].Key;
            var error = PersistMemo();
            _flash = error ?? $"成功导入{additions.Count}条备注信息";
            ReloadFromStore();

            if (error is null)
                await UiKit.InfoAsync(XamlRoot, "备注信息管理", $"成功导入{additions.Count}条备注信息");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(MemoPage), "导入剪贴板数据失败: " + ex.Message);
            _flash = "导入剪贴板数据失败：" + ex.Message;
            UpdateStatus();
        }
    }

    /// <summary>
    /// MAC-shaped clipboard keys are folded to the ARP shape so the scanner can
    /// find them; anything else is stored exactly as pasted — the original
    /// validated nothing on import (e.g. <c>abc=备注</c> was accepted).
    /// </summary>
    private static string NormaliseImportedKey(string key)
    {
        var trimmed = key.Trim();
        return MacKeyRegex.IsMatch(trimmed) ? NormaliseMac(trimmed) : trimmed;
    }

    /// <summary>The preview shown before an import is applied.</summary>
    private static StackPanel BuildImportPreview(
        int recognised, List<KeyValuePair<string, string>> additions, int duplicates)
    {
        var panel = new StackPanel { Spacing = 8, Width = 460 };

        panel.Children.Add(new TextBlock
        {
            Text = $"识别到 {recognised} 条备注信息：新增 {additions.Count} 条，已存在 {duplicates} 条（已存在的不覆盖）。",
            TextWrapping = TextWrapping.Wrap,
        });

        var lines = new StackPanel { Spacing = 2 };
        foreach (var (key, value) in additions.Take(50))
        {
            lines.Children.Add(new TextBlock
            {
                Text = $"{key} = {SingleLine(value)}",
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        if (additions.Count > 50)
        {
            lines.Children.Add(new TextBlock
            {
                Text = $"…… 其余 {additions.Count - 50} 条略",
                FontSize = 12,
                Opacity = 0.7,
            });
        }

        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = lines,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "格式：IP或MAC在前，备注信息在后，例如：192.168.0.1 = 监控服务器",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        return panel;
    }

    // =====================================================================
    // cross-page hand-off
    // =====================================================================

    /// <summary>
    /// Honours <see cref="NavigationArgs.TakeMemoKey"/>: the main grid's 设置备注
    /// flow (or any other page) can ask for one key to be selected and focused.
    /// </summary>
    private void LocatePendingKey()
    {
        var pending = NavigationArgs.TakeMemoKey();
        if (string.IsNullOrEmpty(pending)) return;

        var wanted = pending.Trim();
        var normalised = NormaliseImportedKey(wanted);
        var match = _rows.FirstOrDefault(row =>
            string.Equals(row.Key, wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormaliseImportedKey(row.Key), normalised, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            _flash = $"未找到键为【{wanted}】的备注，点击【新增】可创建";
            UpdateStatus();
            return;
        }

        _selectedKey = match.Key;
        if (FindItem(match.Key) is { } item)
        {
            MemoList.SelectedItem = item;
            ScrollTo(match.Key);
            try { item.Focus(FocusState.Programmatic); }
            catch (Exception ex) { AppLog.Instance.Log(nameof(MemoPage), "聚焦备注行失败: " + ex.Message); }
        }

        _flash = $"已定位到【{match.Key}】";
        UpdateStatus();
    }
}
