using Xunit;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using IPScaner.Core.Export;

namespace IPScaner.Core.Tests;

/// <summary>
/// Coverage for <see cref="TableExporter"/> — CSV escaping, the original file-name
/// convention, and the hand-built .xlsx package.
/// </summary>
public class TableExporterTests
{
    /// <summary>Name of the workbook kept in the test output folder for the openpyxl check.</summary>
    public const string OpenpyxlSampleName = "export-sample.xlsx";

    // ---- Escape ------------------------------------------------------------

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("192.168.1.10", "192.168.1.10")]
    [InlineData("有,逗号", "\"有,逗号\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("line1\r\nline2", "\"line1\r\nline2\"")]
    [InlineData("trailing\r", "\"trailing\r\"")]
    [InlineData(" leading and trailing ", " leading and trailing ")]
    public void Escape_QuotesPerRfc4180(string? value, string expected)
        => Assert.Equal(expected, TableExporter.Escape(value));

    [Fact]
    public void Escape_OnlyDoublesQuotesInsideAQuotedField()
    {
        Assert.Equal("\"a\"\"b\"", TableExporter.Escape("a\"b"));
        Assert.Equal("a'b", TableExporter.Escape("a'b"));
        Assert.Equal("\"a'b,c\"", TableExporter.Escape("a'b,c"));
        Assert.Equal("\"a\"\"b,c\"", TableExporter.Escape("a\"b,c"));
    }

    // ---- Column ------------------------------------------------------------

    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Column_MapsIndexToExcelColumnName(int index, string expected)
        => Assert.Equal(expected, TableExporter.Column(index));

    [Fact]
    public void Column_ProducesUniqueNamesForTheFirstThousandColumns()
    {
        var names = Enumerable.Range(0, 1000).Select(TableExporter.Column).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- SafeSheetName -----------------------------------------------------

    [Theory]
    [InlineData("Sheet1", "Sheet1")]
    [InlineData("扫描结果", "扫描结果")]
    [InlineData("a:b\\c/d?e*f[g]h", "abcdefgh")]
    [InlineData("2026:08:12", "20260812")]
    [InlineData("", "Sheet1")]
    [InlineData("   ", "Sheet1")]
    [InlineData(":", "Sheet1")]
    [InlineData("[]?*", "Sheet1")]
    [InlineData(null, "Sheet1")]
    public void SafeSheetName_StripsForbiddenCharacters(string? name, string expected)
        => Assert.Equal(expected, TableExporter.SafeSheetName(name));

    [Fact]
    public void SafeSheetName_TruncatesTo31Characters()
    {
        var exactly31 = new string('字', 31);
        var tooLong = new string('字', 40);

        Assert.Equal(31, TableExporter.SafeSheetName(exactly31).Length);
        Assert.Equal(exactly31, TableExporter.SafeSheetName(exactly31));
        Assert.Equal(31, TableExporter.SafeSheetName(tooLong).Length);
        Assert.Equal(new string('字', 31), TableExporter.SafeSheetName(tooLong));
    }

    // ---- BuildFileName -----------------------------------------------------

    [Fact]
    public void BuildFileName_UsesTheOriginalTimestampedConvention()
    {
        var stamp = new DateTime(2026, 8, 12, 17, 48, 28);

        Assert.Equal("IP批量扫描-20260812174828.csv", TableExporter.BuildFileName("IP批量扫描", ".csv", stamp));
        Assert.Equal("导出-20260812174828.xlsx", TableExporter.BuildFileName("导出", "xlsx", stamp));
        Assert.Equal("目标端口扫描-20260812174828.csv", TableExporter.BuildFileName("目标端口扫描", ".csv", stamp));
    }

    [Fact]
    public void BuildFileName_DefaultsToNow()
    {
        var name = TableExporter.BuildFileName("IP批量扫描", ".csv");

        Assert.Matches(new Regex(@"^IP批量扫描-\d{14}\.csv$"), name);
    }

    // ---- WriteCsv ----------------------------------------------------------

    [Fact]
    public void WriteCsv_WritesUtf8BomHeadersAndEscapedRows()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("out.csv");
        var headers = new[] { "IP地址", "备注" };
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "192.168.1.10", "有,逗号" },
            new[] { "192.168.1.11", "say \"hi\"" },
        };

        TableExporter.WriteCsv(path, headers, rows);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));

        var lines = File.ReadAllText(path, Encoding.UTF8).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("IP地址,备注", lines[0]);
        Assert.Equal("192.168.1.10,\"有,逗号\"", lines[1]);
        Assert.Equal("192.168.1.11,\"say \"\"hi\"\"\"", lines[2]);
    }

    [Fact]
    public void WriteCsv_CanOmitTheBom()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("no-bom.csv");

        TableExporter.WriteCsv(path, ["IP地址"], [], withBom: false);

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual((byte)0xEF, bytes[0]);
        Assert.Equal((byte)'I', bytes[0]);
    }

    [Fact]
    public void WriteCsv_CreatesMissingDirectories()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "exports", "2026", "out.csv");

        TableExporter.WriteCsv(path, ["A"], []);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WriteCsv_NewlinesInsideAValue_DoNotBreakTheQuoting()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("multiline.csv");

        TableExporter.WriteCsv(path, ["备注"], [new[] { "上\n下" }]);

        Assert.Contains("\"上\n下\"", File.ReadAllText(path, Encoding.UTF8));
    }

    // ---- WriteXlsx ---------------------------------------------------------

    [Fact]
    public void WriteXlsx_ProducesAValidPackageWithTheExpectedParts()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("sample.xlsx");

        WriteSampleWorkbook(path);

        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("[Content_Types].xml", names);
        Assert.Contains("_rels/.rels", names);
        Assert.Contains("xl/workbook.xml", names);
        Assert.Contains("xl/_rels/workbook.xml.rels", names);
        Assert.Contains("xl/worksheets/sheet1.xml", names);
    }

    [Fact]
    public void WriteXlsx_SheetXmlContainsHeadersValuesAndNumbers()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("sample.xlsx");

        WriteSampleWorkbook(path);

        var sheet = ReadEntry(path, "xl/worksheets/sheet1.xml");

        // Headers, inline strings and a real numeric cell.
        Assert.Contains("t=\"inlineStr\"", sheet);
        Assert.Contains(">IP地址<", sheet);
        Assert.Contains(">MAC地址<", sheet);
        Assert.Contains(">备注<", sheet);
        Assert.Contains(">192.168.1.10<", sheet);
        Assert.Contains("<c r=\"B2\"><v>80</v></c>", sheet);
        Assert.Contains("&lt;服务&gt;", sheet);   // XML-escaped value
        Assert.Contains("r=\"A1\"", sheet);

        // The part must be well-formed XML, not just a string that looks like it.
        var document = XDocument.Parse(sheet);
        Assert.Equal("worksheet", document.Root!.Name.LocalName);
        Assert.Equal(4, document.Root!.Element(XName.Get("sheetData", document.Root.Name.NamespaceName))!.Elements().Count());
    }

    [Fact]
    public void WriteXlsx_PartsAreAllWellFormedXml()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("sample.xlsx");

        WriteSampleWorkbook(path);

        foreach (var part in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels" })
        {
            var document = XDocument.Parse(ReadEntry(path, part));
            Assert.NotNull(document.Root);
        }
    }

    [Fact]
    public void WriteXlsx_WorkbookSheetNameIsSanitised()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("sample.xlsx");

        TableExporter.WriteXlsx(path, "扫描结果:2026*[?]", ["IP地址"], []);

        var workbook = ReadEntry(path, "xl/workbook.xml");
        Assert.Contains("name=\"扫描结果2026\"", workbook);
    }

    [Fact]
    public void WriteXlsx_OverwritesAnExistingFile()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("sample.xlsx");
        File.WriteAllText(path, "not a workbook");

        WriteSampleWorkbook(path);

        using var zip = ZipFile.OpenRead(path);
        Assert.Contains(zip.Entries, e => e.FullName == "xl/worksheets/sheet1.xml");
    }

    [Fact]
    public void WriteXlsx_CreatesMissingDirectories()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "exports", "out.xlsx");

        TableExporter.WriteXlsx(path, "Sheet1", ["A"], []);

        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// Keeps a canonical workbook in the test output folder so the bundled Python
    /// + openpyxl can be pointed at the exact bytes the library wrote. This is the
    /// out-of-process proof that Excel can open the file.
    /// </summary>
    [Fact]
    public void WriteXlsx_LeavesASampleForTheOpenpyxlRoundTrip()
    {
        var artifact = Path.Combine(TempWorkspace.ArtifactsDir, OpenpyxlSampleName);

        WriteSampleWorkbook(artifact);

        Assert.True(File.Exists(artifact));
    }

    // ---- helpers -----------------------------------------------------------

    private static void WriteSampleWorkbook(string path)
    {
        var headers = new[] { "IP地址", "MAC地址", "备注" };
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "192.168.1.10", "80", "HTTP" },
            new[] { "192.168.1.11", "445", "Microsoft-DS" },
            new[] { "192.168.1.12", "0", "未知 <服务>" },
        };

        TableExporter.WriteXlsx(path, "扫描结果", headers, rows);
    }

    private static string ReadEntry(string xlsxPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(xlsxPath);
        var entry = zip.GetEntry(entryName);
        Assert.NotNull(entry);

        using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
