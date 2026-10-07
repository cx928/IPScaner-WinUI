using System.Diagnostics;
using IPScaner.Core.Configuration;
using IPScaner.Core.Logging;
using IPScaner.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPScaner.WinUI.Views;

/// <summary>
/// 选项配置 — the WinUI rebuild of the original <c>FormConfig</c> (IPScaner V1.28.2).
/// </summary>
/// <remarks>
/// Behaviour kept from the original:
/// <list type="bullet">
/// <item>Three tabs 外观 / 参数 / 桌面 with the exact captions, tooltips and the
/// original control order (the designer coordinates in <c>FormConfig.Designer.cs</c>).</item>
/// <item>The dialog edits a copy; nothing is persisted until 保存, and 关闭 discards
/// everything (the original only wrote <c>IPScaner.cfg</c> on <c>DialogResult.OK</c>).</item>
/// <item><c>PrePortArray</c> keeps the original's <c>Leave</c> validation, including the
/// full-width comma normalisation and the <c>[x] 输入错误，请输入正确的端口号</c> message.</item>
/// <item>Colours stay signed ARGB integers (WinForms <c>Color.ToArgb()</c>), so an
/// existing <c>IPScaner.cfg</c> keeps working byte for byte.</item>
/// </list>
/// Fixed defects (see docs/re/02-config-memo.md §3.5, §3.7 and the contract §5):
/// <list type="bullet">
/// <item>The original assigned every value straight into a <c>NumericUpDown</c> /
/// <c>ComboBox</c>, so a hand-edited cfg holding an out-of-range number threw
/// <c>ArgumentOutOfRangeException</c> out of the constructor and the options window
/// silently refused to open. Every value is clamped here and the page reports how
/// many values had to be corrected.</item>
/// <item><c>btnSave_Click</c> mutated 11 fields of the live global config before its
/// only validation check; saving is all-or-nothing here because the page edits a clone.</item>
/// <item>A non-empty but invalid port list (<c>abc</c>, <c>99999</c>) used to be saved and
/// later threw inside the scanner thread; the list is validated at save time too.</item>
/// </list>
/// Deviations from the original layout are called out inline in the XAML.
/// </remarks>
public sealed partial class ConfigPage : Page
{
    private const string ConfigTitle = "选项配置";

    // ---------------------------------------------------------------------
    // Ranges the original designer (or WinForms' own defaults) enforced.
    // ---------------------------------------------------------------------
    private const int BtnFontSizeMin = 7, BtnFontSizeMax = 12;
    private const int PingTimeoutMin = 10, PingTimeoutMax = 5000;
    private const int PingCountMin = 1, PingCountMax = 100;
    private const int DoubleClickMin = 100, DoubleClickMax = 500;
    private const int PortTimeoutMin = 10, PortTimeoutMax = 2000;
    private const int OffsetMin = -500, OffsetMax = 5000;
    private const int OpacityMin = 0, OpacityMax = 100;
    private const int LocationMin = 0, LocationMax = 3;
    private const int PortMin = 1, PortMax = 65535;

    /// <summary><c>cmbFontSize.SelectedIndex = BtnFontSize - 8</c> in the original.</summary>
    private const int FontSizeOffset = 8;

    /// <summary>cmbFontSize items, verbatim (小..特大 = BtnFontSize 8..12).</summary>
    private static readonly string[] FontSizeItems = ["小", "中", "大", "超大", "特大"];

    /// <summary>cmbDesktopLocation items; index == DesktopOverlayLocation.</summary>
    private static readonly string[] LocationItems = ["左上角", "右上角", "右下角", "左下角"];

    /// <summary>The WinForms colours the original's colours happened to be, offered as shortcuts.</summary>
    private static readonly (string Name, int Argb)[] KnownColors =
    [
        ("天蓝 SkyBlue", AppConfig.SkyBlueArgb),
        ("亮绿 LimeGreen", AppConfig.LimeGreenArgb),
        ("印度红 IndianRed", AppConfig.IndianRedArgb),
        ("蓝色 Blue", AppConfig.BlueArgb),
        ("黑色 Black", AppConfig.BlackArgb),
        ("黄色 Yellow", AppConfig.YellowArgb),
    ];

    private AppConfig _draft;
    private int _clampedCount;

