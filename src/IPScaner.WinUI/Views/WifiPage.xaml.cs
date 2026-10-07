using IPScaner.Core.Export;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;
using IPScaner.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPScaner.WinUI.Views;

/// <summary>
/// WiFi密码查看 — the page form of the original <c>FormWiFiViewer</c>
/// (title WiFi密码查看器).
/// </summary>
/// <remarks>
/// <para>
/// All data comes from <see cref="IPscaner.Core.Net.WifiService.QueryAsync"/>, which
/// already fixed the original's worst defect: <c>FormWiFiViewer</c> matched the
/// <em>localised</em> console markers <c>所有用户配置文件</c> / <c>关键内容</c>, so on an
/// English Windows it silently listed nothing. This page therefore contains no marker
/// strings, no code-page guessing and no locale-dependent parsing of its own — it only
/// consumes <see cref="WifiProfile"/> fields.
/// </para>
/// <para>
/// Improvement over the original: passwords are masked by default and revealed with the
/// 显示密码 toggle (the WinForms grid always showed the cleartext key). Export and
/// 复制密码 always use the real key.
/// </para>
/// </remarks>
public sealed partial class WifiPage : Page
{
    /// <summary>The original window title, used as every message-box caption.</summary>
    private const string DialogTitle = "WiFi密码查看器";

    /// <summary>Fixed-width mask, so the toggle does not leak the password length.</summary>
    private const string MaskedPassword = "●●●●●●●●";

    /// <summary>
    /// The grid columns — the single source of truth for the caption row and every data
    /// row, so the two can never drift apart. Star widths keep the table inside the shell
    /// window (1180x820 physical pixels ≈ 787x547 units at 150% DPI, i.e. only ~480
    /// usable units per card) while still growing when the window is maximized.
    /// </summary>
    private static readonly (string Header, GridLength Width)[] Columns =
    [
        ("序号", new GridLength(40)),
        ("WiFi名称", new GridLength(1.5, GridUnitType.Star)),
        ("密码", new GridLength(1.5, GridUnitType.Star)),
        ("认证方式", new GridLength(1.4, GridUnitType.Star)),
        ("加密方式", new GridLength(0.9, GridUnitType.Star)),
    ];

