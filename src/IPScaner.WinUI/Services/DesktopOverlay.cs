using System.Runtime.InteropServices;
using System.Text;
using IPScaner.Core.Configuration;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace IPScaner.WinUI.Services;

/// <summary>
/// 桌面显示本机IP — the WinUI port of the original <c>DesktopOverlayForm</c>.
/// </summary>
/// <remarks>
/// Faithful to the reverse-engineered behaviour:
/// <list type="bullet">
/// <item>borderless, chrome-less window sized to the text plus the original's
/// 18&nbsp;px label padding and 40&nbsp;px slack (<c>Width = lbl.Width + 40</c>);</item>
/// <item>pinned to one of four corners of the <b>primary</b> display using the full
/// monitor bounds (<see cref="DisplayArea.Primary"/> <c>OuterBounds</c> = WinForms
/// <c>Screen.PrimaryScreen.Bounds</c> — the taskbar is <i>not</i> excluded and a
/// multi-monitor desktop still uses the primary monitor);</item>
/// <item>made click-through and invisible to the shell with
/// <c>WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW</c> and per-window
/// alpha from <see cref="AppConfig.DesktopOverlayOpacity"/>.</item>
/// </list>
/// <para>
/// As in the original there is <b>no</b> <c>SetParent(Progman/WorkerW)</c> and
/// <b>no</b> always-on-top, so the badge floats in the normal z-order and can be
/// covered by other windows. The documented quirks that were fixed here are noted
/// on the individual members.
/// </para>
/// <para>
/// Every member must be used from the UI thread; <see cref="Update"/> is cheap and
/// safe to call often (it only touches the label/resize/move when something
/// actually changed).
/// </para>
/// </remarks>
public sealed class DesktopOverlay : IDisposable
{
    /// <summary>
    /// Never displayed (<c>HasTitleBar = false</c>, <c>IsShownInSwitchers = false</c>);
    /// it exists so the window is identifiable in diagnostics and screenshot tools.
    /// The original form kept the designer name <c>DesktopOverlayForm</c> as its text.
    /// </summary>
    private const string OverlayTitle = "桌面显示本机IP";

    // ---- window styles (GWL_EXSTYLE) ---------------------------------------
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const uint LWA_ALPHA = 0x00000002;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    /// <summary>The original label's <c>Padding = new Padding(18)</c>.</summary>
    private const double LabelPadding = 18;

    /// <summary>The original's <c>Width = lbl.Width + 40; Height = lbl.Height + 40</c>.</summary>
    private const double ExtraSize = 40;

    /// <summary>宋体 10 pt, expressed in the DIPs WinUI's <c>FontSize</c> uses.</summary>
    private const double LabelFontSize = 10 * 96.0 / 72.0;

    private Window? _window;
    private AppWindow? _appWindow;
    private TextBlock? _label;
    private Grid? _root;
    private IntPtr _hwnd;

    private bool _visible;
    private bool _disposed;

    private string _lastText = string.Empty;
    private int _lastForeColorArgb;
    private int _lastBgColorArgb;
    private int _lastOpacityPercent = -1;
    private int _lastDpi;
    private bool _sizeDirty = true;
    private SizeInt32 _lastSize;

    /// <summary>
    /// Sentinel rather than (0,0): the top-left corner is a legitimate target, and
    /// treating it as "not moved yet" silently skipped the very first Move.
    /// </summary>
    private PointInt32 _lastPosition = new(int.MinValue, int.MinValue);

    private DateTime _lastAdapterRefresh = DateTime.MinValue;

    /// <summary>True while the overlay window is on screen.</summary>
    public bool IsVisible => _visible;

    /// <summary>Creates the window on first use, then updates and shows it.</summary>
    public void Show(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_disposed) return;

