using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using IPScaner.Core.Export;
using Xunit;

namespace IPScaner.Core.Tests;

/// <summary>
/// Coverage for <see cref="ReportWriter"/>: extension mapping, one table rendered into
/// all five formats, the display-width text layout, HTML escaping and the PDF
/// pagination / CJK font embedding.
/// </summary>
public class ReportWriterTests
{
    /// <summary>Sample kept in the test output folder for the bundled-Python PDF check.</summary>
    public const string PdfSampleName = "report-sample-ipscan.pdf";

    /// <summary>300-row sample kept for the out-of-process pagination / text check.</summary>
    public const string PdfLongSampleName = "report-sample-ipscan-300.pdf";

    // ---- ExtensionFor ------------------------------------------------------

    [Theory]
    [InlineData(ReportFormat.Csv, ".csv")]
    [InlineData(ReportFormat.Txt, ".txt")]
    [InlineData(ReportFormat.Html, ".html")]
    [InlineData(ReportFormat.Pdf, ".pdf")]
    [InlineData(ReportFormat.Xlsx, ".xlsx")]
    public void ExtensionFor_MapsEveryFormat(ReportFormat format, string expected)
        => Assert.Equal(expected, ReportWriter.ExtensionFor(format));

    [Fact]
    public void ExtensionFor_RejectsAnUndefinedFormat()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ReportWriter.ExtensionFor((ReportFormat)99));

    // ---- Write dispatch ----------------------------------------------------

    [Theory]
    [InlineData(ReportFormat.Csv)]
    [InlineData(ReportFormat.Txt)]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Pdf)]
    [InlineData(ReportFormat.Xlsx)]
    public void Write_ProducesANonEmptyFileForEveryFormat(ReportFormat format)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("report" + ReportWriter.ExtensionFor(format));

        ReportWriter.Write(Sample(format), path);

        var file = new FileInfo(path);
        Assert.True(file.Exists, $"{format} did not create {path}");
        Assert.True(file.Length > 0, $"{format} produced an empty file");
    }

    [Fact]
    public void Write_CreatesMissingNestedDirectories()
    {
        using var workspace = new TempWorkspace();
        var directory = Path.Combine(workspace.Root, "导出", "2026");

        foreach (var format in Enum.GetValues<ReportFormat>())
        {
            var path = Path.Combine(directory, "report" + ReportWriter.ExtensionFor(format));
            ReportWriter.Write(Sample(format), path);
            Assert.True(File.Exists(path), $"{format} did not create {path}");
        }
    }

    [Fact]
    public void Write_RejectsANullRequestAndABlankPath()
    {
        using var workspace = new TempWorkspace();

        Assert.Throws<ArgumentNullException>(() => ReportWriter.Write(null!, workspace.PathFor("x.txt")));
        Assert.Throws<ArgumentException>(() => ReportWriter.Write(Sample(ReportFormat.Txt), "   "));
        Assert.Throws<ArgumentNullException>(() => ReportWriter.WriteTxt(null!, workspace.PathFor("x.txt")));
        Assert.Throws<ArgumentNullException>(() => ReportWriter.WriteHtml(null!, workspace.PathFor("x.html")));
        Assert.Throws<ArgumentNullException>(() => ReportWriter.WritePdf(null!, workspace.PathFor("x.pdf")));
    }

    [Fact]
    public void Write_RejectsAnUndefinedFormat()
    {
        using var workspace = new TempWorkspace();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportWriter.Write(Sample((ReportFormat)42), workspace.PathFor("x.bin")));
    }

    // ---- Csv / Xlsx delegation ---------------------------------------------

    [Fact]
    public void Write_Csv_DelegatesToTableExporterWithHeadersAndRows()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("out.csv");

        ReportWriter.Write(Sample(ReportFormat.Csv), path);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));

        var lines = File.ReadAllText(path, Encoding.UTF8).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("网络地址,主机名,状态,备注", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.Contains(lines, line => line.Contains("\"有,逗号\"", StringComparison.Ordinal));   // RFC 4180 quoting still applies
    }

    [Fact]
    public void Write_Csv_KeepsMultiLineCellsVerbatim()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("multiline.csv");

        // CSV and .xlsx carry embedded line breaks natively, so they are delegated
        // verbatim; only the single-line formats flatten them (see the Txt test).
        ReportWriter.Write(new ReportRequest
        {
            Title = "备注",
            Headers = ["备注"],
            Rows = [new[] { "上\n下" }],
            Format = ReportFormat.Csv,
        }, path);

        Assert.Contains("\"上\n下\"", File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void Write_Xlsx_DelegatesToTableExporterAndNamesTheSheetAfterTheTitle()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("out.xlsx");

        var request = Sample(ReportFormat.Xlsx);
        ReportWriter.Write(new ReportRequest
        {
            Title = "IP扫描结果:2026*[?]",
            Subtitle = request.Subtitle,
            Headers = request.Headers,
            Rows = request.Rows,
            Format = ReportFormat.Xlsx,
        }, path);

        using var zip = ZipFile.OpenRead(path);
        var workbook = ReadEntry(zip, "xl/workbook.xml");
        var sheet = ReadEntry(zip, "xl/worksheets/sheet1.xml");

        Assert.Contains("name=\"IP扫描结果2026\"", workbook);   // TableExporter sanitises the name
        Assert.Contains("网络地址", sheet);
        Assert.Contains("192.168.1.10", sheet);
    }

    // ---- Txt ---------------------------------------------------------------

    [Fact]
    public void WriteTxt_HasTitleTimestampHeaderRowsAndSummaryFooter()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("report.txt");

        ReportWriter.WriteTxt(Sample(ReportFormat.Txt), path);

        // UTF-8 with BOM, or Notepad on a Chinese system renders mojibake.
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));

        var text = File.ReadAllText(path, Encoding.UTF8);
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        Assert.Contains("IP扫描结果", text);
        Assert.Contains("网段 192.168.1.0/24", text);
        Assert.Matches(@"生成时间：\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", text);
        Assert.Contains(lines, line => line.StartsWith("===", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("---", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("网络地址", StringComparison.Ordinal) && line.Contains("主机名", StringComparison.Ordinal));
        Assert.EndsWith($"共 3 条记录{Environment.NewLine}", text);
    }

    [Fact]
    public void WriteTxt_EmptyResultStillPrintsTheHeaderAndAZeroCount()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("empty.txt");

        ReportWriter.WriteTxt(new ReportRequest
        {
            Title = "IP扫描结果",
            Headers = ["网络地址", "主机名"],
            Rows = [],
            Format = ReportFormat.Txt,
        }, path);

        var text = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains("网络地址", text);
        Assert.Contains("共 0 条记录", text);
    }

    [Fact]
    public void WriteTxt_PadsColumnsByDisplayWidth_NotByStringLength()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("aligned.txt");

        // Column 1 is 13 display columns wide (driven by "192.168.1.100"). "局域网" is
        // 3 characters but 6 display columns, so it must be padded with 7 spaces - padding
        // by string.Length would have emitted 10 and pushed every later column right.
        ReportWriter.WriteTxt(new ReportRequest
        {
            Title = "IP扫描结果",
            Headers = ["网络地址", "主机名", "状态", "备注"],
            Rows = new List<IReadOnlyList<string>>
            {
                new[] { "192.168.1.10", "gw-01", "在线", "默认网关" },
                new[] { "局域网", "远程服务器", "在线", "中文测试" },
                new[] { "192.168.1.100", "pc-2", "离线", "" },
            },
            Format = ReportFormat.Txt,
        }, path);

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var separators = lines.Select((line, index) => (line, index))
            .Where(entry => entry.line.StartsWith("---", StringComparison.Ordinal))
            .Select(entry => entry.index)
            .ToArray();
        Assert.Equal(2, separators.Length);

        // The header row sits directly above the first '-' rule; the data rows sit between
        // the two rules.
        var headerRow = lines[separators[0] - 1];
        var dataRows = lines[(separators[0] + 1)..separators[1]];
        Assert.Equal(3, dataRows.Length);

        var table = new[] { headerRow }.Concat(dataRows).ToArray();

        // Every table line occupies exactly the same number of terminal columns.
        var widths = table.Select(DisplayWidth).Distinct().ToArray();
        Assert.Single(widths);

        // The concrete padding case: 6 display columns of text in a 13 column field.
        Assert.StartsWith("局域网" + new string(' ', 7) + "  " + "远程服务器", table[2], StringComparison.Ordinal);

        // ... and the second column starts at the same display offset in every row,
        // which is exactly what padding by string.Length would break.
        var secondColumn = new[] { "gw-01", "远程服务器", "pc-2" };
        var offsets = table.Skip(1)
            .Select((line, index) => DisplayWidth(line[..line.IndexOf(secondColumn[index], StringComparison.Ordinal)]))
            .Distinct()
            .ToArray();
        Assert.Single(offsets);
    }

    [Fact]
    public void WriteTxt_FlattensLineBreaksInsideACell()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("multiline.txt");

        ReportWriter.WriteTxt(new ReportRequest
        {
            Title = "备注",
            Headers = ["备注"],
            Rows = [new[] { "第一行\r\n第二行\t带制表符" }],
            Format = ReportFormat.Txt,
        }, path);

        var lines = File.ReadAllLines(path, Encoding.UTF8);

        // One cell, one line: the line breaks are flattened into spaces.
        var cellLine = Assert.Single(lines, line => line.Contains("第一行", StringComparison.Ordinal));
        Assert.Contains("第一行 第二行 带制表符", cellLine);
    }

    // ---- Html --------------------------------------------------------------

    [Fact]
    public void WriteHtml_IsAStandaloneOfflineDocument()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("report.html");

        ReportWriter.WriteHtml(Sample(ReportFormat.Html), path);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));

        var html = File.ReadAllText(path, Encoding.UTF8);
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"zh-CN\">", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<title>IP扫描结果</title>", html);
        Assert.Contains("<style>", html);

        // Presentation contract: sticky header, zebra striping, padding, max-width container.
        Assert.Contains("position: sticky", html);
        Assert.Contains("nth-child(even)", html);
        Assert.Contains("padding: 10px 14px", html);
        Assert.Contains("max-width: 1080px", html);

        // Footer carries the generation time and the site.
        Assert.Matches(@"生成时间：\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", html);
        Assert.Contains("https://www.xiaorin.cn", html);
        Assert.Contains("网络地址", html);
    }

    [Fact]
    public void WriteHtml_ReferencesNoExternalAssets()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("report.html");

        ReportWriter.WriteHtml(Sample(ReportFormat.Html), path);

        var html = File.ReadAllText(path, Encoding.UTF8);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cdn", html, StringComparison.OrdinalIgnoreCase);

        // The only absolute URL in the file is the site shown in the footer.
        var urls = Regex.Matches(html, @"https?://[^""\s<]+").Select(match => match.Value).Distinct().ToArray();
        Assert.Equal(new[] { "https://www.xiaorin.cn" }, urls);
    }

    [Fact]
    public void WriteHtml_EscapesEveryCell()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("escape.html");

        ReportWriter.WriteHtml(new ReportRequest
        {
            Title = "转义 <测试> & \"引号\"",
            Subtitle = "<b>粗体</b>",
            Headers = ["列<1>"],
            Rows = [new[] { "<script>alert(\"xss\")</script>", "a & b", "5 > 3" }],
            Format = ReportFormat.Html,
        }, path);

        var html = File.ReadAllText(path, Encoding.UTF8);

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>粗体</b>", html);
        Assert.Contains("&lt;script&gt;alert(&quot;xss&quot;)&lt;/script&gt;", html);
        Assert.Contains("a &amp; b", html);
        Assert.Contains("5 &gt; 3", html);
        Assert.Contains("转义 &lt;测试&gt; &amp; &quot;引号&quot;", html);
        Assert.Contains("列&lt;1&gt;", html);
    }

    [Fact]
    public void WriteHtml_EmptyResultStillRendersAValidDocument()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("empty.html");

        ReportWriter.WriteHtml(new ReportRequest { Title = "空结果", Format = ReportFormat.Html }, path);

        var html = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains("没有可显示的数据。", html);
        Assert.Contains("共 0 条记录", html);
        Assert.EndsWith("</html>" + Environment.NewLine, html);
    }

    // ---- Pdf ---------------------------------------------------------------

    [Fact]
    public void WritePdf_StartsWithThePdfHeaderAndUsesLandscapeA4()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("report.pdf");

        ReportWriter.WritePdf(Sample(ReportFormat.Pdf), path);

        var latin = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.StartsWith("%PDF-", latin, StringComparison.Ordinal);

        var media = Regex.Match(latin, @"/MediaBox\s*\[\s*0\s+0\s+(\d+)\s+(\d+)\s*\]");
        Assert.True(media.Success, "no /MediaBox found in the PDF");

        var width = double.Parse(media.Groups[1].Value, CultureInfo.InvariantCulture);
        var height = double.Parse(media.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.True(width > height, $"page is not landscape ({width} x {height})");
        Assert.InRange(width, 841, 843);     // A4 landscape, in points
        Assert.InRange(height, 594, 596);
    }

    [Fact]
    public void WritePdf_KeepsShortTablesOnASinglePage()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("one-page.pdf");

        ReportWriter.WritePdf(Sample(ReportFormat.Pdf), path);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(1, CountPages(bytes));
        Assert.Equal(1, PageTreeCount(bytes));
    }

    [Fact]
    public void WritePdf_PaginatesALongTableAndRepeatsTheHeaderRow()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("many-pages.pdf");

        ReportWriter.WritePdf(LongSample(ReportFormat.Pdf), path);

        var bytes = File.ReadAllBytes(path);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);

        var pages = CountPages(bytes);
        Assert.True(pages > 1, $"300 rows should not fit on one page, found {pages} page(s)");
        Assert.Equal(pages, PageTreeCount(bytes));

        // The header row is redrawn on the continuation pages, so the column header
        // string has to appear in every page's content stream.
        Assert.True(CountOccurrences(Encoding.Latin1.GetString(bytes), "/Type/Page") > 1);
    }

    [Fact]
    public void WritePdf_EmbedsASubsettedCjkFontWithAToUnicodeMap()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("font.pdf");

        ReportWriter.WritePdf(Sample(ReportFormat.Pdf), path);

        var latin = Encoding.Latin1.GetString(File.ReadAllBytes(path));

        // A TrueType subset (six-letter tag + "+" prefix) embedded as /FontFile2 and a
        // /ToUnicode CMap: without both, Chinese either renders as boxes or cannot be
        // copied/searched out of the document.
        Assert.Contains("/FontFile2", latin);
        Assert.Contains("/ToUnicode", latin);
        Assert.Contains("/Identity-H", latin);
        Assert.Matches(@"/BaseFont\s*/[A-Z]{6}\+", latin);
        Assert.DoesNotContain("/FontFile3", latin);   // Type0 CFF would not be a TrueType subset
    }

    [Fact]
    public void WritePdf_EmptyResultStillProducesAReadableDocument()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("empty.pdf");

        ReportWriter.WritePdf(new ReportRequest { Title = "空结果", Subtitle = "无数据", Format = ReportFormat.Pdf }, path);

        var bytes = File.ReadAllBytes(path);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(1, CountPages(bytes));
    }

    /// <summary>
    /// Leaves the two PDF samples in the test output folder so the bundled Python can
    /// be pointed at the exact bytes the library produced (pypdf is not installed, so
    /// the checker parses the objects, the embedded font and the ToUnicode CMap itself).
    /// </summary>
    [Fact]
    public void WritePdf_LeavesSamplesForTheBundledPythonCheck()
    {
        var shortPath = Path.Combine(TempWorkspace.ArtifactsDir, PdfSampleName);
        var longPath = Path.Combine(TempWorkspace.ArtifactsDir, PdfLongSampleName);

        ReportWriter.WritePdf(Sample(ReportFormat.Pdf), shortPath);
        ReportWriter.WritePdf(LongSample(ReportFormat.Pdf), longPath);

        Assert.True(new FileInfo(shortPath).Length > 0);
        Assert.True(new FileInfo(longPath).Length > 0);
    }

    // ---- fixtures and helpers ----------------------------------------------

    /// <summary>Three-row Chinese fixture: mixed widths, an empty cell and a comma.</summary>
    private static ReportRequest Sample(ReportFormat format) => new()
    {
        Title = "IP扫描结果",
        Subtitle = "网段 192.168.1.0/24",
        Headers = ["网络地址", "主机名", "状态", "备注"],
        Rows = new List<IReadOnlyList<string>>
        {
            new[] { "192.168.1.10", "gw-01", "在线", "默认网关" },
            new[] { "192.168.1.11", "有,逗号", "在线", "中文测试" },
            new[] { "192.168.1.100", "pc-2", "离线", "" },
        },
        Format = format,
    };

    /// <summary>300-row fixture: two PDF pages at the very least.</summary>
    private static ReportRequest LongSample(ReportFormat format) => new()
    {
        Title = "IP扫描结果",
        Subtitle = "网段 192.168.1.0/24",
        Headers = ["网络地址", "主机名", "MAC地址", "状态"],
        Rows = Enumerable.Range(1, 300)
            .Select(index => (IReadOnlyList<string>)new[]
            {
                $"192.168.{index / 250}.{index % 250 + 1}",
                $"host-{index:D3}",
                $"AA-BB-CC-{index % 90:D2}-{index % 70:D2}-{index % 60:D2}",
                index % 3 == 0 ? "离线" : "在线",
            })
            .ToList(),
        Format = format,
    };

    /// <summary>
    /// Counts the page objects in a PDF. PDFsharp writes "&lt;&lt;/Type/Page/MediaBox…"
    /// with no whitespace after the name, so the comparison is done on a
    /// whitespace-stripped copy — that also matches the "/Type /Page" form other
    /// producers emit.
    /// </summary>
    private static int CountPages(byte[] pdf)
    {
        var flat = Regex.Replace(Encoding.Latin1.GetString(pdf), @"\s+", string.Empty);
        return CountOccurrences(flat, "/Type/Page") - CountOccurrences(flat, "/Type/Pages");
    }

    /// <summary>Reads "/Type/Pages/Count N" — the page tree's own page counter.</summary>
    private static int PageTreeCount(byte[] pdf)
    {
        var flat = Regex.Replace(Encoding.Latin1.GetString(pdf), @"\s+", string.Empty);
        var match = Regex.Match(flat, @"/Type/Pages/Count(\d+)");
        Assert.True(match.Success, "no /Type/Pages/Count entry found in the PDF");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    /// Display width, implemented independently of the library: a CJK / fullwidth
    /// character occupies two terminal columns.
    /// </summary>
    private static int DisplayWidth(string text) => text.Sum(c =>
        c >= 0x1100 && (c <= 0x115F
            || c is (char)0x2329 or (char)0x232A
            || (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F)
            || (c >= 0xAC00 && c <= 0xD7A3)
            || (c >= 0xF900 && c <= 0xFAFF)
            || (c >= 0xFE30 && c <= 0xFE6F)
            || (c >= 0xFF00 && c <= 0xFF60)
            || (c >= 0xFFE0 && c <= 0xFFE6)) ? 2 : 1);

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);

        using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
