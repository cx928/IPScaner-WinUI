using System.Diagnostics;
using System.Runtime.InteropServices;
using IPScaner.Core.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace IPScaner.WinUI.Services;

/// <summary>
/// Small UI helpers shared by every page: colour conversion, dialogs, clipboard
/// and elevation restart.
/// </summary>
/// <remarks>
/// WinUI 3 has no <c>MessageBox</c>, so the original's modal prompts are all
/// expressed as <see cref="ContentDialog"/>. A dialog needs a live
/// <see cref="XamlRoot"/>, which is why every helper requires one.
/// </remarks>
public static class UiKit
{
    // ---- colour ------------------------------------------------------------

    /// <summary>
    /// Converts the tool's signed ARGB integer (WinForms <c>Color.ToArgb()</c>
    /// convention, e.g. SkyBlue = -7876885) to a WinUI <see cref="Color"/>.
    /// </summary>
    public static Color ColorFromArgb(int argb)
    {
        var u = unchecked((uint)argb);
        return Color.FromArgb(
            (byte)((u >> 24) & 0xFF),
            (byte)((u >> 16) & 0xFF),
            (byte)((u >> 8) & 0xFF),
            (byte)(u & 0xFF));
    }

    /// <summary>Converts a WinUI colour back to the signed ARGB integer the config file stores.</summary>
    public static int ArgbFromColor(Color c) =>
        unchecked((int)(((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B));

    public static SolidColorBrush BrushFromArgb(int argb) => new(ColorFromArgb(argb));

    /// <summary>Picks black or white text for legibility on a given background.</summary>
    public static Color ContrastingTextColor(Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
        return luminance > 0.55 ? Colors.Black : Colors.White;
    }

    // ---- clipboard ---------------------------------------------------------

    /// <summary>Copies text to the system clipboard, reporting failure rather than throwing.</summary>
    public static bool CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text ?? string.Empty);
            Clipboard.SetContent(package);
            Clipboard.Flush(); // keeps the content after the app exits
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(UiKit), "写入剪贴板失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>Reads text from the clipboard, or an empty string.</summary>
    public static async Task<string> ReadClipboardAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return string.Empty;
            return await view.GetTextAsync();
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---- dialogs -----------------------------------------------------------

    public static async Task InfoAsync(XamlRoot? root, string title, string message)
    {
        if (root is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowSafeAsync(dialog);
    }

    public static async Task<bool> ConfirmAsync(
        XamlRoot? root, string title, string message,
        string primaryText = "确定", string closeText = "取消")
    {
        if (root is null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowSafeAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Shows a dialog, swallowing the "only one ContentDialog may be open" error
    /// that concurrent scans can otherwise trigger.
    /// </summary>
    public static async Task<ContentDialogResult> ShowSafeAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(UiKit), "显示对话框失败: " + ex.Message);
            return ContentDialogResult.None;
        }
    }

    /// <summary>Shows a dialog whose body is arbitrary content (a colour picker, a grid...).</summary>
    public static async Task<ContentDialogResult> ShowContentAsync(
        XamlRoot? root, string title, object content,
        string? primaryText = null, string? secondaryText = null, string closeText = "关闭")
    {
        if (root is null) return ContentDialogResult.None;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText ?? string.Empty,
            SecondaryButtonText = secondaryText ?? string.Empty,
            CloseButtonText = closeText,
        };
        if (string.IsNullOrEmpty(primaryText)) dialog.PrimaryButtonText = string.Empty;
        return await ShowSafeAsync(dialog);
    }

    // ---- window ------------------------------------------------------------

    /// <summary>The main window's XamlRoot, for dialogs raised from services.</summary>
    public static XamlRoot? MainXamlRoot => App.MainWindow?.Content?.XamlRoot;

    /// <summary>Brings the main window to the foreground.</summary>
    public static void ActivateMainWindow()
    {
        var window = App.MainWindow;
        if (window is null) return;
        window.Activate();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    /// <summary>True when the current process is elevated.</summary>
    public static bool IsElevated => AppServices.Current.IsElevated;

    /// <summary>
    /// Relaunches the app with a UAC prompt. Used by 修改本地IP and ARP flush,
    /// which need administrator rights; the original simply forced elevation on
    /// every start.
    /// </summary>
    public static bool RestartElevated()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return false;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Exit();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(UiKit), "提权重启被取消或失败: " + ex.Message);
            return false;
        }
    }

    // ---- interop -----------------------------------------------------------

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
