using Xunit;
using System.Text;
using IPScaner.Core.Storage;

namespace IPScaner.Core.Tests;

/// <summary>
/// Coverage for <see cref="TextFileEncoding"/> — BOM detection plus the legacy
/// "ANSI" (GBK) heuristic the original tool relied on.
/// </summary>
public class TextFileEncodingTests
{
    [Fact]
    public void Detect_RecognisesTheUtf8Bom()
    {
        var detected = TextFileEncoding.Detect([0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b']);

        Assert.Equal(65001, detected.CodePage);
        Assert.Equal(3, detected.GetPreamble().Length);
    }

    [Fact]
    public void Detect_RecognisesUtf16LittleEndian()
    {
        var detected = TextFileEncoding.Detect([0xFF, 0xFE, (byte)'a', 0x00]);

        Assert.Equal(1200, detected.CodePage);
        Assert.Equal("utf-16", detected.WebName);
    }

    [Fact]
    public void Detect_RecognisesUtf16BigEndian()
    {
        var detected = TextFileEncoding.Detect([0xFE, 0xFF, 0x00, (byte)'a']);

        Assert.Equal(1201, detected.CodePage);
        Assert.Equal("utf-16BE", detected.WebName);
    }

    [Fact]
    public void Detect_BomWins_EvenWhenTheRestLooksLikeGbk()
    {
        // 0xD6 0xD0 is the GBK encoding of 中, but the BOM decides.
        var detected = TextFileEncoding.Detect([0xEF, 0xBB, 0xBF, 0xD6, 0xD0]);

        Assert.Equal(65001, detected.CodePage);
    }

    [Fact]
    public void Detect_GbkHeuristic_PicksCodePage936()
    {
        // "中文" as GBK: D6 D0 CE C4 — no BOM, lead byte >= 161.
        var detected = TextFileEncoding.Detect([0xD6, 0xD0, 0xCE, 0xC4]);

        Assert.Equal(936, detected.CodePage);
        Assert.Equal(TextFileEncoding.Gbk.CodePage, detected.CodePage);
    }

    [Theory]
    [InlineData(161, 936)]  // first byte in the heuristic window
    [InlineData(200, 936)]
    [InlineData(247, 936)]  // last byte in the window (inclusive)
    [InlineData(160, 65001)]
    [InlineData(248, 65001)]
    [InlineData(255, 65001)]
    public void Detect_GbkHeuristic_UsesThe161To247Window(byte probe, int expectedCodePage)
        => Assert.Equal(expectedCodePage, TextFileEncoding.Detect([(byte)'a', probe]).CodePage);

    [Fact]
    public void Detect_PlainAscii_IsUtf8WithoutBom()
    {
        var detected = TextFileEncoding.Detect(Encoding.ASCII.GetBytes("192.168.1.10=printer\r\n"));

        Assert.Equal(65001, detected.CodePage);
        Assert.Empty(detected.GetPreamble());
    }

    [Fact]
    public void Detect_EmptyInput_IsUtf8WithoutBom()
        => Assert.Equal(65001, TextFileEncoding.Detect([]).CodePage);

    [Fact]
    public void Detect_Utf8ChineseWithoutBom_IsMisdetectedAsGbk()
    {
        // DISCREPANCY / documented limitation (reported; library not modified):
        // the "lead byte >= 161" heuristic is the original's, and it cannot tell a
        // BOM-less UTF-8 Chinese file from GBK — the first byte of 中 (0xE4) is
        // inside the window, so such a file is decoded as GBK and renders as
        // mojibake. Only files *with* a BOM, or pure ASCII, are safe.
        var utf8Chinese = Encoding.UTF8.GetBytes("中文备注");

        Assert.Equal(936, TextFileEncoding.Detect(utf8Chinese).CodePage);
    }

    [Fact]
    public void Detect_FilePath_ReadsTheFile()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("memo.dat");
        File.WriteAllText(path, "192.168.1.10=打印机", TextFileEncoding.Utf8Bom);

        Assert.Equal(65001, TextFileEncoding.Detect(path).CodePage);
        Assert.Equal(3, TextFileEncoding.Detect(path).GetPreamble().Length);
    }

    [Fact]
    public void Detect_MissingFile_FallsBackInsteadOfThrowing()
    {
        using var workspace = new TempWorkspace();

        var detected = TextFileEncoding.Detect(workspace.PathFor("absent.dat"));

        Assert.NotNull(detected);
        Assert.Equal(Encoding.Default.CodePage, detected.CodePage);
    }

    [Fact]
    public void ReadAllLines_UsesTheDetectedEncoding()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("memo.dat");
        File.WriteAllText(path, "192.168.1.10=打印机\r\n00-1A-2B-3C-4D-5E=机房", TextFileEncoding.Utf8Bom);

        var lines = TextFileEncoding.ReadAllLines(path);

        Assert.Equal(["192.168.1.10=打印机", "00-1A-2B-3C-4D-5E=机房"], lines);
    }

    [Fact]
    public void ReadAllLines_ReadsAGbkFile()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("gbk.dat");
        File.WriteAllText(path, "192.168.1.10=打印机", TextFileEncoding.Gbk);

        var lines = TextFileEncoding.ReadAllLines(path);

        Assert.Equal(["192.168.1.10=打印机"], lines);
    }

    [Fact]
    public void Utf8Encodings_HaveTheExpectedPreambles()
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, TextFileEncoding.Utf8Bom.GetPreamble());
        Assert.Empty(TextFileEncoding.Utf8NoBom.GetPreamble());
        Assert.Equal(936, TextFileEncoding.Gbk.CodePage);
    }
}