    public ConfigPage()
    {
        InitializeComponent();

        FontSizeCombo.ItemsSource = FontSizeItems;
        DesktopLocationCombo.ItemsSource = LocationItems;
        // Same order as the enum, so SelectedIndex maps straight onto EventName.
        DoubleEventCombo.ItemsSource = EventNameText.All.Select(e => e.Text).ToArray();

        _draft = AppServices.Current.Config.Clone();
        LoadFromConfig();
    }

    // ---------------------------------------------------------------------
    // load
    // ---------------------------------------------------------------------

    private void LoadFromConfig()
    {
        _clampedCount = 0;
        var c = _draft;

        // ---- 外观 ----------------------------------------------------------
        FontSizeCombo.SelectedIndex = FontSizeIndex(c.BtnFontSize);
        ApplySwatch(DefaultColorSwatch, DefaultColorArgbText, c.DefaultColorArgb);
        ApplySwatch(OkColorSwatch, OkColorArgbText, c.NetworkOKColorArgb);
        ApplySwatch(NgColorSwatch, NgColorArgbText, c.NetworkNGColorArgb);
        ApplySwatch(MemoColorSwatch, MemoColorArgbText, c.MemoColorArgb);
        DoubleEventCombo.SelectedIndex = EventNameIndex(c.DoubleEvent);
        MenuAutoOpenToggle.IsOn = c.MenuAutoOpen;

        // ---- 参数 ----------------------------------------------------------
        QueryHostNameToggle.IsOn = c.QueryHostNameEnabled;
        PingTimeoutBox.Value = ClampCounted(c.PingTimeout, PingTimeoutMin, PingTimeoutMax);
        PingCountBox.Value = ClampCounted(c.PingCount, PingCountMin, PingCountMax);
        DoubleClickBox.Value = ClampCounted(c.DoubleClickTime, DoubleClickMin, DoubleClickMax);
        ArpCheck.IsChecked = c.ARPInsteadPingEnabled;
        PortCheck.IsChecked = c.PortInsteadPingEnabled;
        PortBox.IsEnabled = c.PortInsteadPingEnabled;
        // Raw string on load — the original never normalised here either.
        PortBox.Text = c.PrePortArray ?? string.Empty;
        PortTimeoutBox.Value = ClampCounted(c.PortTimeout, PortTimeoutMin, PortTimeoutMax);
        LogCheck.IsChecked = c.LogEnabled;
        HideMainCheck.IsChecked = c.HideMainEnabled;

        // ---- 桌面 ----------------------------------------------------------
        DesktopCheck.IsChecked = c.DesktopOverlayEnabled;
        DesktopLocationCombo.SelectedIndex = LocationIndex(c.DesktopOverlayLocation);
        OffsetXBox.Value = ClampCounted(c.DesktopOverlayOffsetX, OffsetMin, OffsetMax);
        OffsetYBox.Value = ClampCounted(c.DesktopOverlayOffsetY, OffsetMin, OffsetMax);
        OpacityBox.Value = ClampCounted(c.DesktopOverlayOpacity, OpacityMin, OpacityMax);
        DesktopPreBox.Text = c.DesktopOverlayPre ?? string.Empty;
        ApplySwatch(DesktopForeSwatch, DesktopForeArgbText, c.DesktopForeColorArgb);
        ApplySwatch(DesktopBgSwatch, DesktopBgArgbText, c.DesktopBgColorArgb);

        SaveStatusText.Text = string.Empty;
        UpdateClampNotice();
        UpdateConfigPathText();
    }

    /// <summary>Explains the values that had to be corrected instead of silently rewriting them.</summary>
    private void UpdateClampNotice()
    {
        if (_clampedCount == 0)
        {
            ClampNoticeText.Text = string.Empty;
            ClampNoticeText.Visibility = Visibility.Collapsed;
            return;
        }

        ClampNoticeText.Text =
            $"配置文件中有 {_clampedCount} 项数值超出允许范围，已自动修正到最近的合法值并显示（原版会因此无法打开本窗口）。";
        ClampNoticeText.Visibility = Visibility.Visible;
    }

    private void UpdateConfigPathText()
    {
        var store = AppServices.Current.ConfigStore;
        var state = store.Exists ? "已存在" : "尚未创建，首次保存时生成";
        ConfigPathText.Text = $"配置文件：{store.FilePath}（{state}）";
    }

    // ---------------------------------------------------------------------
    // clamping helpers — never throw
    // ---------------------------------------------------------------------

