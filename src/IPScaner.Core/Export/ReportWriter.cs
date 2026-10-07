using System.Globalization;
using System.Text;
using IPScaner.Core.Storage;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace IPScaner.Core.Export;

/// <summary>
/// Output formats produced by <see cref="ReportWriter"/>.
/// </summary>
public enum ReportFormat
{
    /// <summary>Comma-separated values, UTF-8 with BOM and RFC 4180 quoting.</summary>
    Csv,

    /// <summary>Human-readable fixed-width plain text, UTF-8 with BOM.</summary>
    Txt,

    /// <summary>Standalone offline HTML document, UTF-8 with BOM.</summary>
    Html,

    /// <summary>Paginated landscape A4 PDF with an embedded Chinese font.</summary>
    Pdf,

    /// <summary>Real SpreadsheetML workbook (.xlsx).</summary>
    Xlsx,
}

/// <summary>
/// One result table plus the metadata the report formats need.
/// </summary>
/// <remarks>
/// The request is deliberately format-agnostic: the same table can be rendered to
/// all five formats by changing <see cref="Format"/> only.
/// </remarks>
public sealed class ReportRequest
{
    /// <summary>Report title, e.g. "IP扫描结果".</summary>
    public required string Title { get; init; }

    /// <summary>Optional second line, e.g. "网段 192.168.1.0/24".</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>Column headers, in display order.</summary>
    public IReadOnlyList<string> Headers { get; init; } = [];

    /// <summary>The data rows; each row holds one cell per column.</summary>
    public IEnumerable<IReadOnlyList<string>> Rows { get; init; } = [];

    /// <summary>Requested output format.</summary>
    public ReportFormat Format { get; init; }
}

/// <summary>
/// Renders one result table into the five export formats of the original tool:
/// CSV, fixed-width text, standalone HTML, PDF and .xlsx.
/// </summary>
/// <remarks>
/// <para>
/// CSV and .xlsx are delegated to <see cref="TableExporter"/> with the cell values
/// untouched — both formats carry multi-line cells natively, so a memo column keeps its
/// line breaks. The three single-line formats (text, HTML and PDF) instead flatten tabs
/// and line breaks inside a cell into spaces, because a newline would break the
/// fixed-width layout of the text report and the row grid of the other two.
/// </para>
/// <para>
/// Every writer creates the target directory when it is missing.
/// </para>
/// <para>
/// Unlike the original — which printed Chinese through the GDI print driver and
/// produced boxes on a machine without the expected font — the PDF writer embeds a
/// real CJK TrueType font and fails loudly (<see cref="InvalidOperationException"/>)
/// when no usable font can be loaded, rather than emitting blank glyphs.
/// </para>
/// </remarks>
public static class ReportWriter
{
    /// <summary>Site shown in the HTML footer and on every PDF page.</summary>
    private const string SiteUrl = "https://www.xiaorin.cn";

    /// <summary>Spaces drawn between two text columns.</summary>
    private const int ColumnGap = 2;

    /// <summary>Smallest / largest display width a text column may occupy.</summary>
    private const int MinColumnWidth = 4;
    private const int MaxColumnWidth = 40;

    /// <summary>Cells longer than this are elided before layout work starts.</summary>
    private const int MaxCellLength = 400;

    // PDF layout, in PDF points (1 point = 1/72 inch); A4 landscape is 841.89 x 595.28.
    private const double MarginLeft = 36;
    private const double MarginRight = 36;
    private const double MarginTop = 34;
    private const double MarginBottom = 48;
    private const double CellPaddingX = 4;
    private const double MinColumnWidthPt = 36;
    private const double MaxColumnWidthPt = 240;
    private const double HeaderRowHeight = 18;
    private const int SampledRowsForWidth = 200;

    /// <summary>Text used to prove a candidate font really renders Chinese.</summary>
    private const string ProbeText = "中文报表 Chinese 0123";

    /// <summary>Returns the file extension (with the leading dot) used by a format.</summary>
    /// <param name="format">Format to map.</param>
    /// <returns>".csv", ".txt", ".html", ".pdf" or ".xlsx".</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is not defined.</exception>
    public static string ExtensionFor(ReportFormat format) => format switch
    {
        ReportFormat.Csv => ".csv",
        ReportFormat.Txt => ".txt",
        ReportFormat.Html => ".html",
        ReportFormat.Pdf => ".pdf",
        ReportFormat.Xlsx => ".xlsx",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知的导出格式。"),
    };

