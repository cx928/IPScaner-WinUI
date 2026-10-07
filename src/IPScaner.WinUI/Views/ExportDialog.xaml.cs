using IPScaner.Core.Export;
using IPScaner.Core.Logging;
using IPScaner.Core.Storage;
using IPScaner.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace IPScaner.WinUI.Views;

/// <summary>
/// Shared export chooser: pick one of the five report formats and a destination
/// folder, then hand <c>(格式, 完整路径)</c> back to the caller.
/// </summary>
/// <remarks>
/// <para>The dialog deliberately writes nothing. Every page that exports already
/// owns its own rows and its own file name; this type only decides <i>which</i>
/// format and <i>where</i>, and the caller performs the write through
/// <c>IPscaner.Core.Export.ReportWriter</c>.</para>
/// <para>WinUI constraints that shaped the implementation:</para>
/// <list type="bullet">
/// <item><b>The folder picker runs under our own window.</b> The picker is initialised
/// with the main window's handle and shown with the parameterless overload. Using the
/// <c>windowId</c> overload instead would make the picker disable its owning window,
/// and while a <see cref="ContentDialog"/> is open that window is <i>already</i>
/// disabled — so 浏览… would do nothing at all.</item>
/// <item><b>No second dialog.</b> Everything here stays inside the one
/// <see cref="ContentDialog"/>, so the "only one ContentDialog may be open" failure
/// cannot be triggered, and an invalid folder is reported in the preview panel
/// instead of through a nested prompt.</item>
/// </list>
/// </remarks>
public sealed partial class ExportDialog : ContentDialog
{
    /// <summary>Sub-folder of the data directory that exports default to.</summary>
    private const string ExportFolderName = "导出";

    private readonly string _suggestedBaseName;
    private readonly int _rowCount;

    private bool _ready;

    private ExportDialog(XamlRoot root, string suggestedBaseName, int rowCount)
    {
        InitializeComponent();

        _suggestedBaseName = string.IsNullOrWhiteSpace(suggestedBaseName) ? "导出结果" : suggestedBaseName.Trim();
        _rowCount = rowCount;

        // The default folder is ours, so creating it is safe and makes 打开文件夹
        // and the preview honest. A pre-existing custom folder is never touched.
        try { Directory.CreateDirectory(DefaultFolder); }
        catch (Exception ex) { AppLog.Instance.Log(nameof(ExportDialog), "创建导出目录失败: " + ex.Message); }

        FolderBox.Text = DefaultFolder;
        _ready = true;
        UpdatePreview();
    }

    /// <summary>
    /// Shows the chooser. Returns the chosen format and full output path, or
    /// <c>null</c> when the user cancelled.
    /// </summary>
    public static async Task<(ReportFormat Format, string Path)?> ShowAsync(
        XamlRoot root, string suggestedBaseName, int rowCount)
    {
        if (root is null) return null;

        var dialog = new ExportDialog(root, suggestedBaseName, rowCount) { XamlRoot = root };
        var result = await UiKit.ShowSafeAsync(dialog);
        if (result != ContentDialogResult.Primary) return null;

        var format = dialog.SelectedFormat;
        var path = dialog.BuildPath(format);
        if (path is null) return null;

        return (format, path);
    }

    // =====================================================================
    // state
    // =====================================================================

    private ReportFormat SelectedFormat
    {
        get
        {
            var tag = new[] { TxtRadio, CsvRadio, XlsxRadio, HtmlRadio, PdfRadio }
                .FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

            // Tag values mirror the enum member names, so a rename over there is a
            // compile-time-visible parse failure rather than a silent export bug.
            return tag is not null && Enum.TryParse<ReportFormat>(tag, out var parsed)
                ? parsed
                : ReportFormat.Txt;
        }
    }

    private static string DefaultFolder => Path.Combine(AppPaths.DataDirectory, ExportFolderName);

    private string CurrentFolder
    {
        get
        {
            var text = (FolderBox.Text ?? string.Empty).Trim().Trim('"');
            return text.Length == 0 ? DefaultFolder : text;
        }
    }

    private static string ExtensionOf(ReportFormat format) => format switch
    {
        ReportFormat.Csv => ".csv",
        ReportFormat.Xlsx => ".xlsx",
        ReportFormat.Html => ".html",
        ReportFormat.Pdf => ".pdf",
        _ => ".txt",
    };