    private int ClampCounted(int value, int min, int max)
    {
        if (value < min)
        {
            _clampedCount++;
            return min;
        }
        if (value > max)
        {
            _clampedCount++;
            return max;
        }
        return value;
    }

    private int FontSizeIndex(int value)
    {
        var clamped = ClampCounted(value, BtnFontSizeMin, BtnFontSizeMax);
        // The original combo only offered 小..特大 (8..12); a stored 7 left it
        // unselected. Showing the nearest offered size keeps the combo usable
        // (saving then stores 8) instead of presenting an empty selection.
        return Math.Clamp(clamped, FontSizeOffset, FontSizeOffset + FontSizeItems.Length - 1) - FontSizeOffset;
    }

    private int LocationIndex(int value)
    {
        if (value < LocationMin || value > LocationMax)
        {
            _clampedCount++;
            return 2; // 右下角 — the factory default
        }
        return value;
    }

    private int EventNameIndex(EventName value)
    {
        var index = (int)value;
        if (index < 0 || index >= EventNameText.All.Length)
        {
            _clampedCount++;
            return 0; // Ping
        }
        return index;
    }

    /// <summary>Reads a NumberBox defensively: an empty box yields the fallback, never NaN.</summary>
    private static int ReadNumber(NumberBox box, int min, int max, int fallback)
    {
        var value = box.Value;
        var result = double.IsNaN(value) ? fallback : (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return Math.Clamp(result, min, max);
    }

    // ---------------------------------------------------------------------
    // TCP port list (PrePortArray) — FormConfig.txtTcpPorts_Leave
    // ---------------------------------------------------------------------

    /// <summary>
    /// Normalises and validates the port list. Full-width commas become ASCII, empty
    /// tokens are dropped (a trailing comma is fine) and surrounding whitespace is
    /// tolerated by <see cref="int.TryParse(string?, out int)"/> — exactly like the original.
    /// An empty list is not an error here; it only fails at save time when the option is on.
    /// </summary>
    private bool TryNormalizePorts(out string normalized, out string error)
    {
        normalized = (PortBox.Text ?? string.Empty).Trim().Replace('，', ',');
        error = string.Empty;

        var tokens = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return true;

        foreach (var token in tokens)
        {
            if (!int.TryParse(token, out var port) || port < PortMin || port > PortMax)
            {
                error = $"[{token}] 输入错误，请输入正确的端口号";
                return false;
            }
        }
        return true;
    }

    private async void OnPortsLostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryNormalizePorts(out var normalized, out var error))
            {
                await UiKit.InfoAsync(XamlRoot, ConfigTitle, error);

                // The original kept the offending text (it is reported, never
                // reverted) and simply put the caret back into the box.
                DispatcherQueue.TryEnqueue(() =>
                {
                    PortBox.Focus(FocusState.Programmatic);
                    PortBox.SelectionStart = 0;
                    PortBox.SelectionLength = 0;
                });
                return;
            }