    /// <summary>Renders <paramref name="request"/> to <paramref name="path"/>.</summary>
    /// <param name="request">The table and its metadata.</param>
    /// <param name="path">Target file path; a missing directory is created.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested format is not defined.</exception>
    /// <exception cref="InvalidOperationException">PDF export found no usable Chinese font.</exception>
    public static void Write(ReportRequest request, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        switch (request.Format)
        {
            case ReportFormat.Csv:
                // Delegated verbatim: CSV quotes embedded line breaks, so the values are
                // handed to TableExporter untouched.
                TableExporter.WriteCsv(path, request.Headers, request.Rows);
                break;
            case ReportFormat.Txt:
                WriteTxt(request, path);
                break;
            case ReportFormat.Html:
                WriteHtml(request, path);
                break;
            case ReportFormat.Pdf:
                WritePdf(request, path);
                break;
            case ReportFormat.Xlsx:
                // Delegated verbatim; TableExporter sanitises the sheet name itself.
                TableExporter.WriteXlsx(path, request.Title, request.Headers, request.Rows);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Format, "未知的导出格式。");
        }
    }

    /// <summary>
    /// Writes the fixed-width text report: title, generation time, rule, header row,
    /// data rows and a row-count footer. UTF-8 <b>with</b> BOM so Notepad on a
    /// Chinese system shows the characters instead of mojibake.
    /// </summary>
    /// <param name="request">The table and its metadata.</param>
    /// <param name="path">Target file path; a missing directory is created.</param>
    public static void WriteTxt(ReportRequest request, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        WriteTextFile(path, BuildTxt(request, DateTime.Now));
    }

    /// <summary>
    /// Writes a complete, self-contained HTML document (inline CSS only, no CDN and
    /// no external assets, so it renders offline). UTF-8 <b>with</b> BOM.
    /// </summary>
    /// <param name="request">The table and its metadata.</param>
    /// <param name="path">Target file path; a missing directory is created.</param>
    public static void WriteHtml(ReportRequest request, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        WriteTextFile(path, BuildHtml(request, DateTime.Now));
    }

    /// <summary>
    /// Writes the landscape A4 PDF report with an embedded Chinese TrueType font,
    /// a repeating header row on every page and per-page footers.
    /// </summary>
    /// <param name="request">The table and its metadata.</param>
    /// <param name="path">Target file path; a missing directory is created.</param>
    /// <exception cref="InvalidOperationException">
    /// No CJK font could be loaded from the Windows font folder. The message lists
    /// every path that was tried; nothing is written in that case.
    /// </exception>
    public static void WritePdf(ReportRequest request, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stamp = DateTime.Now;
        var title = FlattenCell(request.Title);
        var subtitle = FlattenCell(request.Subtitle);
        var rows = MaterializeRows(request.Rows);
        var headers = FlattenAll(request.Headers);
        var columns = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(row => row.Count));

        if (columns > 0 && headers.Count == 0)
        {
            // A header row is part of the PDF layout; name unnamed columns rather
            // than dropping the header line entirely.
            headers = Enumerable.Range(1, columns).Select(i => $"列 {i}").ToList();
        }

        // Fails with a Chinese message when no CJK font is available; never silent.
        var family = PdfFonts.FamilyName;
        var options = XPdfFontOptions.UnicodeDefault;

        var titleFont = new XFont(family, 17, XFontStyleEx.Bold, options);
        var metaFont = new XFont(family, 9, XFontStyleEx.Regular, options);
        var headFont = new XFont(family, 9.5, XFontStyleEx.Bold, options);
        var bodyFont = new XFont(family, 9, XFontStyleEx.Regular, options);
        var footFont = new XFont(family, 8, XFontStyleEx.Regular, options);

        var textBrush = new XSolidBrush(XColor.FromArgb(0x1F, 0x29, 0x33));
        var mutedBrush = new XSolidBrush(XColor.FromArgb(0x6B, 0x76, 0x84));
        var headTextBrush = new XSolidBrush(XColor.FromArgb(0x11, 0x2A, 0x46));
        var headFillBrush = new XSolidBrush(XColor.FromArgb(0xEE, 0xF2, 0xF6));
        var zebraBrush = new XSolidBrush(XColor.FromArgb(0xFA, 0xFB, 0xFC));
        var gridPen = new XPen(XColor.FromArgb(0xD3, 0xDA, 0xE3), 0.5);
        var rulePen = new XPen(XColor.FromArgb(0x9A, 0xA5, 0xB1), 0.8);

        using var document = new PdfDocument();
        document.Info.Title = title;
        document.Info.Subject = subtitle;
        document.Info.Creator = "IPScaner";
        document.Info.CreationDate = stamp;

        var page = AddLandscapeA4Page(document);
        var gfx = XGraphics.FromPdfPage(page);
        try
        {
            var contentWidth = page.Width.Point - MarginLeft - MarginRight;
            var y = DrawReportHead(gfx, contentWidth, title, subtitle, stamp, rows.Count, titleFont, metaFont, rulePen, textBrush, mutedBrush);

            if (columns > 0)
            {
                var widths = MeasureColumns(gfx, headers, rows, columns, headFont, bodyFont, contentWidth);
                var rowHeight = bodyFont.Height + 7;

                y = DrawHeaderRow(gfx, headers, widths, y, HeaderRowHeight, headFont, headFillBrush, headTextBrush, gridPen);

                var index = 0;
                foreach (var row in rows)
                {
                    index++;
                    if (y + rowHeight > page.Height.Point - MarginBottom)
                    {
                        // Paginate: new page, repeated title line and repeated header row.
                        gfx.Dispose();
                        page = AddLandscapeA4Page(document);
                        gfx = XGraphics.FromPdfPage(page);
                        y = DrawContinuationHead(gfx, contentWidth, title, rulePen, headFont, textBrush);
                        y = DrawHeaderRow(gfx, headers, widths, y, HeaderRowHeight, headFont, headFillBrush, headTextBrush, gridPen);
                    }

                    DrawDataRow(gfx, row, widths, y, rowHeight, index % 2 == 0, bodyFont, textBrush, zebraBrush, gridPen);
                    y += rowHeight;
                }

                y += 4;
                DrawClipped(gfx, $"共 {rows.Count} 条记录", bodyFont, mutedBrush, new XRect(MarginLeft, y, contentWidth, bodyFont.Height + 4));
            }
            else
            {
                DrawClipped(gfx, "没有可导出的记录。", bodyFont, mutedBrush, new XRect(MarginLeft, y, contentWidth, bodyFont.Height + 4));
            }
        }
        finally
        {
            gfx.Dispose();
        }

        DrawPageFooters(document, stamp, rulePen, footFont, mutedBrush);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        document.Save(path);
    }

    // ---- text ---------------------------------------------------------------

    private static string BuildTxt(ReportRequest request, DateTime stamp)
    {
        var headers = FlattenAll(request.Headers);
        var rows = MaterializeRows(request.Rows);
        var columns = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(row => row.Count));
        var widths = MeasureTextColumns(headers, rows, columns);

        var tableWidth = columns == 0 ? 0 : widths.Sum() + ColumnGap * (columns - 1);
        var ruleWidth = Math.Max(tableWidth, Math.Max(DisplayWidth(FlattenCell(request.Title)), 32));

        var sb = new StringBuilder();
        sb.AppendLine(new string('=', ruleWidth));
        AppendCentered(sb, request.Title, ruleWidth);
        if (!string.IsNullOrWhiteSpace(request.Subtitle)) AppendCentered(sb, request.Subtitle, ruleWidth);
        AppendCentered(sb, $"生成时间：{Stamp(stamp)}", ruleWidth);
        sb.AppendLine(new string('=', ruleWidth));

        if (columns > 0)
        {
            sb.AppendLine(BuildTextRow(headers, widths));
            sb.AppendLine(new string('-', tableWidth));
            foreach (var row in rows) sb.AppendLine(BuildTextRow(row, widths));
            sb.AppendLine(new string('-', tableWidth));
        }

        sb.AppendLine($"共 {rows.Count} 条记录");
        return sb.ToString();
    }

    /// <summary>
    /// Column widths in <i>display</i> columns: a CJK character occupies two terminal
    /// cells, so measuring with <see cref="string.Length"/> would make the table ragged.
    /// </summary>
    private static int[] MeasureTextColumns(List<string> headers, List<IReadOnlyList<string>> rows, int columns)
    {
        var widths = new int[columns];
        for (var c = 0; c < columns; c++)
        {
            var width = c < headers.Count ? DisplayWidth(headers[c]) : 0;
            foreach (var row in rows)
            {
                if (c < row.Count) width = Math.Max(width, DisplayWidth(row[c]));
            }

            widths[c] = Math.Clamp(width, MinColumnWidth, MaxColumnWidth);
        }

        return widths;
    }

    private static string BuildTextRow(IReadOnlyList<string> cells, int[] widths)
    {
        var parts = new string[widths.Length];
        for (var c = 0; c < widths.Length; c++)
        {
            parts[c] = Fit(c < cells.Count ? cells[c] : string.Empty, widths[c]);
        }

        // Every cell — including the last one — is padded to its column width, so all
        // table lines are exactly the same display width. That makes the fixed-width
        // layout verifiable (and keeps column starts stable in every text editor).
        return string.Join(new string(' ', ColumnGap), parts);
    }

    /// <summary>Truncates or pads <paramref name="value"/> to an exact display width.</summary>
    private static string Fit(string value, int width)
    {
        var current = DisplayWidth(value);
        if (current <= width) return value + new string(' ', width - current);

        const int ellipsis = 3;
        var keep = Math.Max(0, width - ellipsis);
        var sb = new StringBuilder(width);
        var used = 0;
        for (var i = 0; i < value.Length;)
        {
            var length = char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
            var cellWidth = DisplayWidth(value.AsSpan(i, length));
            if (used + cellWidth > keep) break;
            sb.Append(value, i, length);
            used += cellWidth;
            i += length;
        }

        while (used + ellipsis > width && sb.Length > 0)
        {
            // Extremely narrow column: drop a character until the dots fit.
            var last = char.IsLowSurrogate(sb[^1]) && sb.Length > 1 ? 2 : 1;
            used -= DisplayWidth(sb.ToString(sb.Length - last, last));
            sb.Length -= last;
        }

        sb.Append('.', Math.Min(ellipsis, width));
        used = Math.Min(used + ellipsis, width);
        if (used < width) sb.Append(' ', width - used);
        return sb.ToString();
    }

    private static void AppendCentered(StringBuilder sb, string text, int width)
    {
        var value = FlattenCell(text);
        sb.Append(' ', Math.Max(0, (width - DisplayWidth(value)) / 2)).AppendLine(value);
    }

    /// <summary>
    /// Terminal width of <paramref name="value"/>: 2 cells for East Asian wide and
    /// fullwidth characters, 0 for combining marks and control characters, 1 otherwise.
    /// </summary>
    private static int DisplayWidth(string? value) => string.IsNullOrEmpty(value) ? 0 : DisplayWidth(value.AsSpan());

    private static int DisplayWidth(ReadOnlySpan<char> value)
    {
        var width = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                width += IsWideCodePoint(char.ConvertToUtf32(c, value[i + 1])) ? 2 : 1;
                i++;
                continue;
            }

            if (IsZeroWidth(c)) continue;
            width += IsWideCodePoint(c) ? 2 : 1;
        }

        return width;
    }

    private static bool IsZeroWidth(char c) =>
        char.IsControl(c) || c == '\u200B' || c is >= '\u0300' and <= '\u036F' || c is >= '\uFE00' and <= '\uFE0F' || c == '\uFEFF';

    /// <summary>Unicode East Asian Width property, classes W and F.</summary>
    private static bool IsWideCodePoint(int cp) => cp switch
    {
        >= 0x1100 and <= 0x115F => true,                               // Hangul Jamo
        >= 0x2E80 and <= 0x303E => true,                               // CJK radicals, Kangxi, punctuation
        >= 0x3041 and <= 0x33FF => true,                               // Kana, Hangul compat, CJK compat, CJK symbols
        >= 0x3400 and <= 0x4DBF => true,                               // CJK ext A
        >= 0x4E00 and <= 0x9FFF => true,                               // CJK unified ideographs
        >= 0xA000 and <= 0xA4CF => true,                               // Yi
        >= 0xA960 and <= 0xA97F => true,                               // Hangul Jamo ext A
        >= 0xAC00 and <= 0xD7A3 => true,                               // Hangul syllables
        >= 0xF900 and <= 0xFAFF => true,                               // CJK compat ideographs
        >= 0xFE10 and <= 0xFE19 => true,                               // Vertical forms
        >= 0xFE30 and <= 0xFE6F => true,                               // CJK compat forms
        >= 0xFF00 and <= 0xFF60 => true,                               // Fullwidth forms
        >= 0xFFE0 and <= 0xFFE6 => true,                               // Fullwidth signs
        >= 0x1F300 and <= 0x1F64F => true,                             // Emoji
        >= 0x1F900 and <= 0x1F9FF => true,                             // Supplemental symbols
        >= 0x20000 and <= 0x2FFFD => true,                             // CJK ext B..
        >= 0x30000 and <= 0x3FFFD => true,                             // CJK ext G..
        _ => false,
    };

    // ---- html ---------------------------------------------------------------

    private static string BuildHtml(ReportRequest request, DateTime stamp)
    {
        var title = FlattenCell(request.Title);
        var subtitle = FlattenCell(request.Subtitle);
        var headers = FlattenAll(request.Headers);
        var rows = MaterializeRows(request.Rows);
        var stampText = Stamp(stamp);
        var columns = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(row => row.Count));

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>{Html(title)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("""
            :root { color-scheme: light; }
            * { box-sizing: border-box; }
            body { margin: 0; padding: 24px 16px 48px; background: #f4f6f8; color: #1f2933;
                   font-family: "Microsoft YaHei", "PingFang SC", "Segoe UI", system-ui, sans-serif;
                   font-size: 14px; line-height: 1.55; }
            .wrap { max-width: 1080px; margin: 0 auto; }
            .card { background: #fff; border: 1px solid #e3e8ee; border-radius: 10px;
                    box-shadow: 0 1px 3px rgba(16, 24, 40, .06); overflow: hidden; }
            .head { padding: 20px 24px 16px; border-bottom: 1px solid #e3e8ee; }
            h1 { margin: 0 0 6px; font-size: 22px; font-weight: 600; }
            .subtitle { margin: 0 0 4px; color: #52606d; }
            .meta { margin: 0; color: #7b8794; font-size: 12px; }
            .scroll { max-height: 72vh; overflow: auto; }
            table { width: 100%; border-collapse: collapse; font-size: 13px; }
            thead th { position: sticky; top: 0; z-index: 1; background: #eef2f6; color: #112a46;
                       text-align: left; font-weight: 600; padding: 10px 14px; white-space: nowrap;
                       border-bottom: 2px solid #d3dae3; }
            tbody td { padding: 9px 14px; border-bottom: 1px solid #eef1f5; vertical-align: top;
                       overflow-wrap: anywhere; }
            tbody tr:nth-child(even) { background: #fafbfc; }
            tbody tr:hover { background: #f0f6ff; }
            tbody tr:last-child td { border-bottom: none; }
            .empty { margin: 0; padding: 28px 24px; color: #7b8794; }
            .foot { display: flex; flex-wrap: wrap; gap: 8px 16px; justify-content: space-between;
                    padding: 14px 24px; border-top: 1px solid #e3e8ee; color: #7b8794; font-size: 12px; }
            .foot a { color: #2563eb; text-decoration: none; }
            .foot a:hover { text-decoration: underline; }
            @media (max-width: 640px) {
              body { padding: 12px 8px 32px; }
              .head, .foot { padding: 14px 16px; }
              thead th, tbody td { padding: 8px 10px; }
              h1 { font-size: 19px; }
            }
            """);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"wrap\">");
        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine("<header class=\"head\">");
        sb.AppendLine($"<h1>{Html(title)}</h1>");
        if (subtitle.Length > 0) sb.AppendLine($"<p class=\"subtitle\">{Html(subtitle)}</p>");
        sb.AppendLine($"<p class=\"meta\">生成时间：{Html(stampText)} ｜ 共 {rows.Count} 条记录</p>");
        sb.AppendLine("</header>");

        if (columns == 0)
        {
            sb.AppendLine("<p class=\"empty\">没有可显示的数据。</p>");
        }
        else
        {
            sb.AppendLine("<div class=\"scroll\">");
            sb.AppendLine("<table>");
            if (headers.Count > 0)
            {
                sb.AppendLine("<thead>");
                sb.Append("<tr>");
                foreach (var header in headers) sb.Append($"<th scope=\"col\">{Html(header)}</th>");
                sb.AppendLine("</tr>");
                sb.AppendLine("</thead>");
            }

            sb.AppendLine("<tbody>");
            if (rows.Count == 0)
            {
                sb.AppendLine($"<tr><td colspan=\"{columns}\">没有记录。</td></tr>");
            }

            foreach (var row in rows)
            {
                sb.Append("<tr>");
                for (var c = 0; c < columns; c++)
                {
                    sb.Append($"<td>{Html(c < row.Count ? row[c] : string.Empty)}</td>");
                }

                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine($"<footer class=\"foot\"><span>由 IPScaner 生成 · <a href=\"{SiteUrl}\">{SiteUrl}</a></span>" +
                      $"<span>生成时间：{Html(stampText)} · 共 {rows.Count} 条记录</span></footer>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>Escapes a cell for HTML text/attribute context.</summary>
    private static string Html(string? value) =>
        (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    // ---- pdf ----------------------------------------------------------------

    private static PdfPage AddLandscapeA4Page(PdfDocument document)
    {
        var page = document.AddPage();
        page.Size = PageSize.A4;
        page.Orientation = PageOrientation.Landscape;
        return page;
    }

    /// <summary>Column widths in points, scaled so the table fills the content width.</summary>
    private static double[] MeasureColumns(
        XGraphics gfx,
        IReadOnlyList<string> headers,
        List<IReadOnlyList<string>> rows,
        int columns,
        XFont headFont,
        XFont bodyFont,
        double contentWidth)
    {
        var natural = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            var header = c < headers.Count ? headers[c] : string.Empty;
            natural[c] = gfx.MeasureString(header, headFont).Width + (2 * CellPaddingX);

            var sampled = 0;
            foreach (var row in rows)
            {
                if (sampled++ >= SampledRowsForWidth) break;
                var cell = c < row.Count ? row[c] : string.Empty;
                natural[c] = Math.Max(natural[c], gfx.MeasureString(cell, bodyFont).Width + (2 * CellPaddingX));
            }

            natural[c] = Math.Clamp(natural[c], MinColumnWidthPt, MaxColumnWidthPt);
        }

        var total = natural.Sum();
        if (total <= 0)
        {
            for (var c = 0; c < columns; c++) natural[c] = 1;
            total = columns;
        }

        var scale = contentWidth / total;
        var widths = new double[columns];
        for (var c = 0; c < columns; c++) widths[c] = natural[c] * scale;
        return widths;
    }

    private static double DrawReportHead(
        XGraphics gfx,
        double contentWidth,
        string title,
        string subtitle,
        DateTime stamp,
        int rowCount,
        XFont titleFont,
        XFont metaFont,
        XPen rulePen,
        XBrush textBrush,
        XBrush mutedBrush)
    {
        var y = MarginTop;
        DrawClipped(gfx, title, titleFont, textBrush, new XRect(MarginLeft, y, contentWidth, titleFont.Height + 4));
        y += titleFont.Height + 6;

        if (subtitle.Length > 0)
        {
            DrawClipped(gfx, subtitle, metaFont, mutedBrush, new XRect(MarginLeft, y, contentWidth, metaFont.Height + 2));
            y += metaFont.Height + 2;
        }

        var meta = $"生成时间：{Stamp(stamp)}    共 {rowCount} 条记录";
        DrawClipped(gfx, meta, metaFont, mutedBrush, new XRect(MarginLeft, y, contentWidth, metaFont.Height + 2));
        y += metaFont.Height + 6;

        gfx.DrawLine(rulePen, MarginLeft, y, MarginLeft + contentWidth, y);
        return y + 8;
    }

    private static double DrawContinuationHead(
        XGraphics gfx,
        double contentWidth,
        string title,
        XPen rulePen,
        XFont headFont,
        XBrush textBrush)
    {
        var y = MarginTop;
        DrawClipped(gfx, $"{title}（续）", headFont, textBrush, new XRect(MarginLeft, y, contentWidth, headFont.Height + 4));
        y += headFont.Height + 6;
        gfx.DrawLine(rulePen, MarginLeft, y, MarginLeft + contentWidth, y);
        return y + 8;
    }

    private static double DrawHeaderRow(
        XGraphics gfx,
        IReadOnlyList<string> headers,
        double[] widths,
        double y,
        double height,
        XFont font,
        XBrush fill,
        XBrush textBrush,
        XPen pen)
    {
        gfx.DrawRectangle(fill, new XRect(MarginLeft, y, widths.Sum(), height));

        var x = MarginLeft;
        for (var c = 0; c < widths.Length; c++)
        {
            var cell = new XRect(x, y, widths[c], height);
            DrawClipped(gfx, c < headers.Count ? headers[c] : string.Empty, font, textBrush, cell);
            gfx.DrawRectangle(pen, cell);
            x += widths[c];
        }

        return y + height;
    }

    private static void DrawDataRow(
        XGraphics gfx,
        IReadOnlyList<string> row,
        double[] widths,
        double y,
        double height,
        bool zebra,
        XFont font,
        XBrush textBrush,
        XBrush zebraBrush,
        XPen pen)
    {
        if (zebra) gfx.DrawRectangle(zebraBrush, new XRect(MarginLeft, y, widths.Sum(), height));

        var x = MarginLeft;
        for (var c = 0; c < widths.Length; c++)
        {
            DrawClipped(gfx, c < row.Count ? row[c] : string.Empty, font, textBrush, new XRect(x, y, widths[c], height));
            gfx.DrawLine(pen, x, y + height, x + widths[c], y + height);
            x += widths[c];
        }
    }

    /// <summary>
    /// Draws one cell through <see cref="XGraphics.DrawString(string, XFont, XBrush, XRect, XStringFormat)"/>
    /// inside its layout rectangle, clipped to that rectangle, so an over-long value is
    /// cut off at the column edge instead of overrunning the neighbouring columns.
    /// </summary>
    private static void DrawClipped(XGraphics gfx, string value, XFont font, XBrush brush, XRect rect)
    {
        if (value.Length == 0) return;

        var inner = new XRect(rect.X + CellPaddingX, rect.Y, Math.Max(1, rect.Width - (2 * CellPaddingX)), rect.Height);
        var state = gfx.Save();
        gfx.IntersectClip(inner);
        gfx.DrawString(value, font, brush, inner, XStringFormats.CenterLeft);
        gfx.Restore(state);
    }

    private static void DrawPageFooters(PdfDocument document, DateTime stamp, XPen rulePen, XFont font, XBrush brush)
    {
        var total = document.PageCount;
        for (var i = 0; i < total; i++)
        {
            var page = document.Pages[i];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var width = page.Width.Point;
            var y = page.Height.Point - 30;
            gfx.DrawLine(rulePen, MarginLeft, y, width - MarginRight, y);

            var rect = new XRect(MarginLeft, y + 3, width - MarginLeft - MarginRight, 14);
            gfx.DrawString($"IPScaner · {SiteUrl}", font, brush, rect, XStringFormats.CenterLeft);
            gfx.DrawString($"第 {i + 1} / {total} 页 · 生成时间：{Stamp(stamp)}", font, brush, rect, XStringFormats.CenterRight);
        }
    }

    // ---- shared helpers -----------------------------------------------------

    private static string Stamp(DateTime stamp) => stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static void WriteTextFile(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // UTF-8 with BOM: Notepad on a Chinese Windows treats BOM-less UTF-8 as ANSI
        // (code page 936) and would render every Chinese character as mojibake.
        File.WriteAllText(path, content, TextFileEncoding.Utf8Bom);
    }

    private static List<string> FlattenAll(IReadOnlyList<string>? cells) =>
        cells is null ? [] : cells.Select(FlattenCell).ToList();

    private static List<IReadOnlyList<string>> MaterializeRows(IEnumerable<IReadOnlyList<string>>? rows)
    {
        var result = new List<IReadOnlyList<string>>();
        if (rows is null) return result;

        foreach (var row in rows)
        {
            result.Add(row is null ? [] : row.Select(FlattenCell).ToArray());
        }

        return result;
    }

    /// <summary>
    /// Collapses tabs and line breaks into single spaces and drops other control
    /// characters, so one cell always occupies exactly one table line.
    /// </summary>
    private static string FlattenCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(Math.Min(value.Length, MaxCellLength));
        foreach (var c in value)
        {
            if (sb.Length >= MaxCellLength)
            {
                sb.Append("...");
                break;
            }

            if (c is '\r' or '\n' or '\t' or '\f' or '\v')
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            else if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Font plumbing for the PDF writer: one resolver instance serving a short list
    /// of CJK TrueType candidates from the Windows font folder.
    /// </summary>
    private static class PdfFonts
    {
        private static readonly FontCandidate[] Candidates = BuildCandidates();

        // PDFsharp requires the same resolver instance for every assignment.
        private static readonly CjkFontResolver Resolver = new(Candidates);

        private static readonly Lazy<string> ResolvedFamily =
            new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Family name of the first candidate that really renders Chinese.</summary>
        public static string FamilyName => ResolvedFamily.Value;

        private static FontCandidate[] BuildCandidates()
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrEmpty(folder)) folder = @"C:\Windows\Fonts";

            return
            [
                // SimHei first: it is a single-face TrueType file, which is what a custom
                // PDFsharp font resolver can serve. Microsoft YaHei only ships as a TrueType
                // collection (msyh.ttc) and PDFsharp 6.2.4 throws a NullReferenceException out
                // of OpenTypeFontFace for 'ttcf' files because the face index of a
                // FontResolverInfo is internal API — so it is kept as a fallback that is
                // probed for real and skipped when it fails.
                new("IPScanerCJK-simhei", Path.Combine(folder, "simhei.ttf")),
                new("IPScanerCJK-msyh", Path.Combine(folder, "msyh.ttc")),
            ];
        }

        private static string Probe()
        {
            GlobalFontSettings.FontResolver = Resolver;

            var attempts = new List<string>();
            foreach (var candidate in Candidates)
            {
                if (!File.Exists(candidate.FilePath))
                {
                    attempts.Add($"{candidate.FilePath}（文件不存在）");
                    continue;
                }

                try
                {
                    // A real (discarded) render proves the file parses, embeds and
                    // produces measurable Chinese text; a font that merely opens is
                    // not good enough.
                    var probe = new PdfDocument();
                    var page = probe.AddPage();
                    var gfx = XGraphics.FromPdfPage(page);
                    var font = new XFont(candidate.FamilyName, 10, XFontStyleEx.Regular, XPdfFontOptions.UnicodeDefault);
                    var size = gfx.MeasureString(ProbeText, font);
                    gfx.Dispose();

                    if (size.Width <= 0 || size.Height <= 0)
                    {
                        attempts.Add($"{candidate.FilePath}（无法度量中文字形）");
                        probe.Dispose();
                        continue;
                    }

                    using var sink = new MemoryStream();
                    probe.Save(sink, closeStream: false);
                    probe.Dispose();

                    if (sink.Length == 0)
                    {
                        attempts.Add($"{candidate.FilePath}（字体未能嵌入）");
                        continue;
                    }

                    return candidate.FamilyName;
                }
                catch (Exception ex)
                {
                    attempts.Add($"{candidate.FilePath}（{ex.GetType().Name}: {ex.Message}）");
                }
            }

            throw new InvalidOperationException(
                "无法加载可用的中文字体，PDF 导出已中止（不会输出空白或方框字符）。已尝试：" +
                string.Join("；", attempts) +
                "。请确认系统已安装微软雅黑（msyh.ttc）或黑体（simhei.ttf）。");
        }
    }

    /// <summary>One CJK font file usable for PDF export.</summary>
    private sealed record FontCandidate(string FamilyName, string FilePath);

    /// <summary>
    /// Maps the family names used by this class onto font files on disk.
    /// </summary>
    private sealed class CjkFontResolver : IFontResolver
    {
        private readonly Dictionary<string, FontCandidate> _candidates;
        private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);

        public CjkFontResolver(IEnumerable<FontCandidate> candidates) =>
            _candidates = candidates.ToDictionary(candidate => candidate.FamilyName, StringComparer.Ordinal);

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
            _candidates.TryGetValue(familyName, out var candidate)
                ? new FontResolverInfo(candidate.FamilyName, isBold, isItalic)
                : null;

        public byte[]? GetFont(string faceName)
        {
            if (_cache.TryGetValue(faceName, out var cached)) return cached;
            if (!_candidates.TryGetValue(faceName, out var candidate)) return null;

            var bytes = File.ReadAllBytes(candidate.FilePath);
            _cache[faceName] = bytes;
            return bytes;
        }
    }
}
