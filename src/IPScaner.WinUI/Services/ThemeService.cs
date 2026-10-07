using IPScaner.Core.Configuration;
using IPScaner.Core.Logging;
using Microsoft.UI.Xaml;

namespace IPScaner.WinUI.Services;

/// <summary>
/// Owns the application's light/dark theme and applies it live.
/// </summary>
/// <remarks>
/// WinUI has no <c>Application.RequestedTheme</c> setter that works after startup,
/// so a theme switch means setting <see cref="FrameworkElement.RequestedTheme"/> on
/// a root element. This service applies it to the shell root
/// (<c>App.MainWindow.Content</c>) when that is reachable, and raises
/// <see cref="Changed"/> so the page that owns the toggle can also stamp its own
/// root — the two agree, because both are derived from the saved
/// <see cref="AppConfig.ThemeMode"/>.
///
/// <para>
/// The shell root is set at run time rather than in markup because
/// <c>MainWindow.xaml.cs</c> belongs to the shell: the permanent form of this is one
/// line in <c>MainWindow</c>'s constructor —
/// <c>RootGrid.RequestedTheme = ThemeService.ElementFor(ThemeService.Current);</c> —
/// plus the same call from its <c>ConfigChanged</c> handler.
/// </para>
/// </remarks>
public static class ThemeService
{
    private static ThemeMode _current = ThemeMode.System;

    /// <summary>The theme currently applied.</summary>
    public static ThemeMode Current => _current;

    /// <summary>Raised after <see cref="Apply"/> changes the theme.</summary>
    public static event EventHandler<ThemeMode>? Changed;

    /// <summary>
    /// 跟随系统 / 浅色 / 深色 → the <see cref="ElementTheme"/> an element needs.
    /// <see cref="ThemeMode.System"/> maps to <see cref="ElementTheme.Default"/>,
    /// which is what makes 跟随系统 follow the OS setting without a restart.
    /// </summary>
    public static ElementTheme ElementFor(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Applies a theme to the whole window and notifies listeners.</summary>
    public static void Apply(ThemeMode mode)
    {
        _current = mode;
        var theme = ElementFor(mode);

        try
        {
            // The window's content root is the outermost element the app owns; theming
            // it covers the navigation pane, the custom title bar and the status strip
            // as well as the current page. Guarded because the shell is not ours: if
            // its shape ever changes, the page-level theme still applies.
            if (App.MainWindow?.Content is FrameworkElement root) root.RequestedTheme = theme;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ThemeService), "应用主题失败: " + ex.Message);
        }

        Changed?.Invoke(null, mode);
    }
}
