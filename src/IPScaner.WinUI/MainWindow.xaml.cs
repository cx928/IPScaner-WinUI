using IPScaner.WinUI.Services;
using IPScaner.WinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace IPScaner.WinUI;

/// <summary>
/// Shell window: navigation pane, custom title bar and the status strip that
/// replaces the original's <c>StatusStrip</c> (status text, rotating tip, clock).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _clockTimer = new();
    private readonly DispatcherTimer _tipTimer = new();
    private readonly DispatcherTimer _overlayTimer = new();
    private readonly Queue<string> _tips = new();
    private TrayIcon? _trayIcon;
    private DesktopOverlay? _desktopOverlay;
    private IntPtr _hwnd;

    /// <summary>
    /// Rotating hints, carried over verbatim from the original
    /// <c>FormMain.InitTipList</c> so long-time users see the same guidance.
    /// </summary>
    private static readonly string[] TipList =
    [
        "Tip:右击某一个IP小色块，可调出ping命令窗口",
        "Tip:右击某一个IP小色块，可复制IP、计算机名、MAC地址",
        "注意:开启【主机名查询】功能,将会降低查询效率",
        "Tip:点击【不通】图例,可复制所有通讯【异常】的IP地址",
        "Tip:点击【正常】图例,可复制所有通讯【正常】的IP地址",
        "Tip:点击【IP小色块】,可快速复制IP地址",
        "Tip:点击顶部IP下拉框,可切换IP扫描段",
        "Tip:想扫描A类的多段IP,可以使用【IP批量扫描】",
        "Tip:【修改本地IP】支持历史IP快速切换",
        "Tip:【端口扫描】可以查看计算机开放了哪些端口",
        "Tip:鼠标悬停在【IP小色块】上,会显示计算机名称和MAC地址",
        "Tip:本工具只支持查询 当前电脑所在网段下的所有MAC地址",
        "Tip:小色块的双击功能支持自定义",
        "Tip:按住 Ctrl 点击小色块，可直接打开详情",
    ];

    public MainWindow()
    {
        InitializeComponent();

        Title = "局域网IP扫描工具 IPScaner";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ConfigureWindow();
        InitializeStatusBar();
        InitializeTrayAndOverlay();
        UpdateElevationBadge();

        // Land on the scan page, like the original did.
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
    }

    /// <summary>Set the left-hand status text (the original's statusTip).</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Navigate to a page by tag, used by pages that cross-link.</summary>
    public bool NavigateTo(string tag)
    {
        foreach (var item in Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag)
            {
                Nav.SelectedItem = item;
                return true;
            }
        }
        return false;
    }

    private void ConfigureWindow()
    {
        try
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);

            // A /24 grid needs room; the original grew itself until the flow panel
            // stopped scrolling, which usually landed around this size.
            appWindow.Resize(new SizeInt32(1180, 820));
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 900;
                presenter.PreferredMinimumHeight = 620;
            }
        }
        catch (Exception ex)
        {
            Core.Logging.AppLog.Instance.Log(nameof(MainWindow), "配置窗口失败: " + ex.Message);
        }
    }

    private void InitializeStatusBar()
    {
        _tips.Clear();
        foreach (var tip in TipList) _tips.Enqueue(tip);
        TipText.Text = TipList[Random.Shared.Next(TipList.Length)];

        ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clockTimer.Start();

        // The original rotated a tip every 10 seconds.
        _tipTimer.Interval = TimeSpan.FromSeconds(10);
        _tipTimer.Tick += (_, _) =>
        {
            if (_tips.Count == 0) foreach (var t in TipList) _tips.Enqueue(t);
            TipText.Text = _tips.Dequeue();
        };
        _tipTimer.Start();

        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            _tipTimer.Stop();
            _overlayTimer.Stop();
            _desktopOverlay?.Dispose();
            _trayIcon?.Dispose();
            App.ShutdownLogging();
        };
    }

    /// <summary>
    /// Creates the tray icon and the desktop overlay, and keeps the overlay in
    /// step with configuration changes.
    /// </summary>
    /// <remarks>
    /// The overlay window is created lazily by <see cref="DesktopOverlay.Show"/>,
    /// so with the factory default (<c>DesktopOverlayEnabled = false</c>) nothing
    /// is displayed at all — it only appears once the user enables 桌面显示本机IP.
    /// The original had the same shape: a NotifyIcon that was always present, and
    /// an overlay form created on demand.
    /// </remarks>
    private void InitializeTrayAndOverlay()
    {
        try
        {
            _trayIcon = new TrayIcon(
                "局域网IP扫描工具",
                onActivate: OnTrayActivate,
                onExit: OnTrayExit);
            _trayIcon.Show();
            _trayIcon.AttachMainWindow(this, () => AppServices.Current.Config.HideMainEnabled);
        }
        catch (Exception ex)
        {
            Core.Logging.AppLog.Instance.Log(nameof(MainWindow), "初始化托盘图标失败: " + ex.Message);
        }

        try
        {
            _desktopOverlay = new DesktopOverlay();
            ApplyDesktopOverlay();
            AppServices.Current.ConfigChanged += (_, _) => ApplyDesktopOverlay();

            // The badge shows the machine's *live* address, and 修改本地IP can change
            // it without any configuration change, so poll gently while it is visible.
            // Update() is a no-op when hidden and only touches the window when the
            // text, colour or position actually changed.
            _overlayTimer.Interval = TimeSpan.FromSeconds(10);
            _overlayTimer.Tick += (_, _) =>
            {
                if (_desktopOverlay?.IsVisible == true) _desktopOverlay.Update(AppServices.Current.Config);
            };
            _overlayTimer.Start();
        }
        catch (Exception ex)
        {
            Core.Logging.AppLog.Instance.Log(nameof(MainWindow), "初始化桌面叠加窗失败: " + ex.Message);
        }
    }

    /// <summary>Shows, refreshes or hides the desktop overlay to match the config.</summary>
    private void ApplyDesktopOverlay()
    {
        if (_desktopOverlay is null) return;
        try
        {
            var config = AppServices.Current.Config;
            if (config.DesktopOverlayEnabled) _desktopOverlay.Show(config);
            else _desktopOverlay.Hide();
        }
        catch (Exception ex)
        {
            Core.Logging.AppLog.Instance.Log(nameof(MainWindow), "更新桌面叠加窗失败: " + ex.Message);
        }
    }

    /// <summary>
    /// Tray icon activated (double-click). Uses the tray's own restore path rather
    /// than <see cref="UiKit.ActivateMainWindow"/> because 最小化时隐藏到托盘
    /// removes the window outright, and that needs a SW_SHOW before Activate.
    /// </summary>
    private void OnTrayActivate() => _trayIcon?.RestoreMainWindow();

    /// <summary>
    /// Tray 退出. Restores the window first so the prompt is actually on screen,
    /// then asks — the original confirmed with 是否确认退出程序？ and cancelling
    /// left the app running.
    /// </summary>
    private async void OnTrayExit()
    {
        _trayIcon?.RestoreMainWindow();

        var root = UiKit.MainXamlRoot;
        if (root is not null && !await UiKit.ConfirmAsync(root, "退出", "是否确认退出程序？")) return;

        Close();
    }

    private void UpdateElevationBadge()
    {
        if (AppServices.Current.IsElevated)
        {
            ElevationText.Text = "管理员";
            ElevationBadge.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBackgroundBrush"];
            ElevationBadge.Visibility = Visibility.Visible;
            ElevateButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ElevationText.Text = "未提权";
            ElevationBadge.Visibility = Visibility.Visible;
            ElevateButton.Visibility = Visibility.Visible;
        }
    }

    private void OnRestartElevated(object sender, RoutedEventArgs e) => UiKit.RestartElevated();

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
        var pageType = ResolvePage(tag);

        if (ContentFrame.Content?.GetType() != pageType)
        {
            ContentFrame.Navigate(pageType, tag, new EntranceNavigationTransitionInfo());
        }

        SetStatus(tag switch
        {
            "scan" => "IP段扫描",
            "batch" => "IP批量扫描",
            "portscan" => "目标端口扫描",
            "localport" => "本机端口占用",
            "localip" => "修改本地IP",
            "wifi" => "WiFi密码查看",
            "calc" => "IP地址计算器",
            "memo" => "备注管理",
            "config" => "选项配置",
            "about" => "关于",
            _ => "就绪",
        });
    }

    /// <summary>
    /// Maps a navigation tag to its page type. Tags whose page has not been built
    /// yet resolve to <see cref="PlaceholderPage"/> so the shell always runs.
    /// </summary>
    private static Type ResolvePage(string tag) => tag switch
    {
        "scan" => typeof(ScanPage),
        "about" => typeof(AboutPage),
        "calc" => typeof(CalculatorPage),
        "batch" => typeof(BatchScanPage),
        "portscan" => typeof(PortScanPage),
        "localport" => typeof(LocalPortPage),
        "localip" => typeof(LocalIpPage),
        "wifi" => typeof(WifiPage),
        "memo" => typeof(MemoPage),
        "config" => typeof(ConfigPage),
        _ => typeof(PlaceholderPage),
    };
}