    // Instance fields: a Brush is a DependencyObject with UI-thread affinity.
    private readonly Brush _rowBackground = new SolidColorBrush(Colors.Transparent);
    private readonly Brush _selectedRowBackground = new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x78, 0xD4));

    private List<WifiProfile> _profiles = [];
    private CancellationTokenSource? _cts;
    private Border? _selectedRow;
    private WifiProfile? _selected;
    private bool _loading;
    private bool _showPasswords;

    public WifiPage()
    {
        InitializeComponent();
        BuildHeader();

        Loaded += OnPageLoaded;
        Unloaded += (_, _) => CancelPendingWork();
    }

    // =====================================================================
    // load
    // =====================================================================

    private void OnPageLoaded(object sender, RoutedEventArgs e) => _ = LoadProfilesAsync();

    private void CancelPendingWork()
    {
        try { _cts?.Cancel(); }
        catch { /* already gone */ }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadProfilesAsync();

    private async Task LoadProfilesAsync()
    {
        if (_loading) return;

        _loading = true;
        RefreshButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        BusyRing.IsActive = true;
        EmptyBar.IsOpen = false;
        StatusText.Text = "正在读取WiFi配置..."; // the original's statusTip

        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            _profiles = await AppServices.Current.Wifi.QueryAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消读取WiFi配置";
            return;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(WifiPage), "读取WiFi密码失败: " + ex.Message);
            _profiles = [];
            StatusText.Text = "读取WiFi保存密码失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            _loading = false;
            BusyRing.IsActive = false;
            RefreshButton.IsEnabled = true;
            ExportButton.IsEnabled = true;
        }

        RebuildRows();

        if (_profiles.Count == 0)
        {
            // Never leave a blank grid: say what to check instead.
            EmptyBar.IsOpen = true;
            StatusText.Text = "读取WiFi保存密码失败，没有可显示的配置";
        }
        else
        {
            EmptyBar.IsOpen = false;
            StatusText.Text = "WiFi保存密码加载完毕"; // the original's completion statusTip
        }
    }

    // =====================================================================
    // grid
    // =====================================================================

    private void BuildHeader()
    {
        HeaderGrid.ColumnDefinitions.Clear();
        HeaderGrid.Children.Clear();

        for (var i = 0; i < Columns.Length; i++)
        {
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = Columns[i].Width });
            var caption = new TextBlock
            {
                Text = Columns[i].Header,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            Grid.SetColumn(caption, i);
            HeaderGrid.Children.Add(caption);
        }
    }

    /// <summary>Rebuilds every row, keeping the selection when the same item is still listed.</summary>
    private void RebuildRows()
    {
        var previous = _selected;

        RowsPanel.Children.Clear();
        _selectedRow = null;
        _selected = null;

        for (var i = 0; i < _profiles.Count; i++)
        {
            var profile = _profiles[i];
            var row = BuildRow(i + 1, profile);
            RowsPanel.Children.Add(row);
            if (ReferenceEquals(profile, previous)) SelectRow(row, profile);
        }

        SummaryText.Text = $"共 {_profiles.Count} 个已保存的 WiFi";
        CopyButton.IsEnabled = _profiles.Count > 0;
    }

    private Border BuildRow(int index, WifiProfile profile)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 6, 12, 6) };
        foreach (var column in Columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = column.Width });

        AddCell(grid, 0, index.ToString(), secondary: true);
        AddCell(grid, 1, profile.Ssid);
        AddCell(grid, 2, PasswordText(profile));
        AddCell(grid, 3, OrDash(profile.Authentication));
        AddCell(grid, 4, OrDash(profile.Encryption));

        var row = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = _rowBackground,
            Child = grid,
            Tag = profile,
        };

        // A multi-line tooltip carries the values the narrow columns have to trim.
        var details = $"{profile.Ssid}\n认证 {OrDash(profile.Authentication)} · 加密 {OrDash(profile.Encryption)}";
        ToolTipService.SetToolTip(row, new TextBlock
        {
            Text = string.IsNullOrEmpty(profile.Password)
                ? details + "\n开放网络，没有保存密码"
                : details + "\n单击选中，双击复制密码",
            TextWrapping = TextWrapping.Wrap,
        });

        row.Tapped += OnRowTapped;
        row.DoubleTapped += OnRowDoubleTapped;
        return row;
    }

    private static void AddCell(Grid grid, int column, string text, bool secondary = false)
    {
        var block = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (secondary) block.Opacity = 0.7;

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private string PasswordText(WifiProfile profile)
    {
        if (profile.Password.Length == 0) return "（无）";
        return _showPasswords ? profile.Password : MaskedPassword;
    }

    private static string OrDash(string value) => value.Length > 0 ? value : "—";

    // =====================================================================
    // selection + copy
    // =====================================================================

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border { Tag: WifiProfile profile } row) SelectRow(row, profile);
    }

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: WifiProfile profile } row) return;
        SelectRow(row, profile);
        _ = CopyPasswordAsync(profile);
        e.Handled = true;
    }

    private void SelectRow(Border row, WifiProfile profile)
    {
        if (_selectedRow is not null) _selectedRow.Background = _rowBackground;
        _selectedRow = row;
        _selected = profile;
        row.Background = _selectedRowBackground;
    }

    private async void OnCopyPasswordClick(object sender, RoutedEventArgs e)
    {
        // With a single saved profile there is nothing to disambiguate.
        var profile = _selected ?? (_profiles.Count == 1 ? _profiles[0] : null);
        if (profile is null)
        {
            await UiKit.InfoAsync(XamlRoot, DialogTitle, "请先选中一行，再复制密码。");
            return;
        }

        await CopyPasswordAsync(profile);
    }

    /// <summary>
    /// Copies the cleartext key. Like the original (whose DataGridView had no copy
    /// handler of its own), this is deliberately silent — no dialog, status text only.
    /// </summary>
    private async Task CopyPasswordAsync(WifiProfile profile)
    {
        if (profile.Password.Length == 0)
        {
            await UiKit.InfoAsync(XamlRoot, DialogTitle, profile.IsOpen
                ? $"【{profile.Ssid}】是开放网络，没有保存密码。"
                : $"【{profile.Ssid}】没有读取到保存的密码。");
            return;
        }

        UiKit.CopyToClipboard(profile.Password);
        StatusText.Text = $"已复制【{profile.Ssid}】的密码";
    }

    // =====================================================================
    // password visibility
    // =====================================================================

    private void OnShowPasswordToggled(object sender, RoutedEventArgs e)
    {
        _showPasswords = ShowPasswordToggle.IsChecked == true;
        StatusText.Text = _showPasswords ? "已显示明文密码" : "已隐藏密码";
        if (_profiles.Count > 0) RebuildRows();
    }

    // =====================================================================
    // export
    // =====================================================================

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            await UiKit.InfoAsync(XamlRoot, DialogTitle, "当前没有可导出的WiFi记录。");
            return;
        }

        try
        {
            var folder = ExportFolder();
            Directory.CreateDirectory(folder);

            // The original wrote a fixed "WiFi密码.csv" without quoting; this port keeps the
            // WiFi密码 prefix but uses the shared timestamped CSV/xlsx exporters. Exporting
            // always writes the real keys, whatever the 显示密码 toggle says.
            var headers = new[] { "WiFi名称", "密码", "认证方式", "加密方式" };
            var rows = _profiles
                .Select(p => new[] { p.Ssid, p.Password, p.Authentication, p.Encryption })
                .ToList();

            var csvPath = Path.Combine(folder, TableExporter.BuildFileName("WiFi密码", ".csv"));
            TableExporter.WriteCsv(csvPath, headers, rows);

            var xlsxPath = Path.Combine(folder, TableExporter.BuildFileName("WiFi密码", ".xlsx"));
            TableExporter.WriteXlsx(xlsxPath, "WiFi密码", headers, rows);

            StatusText.Text = $"已导出 {rows.Count} 条记录：{Path.GetFileName(csvPath)}、{Path.GetFileName(xlsxPath)}";

            var open = await UiKit.ConfirmAsync(XamlRoot, DialogTitle,
                "密码导出成功。是否要打开Excel文档？", "打开文件夹", "关闭");
            if (open) AppServices.Current.Shell.OpenFolder(folder);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(WifiPage), "导出WiFi密码失败: " + ex.Message);
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
}
