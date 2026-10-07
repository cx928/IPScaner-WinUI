using System.Runtime.InteropServices;
using IPScaner.Core.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace IPScaner.WinUI.Services;

/// <summary>
/// 系统托盘图标 — the WinUI replacement for the original's WinForms
/// <c>NotifyIcon</c> (<c>notifyIcon1</c>), implemented directly on
/// <c>Shell_NotifyIcon</c> because WinUI 3 has no tray API.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>a private, never-shown top-level window receives the shell callback
/// (<c>WM_APP + 1</c>) on the UI thread;</item>
/// <item>double-click restores the main window, right-click opens the
/// 显示主界面 / 隐藏 / 关于 / 退出 menu that the original's
/// <c>contextMenuNotify</c> provided;</item>
/// <item><see cref="AttachMainWindow"/> reproduces 最小化时隐藏到托盘
/// (<see cref="Core.Configuration.AppConfig.HideMainEnabled"/>): minimising hides
/// the window from the taskbar with <c>SW_HIDE</c> instead of letting it sit in
/// the taskbar.</item>
/// </list>
/// <para>
/// The callback window is created as a hidden top-level window rather than a
/// message-only one (<c>HWND_MESSAGE</c>). Two measured behaviours forced that
/// choice: (1) the <c>TrackPopupMenu</c> contract requires the owning thread to be
/// foreground so the menu closes on Esc / an outside click, and a message-only
/// window can never become foreground — the menu stayed on screen until an item
/// was clicked; (2) <c>TaskbarCreated</c> is broadcast to top-level windows only,
/// so a message-only window would never learn that Explorer restarted and the icon
/// would disappear for good. This is the same shape WinForms' <c>NotifyIcon</c>
/// uses. The window is never shown, so it has no taskbar button and no Alt+Tab
/// entry (also excluded by <c>WS_EX_TOOLWINDOW</c>).
/// </para>
/// <para>
/// The original's tooltip was the literal designer string <c>notifyIcon1</c>; a real
/// product name is used instead, as the reverse-engineering notes recommend.
/// Its 1-second balloon on every minimise is deliberately not reproduced — the
/// shell has deprecated balloon tips and the config surface has no switch for it.
/// </para>
/// <para>All members must be used from the UI thread.</para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const string WindowClassName = "IPScanerTrayIconMessageWindow";

    private const uint TrayIconId = 1;

    /// <summary><c>WM_APP + 1</c>, the callback message the shell sends the icon's window.</summary>
    private const uint CallbackMessage = 0x8000 + 1;

    // ---- messages ----------------------------------------------------------
    private const uint WM_NULL = 0x0000;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    // ---- Shell_NotifyIcon --------------------------------------------------
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    // ---- icons -------------------------------------------------------------
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    /// <summary><c>IDI_APPLICATION</c> (<c>MAKEINTRESOURCE(32512)</c>).</summary>
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    // ---- menus -------------------------------------------------------------
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RETURNCMD = 0x00000100;
    private const uint TPM_RIGHTBUTTON = 0x00000002;

    private const int CmdShowMain = 1;
    private const int CmdHideMain = 2;
    private const int CmdAbout = 3;
    private const int CmdExit = 4;

    // ---- window commands ---------------------------------------------------
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private const uint ERROR_CLASS_ALREADY_EXISTS = 1410;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly string _tooltip;
    private readonly Action _onActivate;
    private readonly Action _onExit;
    private readonly DispatcherQueue? _dispatcher;
    private readonly uint _taskbarCreatedMessage;

    /// <summary>
    /// Held as a field on purpose: the native window class stores a raw function
    /// pointer, so a collected delegate would crash the shell callback.
    /// </summary>
    private readonly WndProcDelegate _wndProc;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _ownsIcon;
    private bool _added;
    private bool _disposed;

    private Window? _mainWindow;
    private AppWindow? _appWindow;
    private Func<bool>? _hideToTrayEnabled;
    private bool _restoring;

    /// <param name="tooltip">Tray tooltip (max 127 characters), shown on hover.</param>
    /// <param name="onActivate">Invoked for a double-click and for 显示主界面.</param>
    /// <param name="onExit">Invoked for 退出; the handler owns the confirmation prompt.</param>
    public TrayIcon(string tooltip, Action onActivate, Action onExit)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        ArgumentNullException.ThrowIfNull(onExit);

        var text = string.IsNullOrWhiteSpace(tooltip) ? "局域网IP扫描工具" : tooltip.Trim();
        _tooltip = text.Length > 127 ? text[..127] : text; // szTip is 128 chars incl. the terminator
        _onActivate = onActivate;
        _onExit = onExit;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _wndProc = WindowProc;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

        CreateMessageWindow();
        _icon = LoadTrayIcon();
    }

    /// <summary>Adds the icon to the notification area (the original was visible from startup).</summary>
    public void Show()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        AddIcon();
    }

    /// <summary>Removes the icon from the notification area.</summary>
    public void Hide()
    {
        if (!_added || _hwnd == IntPtr.Zero) return;

        try
        {
            var data = CreateData();
            if (!Shell_NotifyIconW(NIM_DELETE, ref data))
                AppLog.Instance.Log(nameof(TrayIcon), $"移除托盘图标失败 (Win32={Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(TrayIcon), "移除托盘图标异常: " + ex.Message);
        }
        finally
        {
            _added = false;
        }
    }

    /// <summary>
    /// Hooks the main window so that minimising it hides the window from the
    /// taskbar while 最小化时隐藏到托盘 is enabled.
    /// </summary>
    /// <param name="window">The main window.</param>
    /// <param name="hideToTrayEnabled">Reads the live <c>HideMainEnabled</c> flag.</param>
    public void AttachMainWindow(Window window, Func<bool> hideToTrayEnabled)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(hideToTrayEnabled);

        _mainWindow = window;
        _hideToTrayEnabled = hideToTrayEnabled;

        var hwnd = WindowNative.GetWindowHandle(window);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.Changed += OnAppWindowChanged;
    }

    /// <summary>
    /// Brings the main window back after it was minimised to the tray:
    /// <c>SW_SHOW</c> (the window was hidden outright) followed by the shared
    /// activate/restore/foreground helper.
    /// </summary>
    public void RestoreMainWindow()
    {
        var window = _mainWindow ?? App.MainWindow;
        if (window is null) return;

        _restoring = true;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            ShowWindow(hwnd, SW_SHOW);
            UiKit.ActivateMainWindow();
        }
        finally
        {
            // Cleared once the restore's window messages have been pumped, so the
            // transitional "still minimized" notifications cannot re-hide the window.
            if (_dispatcher is not null) _dispatcher.TryEnqueue(() => _restoring = false);
            else _restoring = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Hide(); // Shell_NotifyIcon(NIM_DELETE) before the window it belongs to goes away

        if (_appWindow is not null)
        {
            try { _appWindow.Changed -= OnAppWindowChanged; } catch { /* window already gone */ }
            _appWindow = null;
        }
        _mainWindow = null;
        _hideToTrayEnabled = null;

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _ownsIcon = false;
    }

    // ---- notifications -----------------------------------------------------

    private void AddIcon()
    {
        if (_added) return;

        try
        {
            var data = CreateData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = CallbackMessage;
            data.hIcon = _icon;
            data.szTip = _tooltip;

            if (Shell_NotifyIconW(NIM_ADD, ref data)) _added = true;
            else AppLog.Instance.Log(nameof(TrayIcon), $"添加托盘图标失败 (Win32={Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(TrayIcon), "添加托盘图标异常: " + ex.Message);
        }
    }

    private NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static IntPtr LoadTrayIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(path))
            {
                var icon = LoadImageW(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (icon != IntPtr.Zero) return icon;
                AppLog.Instance.Log(nameof(TrayIcon), "加载 app.ico 失败，改用系统默认图标");
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(TrayIcon), "加载托盘图标异常: " + ex.Message);
        }

        return LoadIconW(IntPtr.Zero, IDI_APPLICATION); // shared icon: never destroy it
    }

    // ---- message window ----------------------------------------------------

    private void CreateMessageWindow()
    {
        var instance = GetModuleHandleW(null);

        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = WindowClassName,
        };

        if (RegisterClassExW(ref windowClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_CLASS_ALREADY_EXISTS)
            {
                AppLog.Instance.Log(nameof(TrayIcon), $"注册托盘窗口类失败 (Win32={error})");
                return;
            }
        }

        // Hidden top-level window (see the class remarks for why it is not
        // HWND_MESSAGE); WS_EX_TOOLWINDOW keeps it out of Alt+Tab should it ever
        // be shown, and it is never passed to ShowWindow.
        _hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW, WindowClassName, string.Empty, WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            AppLog.Instance.Log(nameof(TrayIcon), $"创建托盘消息窗口失败 (Win32={Marshal.GetLastWin32Error()})");
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == CallbackMessage)
            {
                // The mouse event arrives in the low word of lParam.
                switch ((uint)(lParam.ToInt64() & 0xFFFF))
                {
                    case WM_LBUTTONDBLCLK:
                        RestoreMainWindow();
                        _onActivate();
                        return IntPtr.Zero;

                    case WM_RBUTTONUP:
                        ShowContextMenu();
                        return IntPtr.Zero;
                }
            }
            else if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                // Explorer restarted: the shell forgot every icon, so re-add ours
                // (WinForms' NotifyIcon did this for the original).
                AppLog.Instance.Log(nameof(TrayIcon), "任务栏已重建，重新添加托盘图标");
                _added = false;
                AddIcon();
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(TrayIcon), "托盘回调异常: " + ex.Message);
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ---- popup menu --------------------------------------------------------

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            // Same 5 entries as the original contextMenuNotify, Chinese strings verbatim.
            AppendMenuW(menu, MF_STRING, CmdShowMain, "显示主界面(&S)");
            AppendMenuW(menu, MF_STRING, CmdHideMain, "隐藏(&H)");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdAbout, "关于(&A)");
            AppendMenuW(menu, MF_STRING, CmdExit, "退出(&X)");

            if (!GetCursorPos(out var cursor)) cursor = default;

            // Documented TrackPopupMenu pattern: the owner must be foreground or the
            // menu will not close when the user clicks elsewhere. A message-only
            // window cannot be foreground, so this is best-effort only.
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(
                menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                cursor.X, cursor.Y, 0, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            HandleCommand(command);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(TrayIcon), "显示托盘菜单失败: " + ex.Message);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleCommand(int command)
    {
        switch (command)
        {
            case CmdShowMain:
                RestoreMainWindow();
                _onActivate();
                break;

            case CmdHideMain:
                HideMainWindow();
                break;

            case CmdAbout:
                RestoreMainWindow();
                App.MainWindow?.NavigateTo("about");
                break;

            case CmdExit:
                // Deferred on purpose: the exit handler closes the main window, whose
                // Closed handler disposes this tray icon — which would destroy the
                // very window whose WndProc is still on the stack.
                if (_dispatcher is not null) _dispatcher.TryEnqueue(() => _onExit());
                else _onExit();
                break;
        }
    }

    private void HideMainWindow()
    {
        var window = _mainWindow ?? App.MainWindow;
        if (window is null) return;

        var hwnd = WindowNative.GetWindowHandle(window);
        if (_hideToTrayEnabled?.Invoke() == true) ShowWindow(hwnd, SW_HIDE); // 隐藏(&H) with the tray option on
        else ShowWindow(hwnd, SW_MINIMIZE);
    }

    // ---- minimise to tray --------------------------------------------------

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // A restore raises transitional Changed events while the presenter still
        // reports Minimized; they must not hide the window again.
        if (_restoring) return;
        if (_hideToTrayEnabled?.Invoke() != true) return;
        if (sender.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State != OverlappedPresenterState.Minimized) return;

        var window = _mainWindow;
        if (window is null) return;

        // AppWindow.IsShownInSwitchers / presenter.IsResizable do not remove the
        // taskbar button of an already created window, so hide it outright — the
        // WinUI equivalent of the original's ShowInTaskbar = false.
        ShowWindow(WindowNative.GetWindowHandle(window), SW_HIDE);
    }

    // ---- interop -----------------------------------------------------------

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenu", SetLastError = true)]
    private static extern int TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