            PortBox.Text = normalized;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ConfigPage), "TCP端口校验失败: " + ex.Message);
        }
    }

    private void OnPortCheckChanged(object sender, RoutedEventArgs e)
    {
        var enabled = PortCheck.IsChecked == true;
        PortBox.IsEnabled = enabled;
        // chkPortScanEnabled.CheckedChanged focused the box as soon as it was ticked.
        if (enabled) PortBox.Focus(FocusState.Programmatic);
    }

    // ---------------------------------------------------------------------
    // colours
    // ---------------------------------------------------------------------

    private async void OnPickDefaultColor(object sender, RoutedEventArgs e) =>
        await PickColorAsync("初始色块颜色", () => _draft.DefaultColorArgb, v => _draft.DefaultColorArgb = v,
            DefaultColorSwatch, DefaultColorArgbText);

    private async void OnPickOkColor(object sender, RoutedEventArgs e) =>
        await PickColorAsync("通讯正常颜色", () => _draft.NetworkOKColorArgb, v => _draft.NetworkOKColorArgb = v,
            OkColorSwatch, OkColorArgbText);

    private async void OnPickNgColor(object sender, RoutedEventArgs e) =>
        await PickColorAsync("通讯异常颜色", () => _draft.NetworkNGColorArgb, v => _draft.NetworkNGColorArgb = v,
            NgColorSwatch, NgColorArgbText);

    private async void OnPickMemoColor(object sender, RoutedEventArgs e) =>
        await PickColorAsync("备注色块颜色", () => _draft.MemoColorArgb, v => _draft.MemoColorArgb = v,
            MemoColorSwatch, MemoColorArgbText);

    private async void OnPickDesktopForeColor(object sender, RoutedEventArgs e) =>
        await PickColorAsync("文字颜色", () => _draft.DesktopForeColorArgb, v => _draft.DesktopForeColorArgb = v,
            DesktopForeSwatch, DesktopForeArgbText);

    private async void OnPickDesktopBgColor(object sender, RoutedEventArgs e) =>
        await PickColorAsync("背景色", () => _draft.DesktopBgColorArgb, v => _draft.DesktopBgColorArgb = v,
            DesktopBgSwatch, DesktopBgArgbText);

    /// <summary>
    /// Replaces the WinForms <c>ColorDialog</c> (WinUI has no colour dialog) with a
    /// <see cref="ColorPicker"/> inside a <see cref="ContentDialog"/>, plus the six
    /// WinForms colours the original defaults came from. Alpha is disabled because
    /// <c>ColorDialog</c> always returned A=255.
    /// </summary>
    private async Task PickColorAsync(string title, Func<int> read, Action<int> write, Border swatch, TextBlock valueText)
    {
        try
        {
            var picker = new ColorPicker
            {
                Color = UiKit.ColorFromArgb(read()),
                IsAlphaEnabled = false,
                IsMoreButtonVisible = false,
                ColorSpectrumShape = ColorSpectrumShape.Box,
            };

            var preview = new Border
            {
                Width = 72,
                Height = 36,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = swatch.BorderBrush,
            };
            var valueLine = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var previewRow = new StackPanel { Orientation = Orientation.Horizontal };
            previewRow.Children.Add(preview);
            previewRow.Children.Add(valueLine);

            void Sync(Color color)
            {
                var argb = UiKit.ArgbFromColor(color);
                preview.Background = new SolidColorBrush(color);
                valueLine.Text = $"{ToHex(argb)}   ({argb})";
            }

            Sync(picker.Color);
            picker.ColorChanged += (_, args) => Sync(args.NewColor);

            var shortcuts = new Grid { ColumnSpacing = 6 };
            for (var i = 0; i < KnownColors.Length; i++)
            {
                var (name, argb) = KnownColors[i];
                shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // A Button (not a bare Border) so the shortcut is focusable and
                // reachable by screen readers / UI Automation.
                var cell = new Button
                {
                    Height = 26,
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    Content = new Border
                    {
                        CornerRadius = new CornerRadius(3),
                        BorderThickness = new Thickness(1),
                        BorderBrush = swatch.BorderBrush,
                        Background = UiKit.BrushFromArgb(argb),
                    },
                    Tag = argb,
                };
                AutomationProperties.SetName(cell, $"{name} {argb}");
                ToolTipService.SetToolTip(cell, $"{name} = {argb}");
                cell.Click += (_, _) => picker.Color = UiKit.ColorFromArgb((int)cell.Tag);
                Grid.SetColumn(cell, i);
                shortcuts.Children.Add(cell);
            }

            var panel = new StackPanel { Spacing = 12, Width = 360 };
            panel.Children.Add(previewRow);
            panel.Children.Add(new TextBlock { Text = "常用颜色", Style = (Style)Application.Current.Resources["HintTextStyle"] });
            panel.Children.Add(shortcuts);
            panel.Children.Add(picker);

            var result = await UiKit.ShowContentAsync(XamlRoot, title, panel, "确定", null, "取消");
            if (result != ContentDialogResult.Primary) return; // 取消 keeps the previous colour

            write(UiKit.ArgbFromColor(picker.Color));
            ApplySwatch(swatch, valueText, read());
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ConfigPage), "选择颜色失败: " + ex.Message);
        }
    }

    private static void ApplySwatch(Border swatch, TextBlock valueText, int argb)
    {
        swatch.Background = UiKit.BrushFromArgb(argb);
        valueText.Text = $"{ToHex(argb)}   ({argb})";
    }

    /// <summary>#AARRGGBB, matching the signed ARGB integer the cfg stores.</summary>
    private static string ToHex(int argb) => "#" + unchecked((uint)argb).ToString("X8");

    // ---------------------------------------------------------------------
    // 保存 / 关闭
    // ---------------------------------------------------------------------

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryNormalizePorts(out var ports, out var portError))
            {
                await UiKit.InfoAsync(XamlRoot, ConfigTitle, portError);
                return;
            }

            var portInsteadPing = PortCheck.IsChecked == true;
            if (portInsteadPing && ports.Length == 0)
            {
                // btnSave_Click step 12: 请输入要侦测的TCP端口, the window stayed open
                // and nothing was written to disk.
                await UiKit.InfoAsync(XamlRoot, ConfigTitle, "请输入要侦测的TCP端口");
                PortBox.Focus(FocusState.Programmatic);
                return;
            }

            var previousFontSize = AppServices.Current.Config.BtnFontSize;
            var cfg = _draft;

            cfg.QueryHostNameEnabled = QueryHostNameToggle.IsOn;
            cfg.PingTimeout = ReadNumber(PingTimeoutBox, PingTimeoutMin, PingTimeoutMax, cfg.PingTimeout);
            cfg.PingCount = ReadNumber(PingCountBox, PingCountMin, PingCountMax, cfg.PingCount);
            cfg.DoubleClickTime = ReadNumber(DoubleClickBox, DoubleClickMin, DoubleClickMax, cfg.DoubleClickTime);
            cfg.BtnFontSize = Math.Clamp(FontSizeCombo.SelectedIndex + FontSizeOffset, BtnFontSizeMin, BtnFontSizeMax);
            cfg.MenuAutoOpen = MenuAutoOpenToggle.IsOn;
            cfg.DoubleEvent = EventNameText.All[Math.Max(0, DoubleEventCombo.SelectedIndex)].Value;
            // DefaultColorArgb / NetworkOKColorArgb / NetworkNGColorArgb / MemoColorArgb /
            // DesktopForeColorArgb / DesktopBgColorArgb were written by the picker.
            cfg.PortInsteadPingEnabled = portInsteadPing;
            cfg.ARPInsteadPingEnabled = ArpCheck.IsChecked == true;
            cfg.PrePortArray = ports;
            cfg.PortTimeout = ReadNumber(PortTimeoutBox, PortTimeoutMin, PortTimeoutMax, cfg.PortTimeout);
            cfg.LogEnabled = LogCheck.IsChecked == true;
            cfg.HideMainEnabled = HideMainCheck.IsChecked == true;
            cfg.DesktopOverlayEnabled = DesktopCheck.IsChecked == true;
            cfg.DesktopOverlayLocation = Math.Clamp(DesktopLocationCombo.SelectedIndex, LocationMin, LocationMax);
            cfg.DesktopOverlayOffsetX = ReadNumber(OffsetXBox, OffsetMin, OffsetMax, cfg.DesktopOverlayOffsetX);
            cfg.DesktopOverlayOffsetY = ReadNumber(OffsetYBox, OffsetMin, OffsetMax, cfg.DesktopOverlayOffsetY);
            cfg.DesktopOverlayOpacity = ReadNumber(OpacityBox, OpacityMin, OpacityMax, cfg.DesktopOverlayOpacity);
            cfg.DesktopOverlayPre = DesktopPreBox.Text ?? string.Empty;

            AppServices.Current.ApplyConfig(cfg);
            App.MainWindow?.SetStatus("选项配置已保存");

            SaveStatusText.Text = $"已保存（{DateTime.Now:HH:mm:ss}）";
            UpdateConfigPathText();

            // Keep editing a detached copy, like the original dialog did.
            _draft = cfg.Clone();

            if (cfg.BtnFontSize != previousFontSize)
            {
                // FormMain.UpdateControlByCfgInfo asked the same question right after
                // a save that changed the block font size.
                var restart = await UiKit.ConfirmAsync(XamlRoot, ConfigTitle, "字号已变更，是否立即重启程序？", "是", "否");
                if (restart) RestartApplication();
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ConfigPage), "保存配置失败: " + ex.Message);
            await UiKit.InfoAsync(XamlRoot, ConfigTitle, "保存配置失败：" + ex.Message);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // 关闭 == the original's btnClose / Esc: DialogResult.Cancel, i.e. every edit
        // is dropped. Because the page edits a clone, discarding costs nothing.
        _draft = AppServices.Current.Config.Clone();
        LoadFromConfig();
        App.MainWindow?.SetStatus("选项配置已关闭，未保存的修改已放弃");
        App.MainWindow?.NavigateTo("scan");
    }

    private static void RestartApplication()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ConfigPage), "重启程序失败: " + ex.Message);
        }
    }
}