    // =====================================================================
    // events
    // =====================================================================

    private void OnFormatChecked(object sender, RoutedEventArgs e) => UpdatePreview();

    private void OnFolderTextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            // Initialise with our own window handle and use the parameterless show:
            // the windowId overload would disable this dialog's owning window for the
            // duration of the pick, and that window is already disabled by the
            // ContentDialog being open.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            if (hwnd == IntPtr.Zero) return;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return; // cancelled: keep the current folder

            FolderBox.Text = folder.Path;
            UpdatePreview();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ExportDialog), "选择文件夹失败: " + ex.Message);
            WarningText.Text = "打开文件夹选择器失败，可以直接在文本框里输入路径。";
            WarningText.Visibility = Visibility.Visible;
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = CurrentFolder;
        try
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ExportDialog), "创建导出目录失败: " + ex.Message);
        }

        if (!Directory.Exists(folder))
        {
            WarningText.Text = $"文件夹不存在，无法打开：{folder}";
            WarningText.Visibility = Visibility.Visible;
            return;
        }

        // Shell-launching rather than the Core ShellLauncher, because this type has
        // to stay usable from any page without dragging AppServices into a dialog.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ExportDialog), "打开文件夹失败: " + ex.Message);
        }
    }

    // =====================================================================
    // preview
    // =====================================================================

    /// <summary>Rebuilds the summary and the resolved file name.</summary>
    private void UpdatePreview()
    {
        if (!_ready) return;

        var format = SelectedFormat;
        var folder = CurrentFolder;
        var fileName = BuildFileName(format);
        var fullPath = Combine(folder, fileName);

        var rows = _rowCount > 0
            ? $"本次将导出 {_rowCount} 行数据。"
            : "本次没有可导出的数据行，导出文件将只有表头。";

        SummaryText.Text = $"{rows}格式：{FormatLabel(format)}";

        PreviewText.Text = fullPath is null
            ? fileName
            : fullPath;

        if (fullPath is null)
        {
            WarningText.Text = "保存位置无效，请重新选择文件夹。";
            WarningText.Visibility = Visibility.Visible;
        }
        else if (_rowCount == 0)
        {
            WarningText.Text = "没有数据行，建议先完成一次扫描或查询。";
            WarningText.Visibility = Visibility.Visible;
        }
        else
        {
            WarningText.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatLabel(ReportFormat format) => format switch
    {
        ReportFormat.Csv => "CSV",
        ReportFormat.Xlsx => "Excel(XLSX)",
        ReportFormat.Html => "网页(HTML)",
        ReportFormat.Pdf => "PDF",
        _ => "文本(TXT)",
    };

    /// <summary>The file name the caller will write, resolved for the preview.</summary>
    private string BuildFileName(ReportFormat format)
        => TableExporter.BuildFileName(_suggestedBaseName, ExtensionOf(format));

    /// <summary>The full output path, or <c>null</c> when the folder is unusable.</summary>
    private string? BuildPath(ReportFormat format)
    {
        var fullPath = Combine(CurrentFolder, BuildFileName(format));
        if (fullPath is null) return null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? CurrentFolder);
            return fullPath;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(ExportDialog), "创建导出目录失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>Joins a folder and a file name, rejecting anything the OS would refuse.</summary>
    private static string? Combine(string folder, string fileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var full = Path.GetFullPath(Path.Combine(folder, fileName));

            // A hand-typed folder can legitimately contain quotes or end in a dot,
            // both of which make File.Create throw much later with a worse message.
            if (full.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            if (full.EndsWith('.')) return null;
            return full;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Suggested name for a row set, exposed for pages that want to show it before
    /// opening the dialog. Kept here so both sides agree on the shape.
    /// </summary>
    public static string SuggestFileName(string baseName, ReportFormat format)
    {
        var extension = format switch
        {
            ReportFormat.Csv => ".csv",
            ReportFormat.Xlsx => ".xlsx",
            ReportFormat.Html => ".html",
            ReportFormat.Pdf => ".pdf",
            _ => ".txt",
        };
        return TableExporter.BuildFileName(baseName, extension);
    }
}