        try
        {
            EnsureWindow();
            ApplyExtendedStyles(); // re-assert: the styles belong to the HWND, not to the content
            Apply(config, show: true);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(DesktopOverlay), "显示桌面显示本机IP失败: " + ex.Message);
        }
    }

    /// <summary>
    /// Re-reads the address, colours, opacity and position. A no-op while hidden
    /// (the next <see cref="Show"/> picks the current configuration up anyway).
    /// </summary>
    public void Update(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_disposed || !_visible || _window is null) return;

        try
        {
            Apply(config, show: false);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(DesktopOverlay), "刷新桌面显示本机IP失败: " + ex.Message);
        }
    }

    /// <summary>Hides the window without destroying it, so showing it again is instant.</summary>
    public void Hide()
    {
        if (_window is null || _hwnd == IntPtr.Zero)
        {
            _visible = false;
            return;
        }

        try
        {
            ShowWindow(_hwnd, SW_HIDE);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(DesktopOverlay), "隐藏桌面显示本机IP失败: " + ex.Message);
        }

        _visible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_window is not null)
            {
                if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
                _window.Close();
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(DesktopOverlay), "关闭桌面显示本机IP失败: " + ex.Message);
        }
        finally
        {
            _window = null;
            _appWindow = null;
            _label = null;
            _root = null;
            _hwnd = IntPtr.Zero;
            _visible = false;
        }
    }

    // ---- window plumbing ---------------------------------------------------

    private void EnsureWindow()
    {
        if (_window is not null) return;

        var label = new TextBlock
        {
            FontFamily = new FontFamily("SimSun"), // 宋体
            FontSize = LabelFontSize,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, // Label.TextAlign = MiddleLeft
            Foreground = new SolidColorBrush(UiKit.ColorFromArgb(AppConfig.BlackArgb)),
        };

        // The original label was AutoSize + Dock=Fill, so its background covered the
        // whole client area: the badge is one solid card with 18 px of padding.
        var root = new Grid
        {
            Padding = new Thickness(LabelPadding),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        root.Children.Add(label);

        var window = new Window { Title = OverlayTitle, Content = root };
        var hwnd = WindowNative.GetWindowHandle(window);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

        // No title bar, no border, not resizable/minimizable/maximizable — the
        // WinUI equivalent of FormBorderStyle.None + ShowIcon = false.
        // (WinAppSDK has no public OverlappedPresenter constructor: Create() plus
        // SetBorderAndTitleBar(false, false) is the supported way to express
        // HasBorder/HasTitleBar, which are read-only properties.)
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = false; // the original had no TopMost
        presenter.SetBorderAndTitleBar(false, false);
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = false; // + WS_EX_TOOLWINDOW: never in Alt+Tab/taskbar

        _window = window;
        _appWindow = appWindow;
        _label = label;
        _root = root;
        _hwnd = hwnd;
        _lastDpi = 0;
        _sizeDirty = true;
    }

    /// <summary>
    /// <c>WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW</c>, exactly what the
    /// original OR-ed into <c>GWL_EXSTYLE</c> (layered = alpha, transparent =
    /// click-through, tool window = out of the shell's window lists).
    /// </summary>
    private void ApplyExtendedStyles()
    {
        if (_hwnd == IntPtr.Zero) return;

        var style = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    private void Apply(AppConfig config, bool show)
    {
        var label = _label;
        var root = _root;
        var appWindow = _appWindow;
        if (label is null || root is null || appWindow is null || _hwnd == IntPtr.Zero) return;

        // 1. text ------------------------------------------------------------
        var text = Normalize(BuildText(config));
        if (!string.Equals(text, _lastText, StringComparison.Ordinal))
        {
            _lastText = text;
            label.Text = text;
            _sizeDirty = true;
        }

        // 2. colours ---------------------------------------------------------
        if (_lastBgColorArgb != config.DesktopBgColorArgb || root.Background is not SolidColorBrush)
        {
            _lastBgColorArgb = config.DesktopBgColorArgb;
            root.Background = new SolidColorBrush(UiKit.ColorFromArgb(_lastBgColorArgb));
        }

        if (_lastForeColorArgb != config.DesktopForeColorArgb || label.Foreground is not SolidColorBrush)
        {
            _lastForeColorArgb = config.DesktopForeColorArgb;
            label.Foreground = new SolidColorBrush(UiKit.ColorFromArgb(_lastForeColorArgb));
        }

        // 3. size ------------------------------------------------------------
        var dpi = GetDpiForWindow(_hwnd);
        if (dpi <= 0) dpi = 96;
        if (_sizeDirty || dpi != _lastDpi) ResizeToContent(dpi);

        // 4. opacity ---------------------------------------------------------
        var opacity = Math.Clamp(config.DesktopOverlayOpacity, 0, 100);
        if (opacity != _lastOpacityPercent)
        {
            // Opacity = DesktopOverlayOpacity / 100.0, applied per window.
            SetLayeredWindowAttributes(_hwnd, 0, (byte)(opacity * 255 / 100), LWA_ALPHA);
            _lastOpacityPercent = opacity;
        }

        // 5. position --------------------------------------------------------
        MoveToConfiguredCorner(config);

        // 6. show ------------------------------------------------------------
        if (show && !_visible) ShowNoActivate();
    }

    private void ShowNoActivate()
    {
        // ShowWindow(SW_SHOWNOACTIVATE) rather than Window.Activate(): the badge is
        // click-through decoration and must not steal activation from whatever the
        // user is working in (WinForms' Show() activated only as a side effect).
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        _visible = true;
    }

    // ---- content -----------------------------------------------------------

    /// <summary>
    /// Mirrors <c>FormMain.DislayDesktopOverlay</c>: the configured prefix, the
    /// active IPv4 address(es), then 名称 / MAC / 掩码 / 网关 / DHCP / DNS for the
    /// first active adapter. Lines without a value are skipped, which also covers
    /// the original's all-or-nothing emission when several NICs are up (its
    /// <c>FirstOrDefault(r =&gt; r.IP == "ip1; ip2")</c> never matched, so a
    /// multi-homed machine lost every detail line).
    /// </summary>
    private string BuildText(AppConfig config)
    {
        var adapters = ReadAdapters();
        var active = adapters
            .Where(a => a.IsUp && !string.IsNullOrWhiteSpace(a.IP))
            .ToList();

        var text = new StringBuilder(config.DesktopOverlayPre ?? string.Empty)
            .Append(string.Join("; ", active.Select(a => a.IP)));

        var primary = active.FirstOrDefault();
        if (primary is not null)
        {
            AppendField(text, "名称", primary.Name);
            AppendField(text, "MAC", primary.Mac);
            AppendField(text, "掩码", primary.SubnetMask);
            AppendField(text, "网关", primary.Gateway);
            AppendField(text, "DHCP", primary.IsDhcpEnabled ? "True" : "False"); // raw .NET bool, as the original printed it
            AppendField(text, "DNS", string.Join("; ", primary.DnsServers));
        }

        return text.ToString();
    }

    private static void AppendField(StringBuilder text, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        text.Append(Environment.NewLine).Append(label).Append('：').Append(value.Trim());
    }

    /// <summary>CRLF is normalised so the TextBlock renders one break per line.</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private IReadOnlyList<AdapterInfo> ReadAdapters()
    {
        var services = AppServices.Current;
        var list = services.AdapterList;

        // AdapterList is filled by a background task at startup, so the first paint
        // can win that race (the original read a list already filled by
        // FormMain_Load). Re-enumerate, throttled so a frequent Update() cannot turn
        // into a WMI storm on a machine with no usable adapter.
        if (list.Count == 0 && DateTime.UtcNow - _lastAdapterRefresh > TimeSpan.FromSeconds(10))
        {
            _lastAdapterRefresh = DateTime.UtcNow;
            list = services.RefreshAdapters();
        }

        return list;
    }

    // ---- geometry ----------------------------------------------------------

    private void ResizeToContent(int dpi)
    {
        var label = _label;
        var appWindow = _appWindow;
        if (label is null || appWindow is null) return;

        // Unconstrained measure = Label.AutoSize.
        label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = label.DesiredSize;
        var dipWidth = desired.Width;
        var dipHeight = desired.Height;

        if (dipWidth <= 0 || dipHeight <= 0)
        {
            // Never hand the window a zero size if the text stack cannot measure.
            var lines = _lastText.Split('\n');
            dipWidth = lines.Max(l => l.Length) * LabelFontSize * 0.95;
            dipHeight = lines.Length * LabelFontSize * 1.35;
        }

        var scale = dpi / 96.0;
        var width = Math.Max(1, (int)Math.Ceiling((dipWidth + (LabelPadding * 2) + ExtraSize) * scale));
        var height = Math.Max(1, (int)Math.Ceiling((dipHeight + (LabelPadding * 2) + ExtraSize) * scale));

        _lastSize = new SizeInt32(width, height);
        _lastDpi = dpi;
        _sizeDirty = false;

        appWindow.Resize(_lastSize);
    }

    /// <summary>
    /// The original's four-corner switch, resolved against the primary display's
    /// full bounds. The only change is the clamp: offsets are allowed to be
    /// negative or huge, and the original happily pushed the badge off the screen
    /// (and fell through to (0,0) for a location outside 0..3).
    /// </summary>
    private void MoveToConfiguredCorner(AppConfig config)
    {
        var appWindow = _appWindow;
        if (appWindow is null) return;

        var bounds = DisplayArea.Primary.OuterBounds;
        var width = _lastSize.Width;
        var height = _lastSize.Height;
        var offsetX = config.DesktopOverlayOffsetX;
        var offsetY = config.DesktopOverlayOffsetY;

        int x, y;
        switch (config.DesktopOverlayLocation)
        {
            case 0: // 左上角
                x = bounds.X + offsetX;
                y = bounds.Y + offsetY;
                break;
            case 1: // 右上角
                x = bounds.X + bounds.Width - width - offsetX;
                y = bounds.Y + offsetY;
                break;
            case 3: // 左下角
                x = bounds.X + offsetX;
                y = bounds.Y + bounds.Height - height - offsetY;
                break;
            default: // 2 = 右下角 (the config default, also used for out-of-range values)
                x = bounds.X + bounds.Width - width - offsetX;
                y = bounds.Y + bounds.Height - height - offsetY;
                break;
        }

        var position = new PointInt32(
            KeepOnScreen(x, bounds.X, bounds.Width, width),
            KeepOnScreen(y, bounds.Y, bounds.Height, height));

        if (position.X != _lastPosition.X || position.Y != _lastPosition.Y)
        {
            appWindow.Move(position);
            _lastPosition = position;
        }
    }

    private static int KeepOnScreen(int value, int origin, int extent, int size)
    {
        if (size >= extent) return origin; // wider/taller than the monitor: pin to its origin
        return Math.Clamp(value, origin, origin + extent - size);
    }

    // ---- interop -----------------------------------------------------------

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    // x64/ARM64 only (the project's Platforms): the 32-bit user32 exports the
    // GetWindowLong/SetWindowLong pair instead.
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);
}
