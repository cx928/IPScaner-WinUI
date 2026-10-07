using Xunit;
using System.Text;
using IPScaner.Core.Memo;

namespace IPScaner.Core.Tests;

/// <summary>
/// Coverage for <see cref="MemoStore"/> — 备注管理, including the on-disk format
/// that existing IPScanerMemo.dat files already use.
/// </summary>
public class MemoStoreTests
{
    // ---- ^v^ escaping round trip ------------------------------------------

    [Fact]
    public void SaveLoad_RoundTripsAMultiLineNote_UsingCaretVEscaping()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(MemoStore.FileName);
        var note = "第一行" + Environment.NewLine + "第二行" + Environment.NewLine + "第三行";

        var store = new MemoStore(path);
        store.Set("192.168.1.10", note);
        store.Save();

        var raw = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("192.168.1.10=第一行^v^第二行^v^第三行", raw);
        // One physical line per entry, even though the value has two newlines.
        Assert.Single(raw.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var reloaded = new MemoStore(path);
        reloaded.Load();

        Assert.Equal(note, reloaded.Lookup(null, "192.168.1.10"));
        Assert.Equal(1, reloaded.Count);
    }

    [Fact]
    public void Save_WritesUtf8WithBom_LikeTheOriginalWriter()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(MemoStore.FileName);

        var store = new MemoStore(path);
        store.Set("192.168.1.10", "打印机");
        store.Save();

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
    }

    [Fact]
    public void SaveLoad_NormalisesLoneLineFeedToThePlatformNewLine()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(MemoStore.FileName);

        var store = new MemoStore(path);
        store.Set("10.0.0.5", "上\n下");
        store.Save();

        var reloaded = new MemoStore(path);
        reloaded.Load();

        // Both "\r\n" and "\n" are stored as "^v^" and restored as Environment.NewLine,
        // so a note saved on Windows reloads byte-identically there. Documented here
        // because a Linux-authored "\n" note gains a CR when re-saved on Windows.
        Assert.Equal("上" + Environment.NewLine + "下", reloaded.Lookup(null, "10.0.0.5"));
    }

    [Fact]
    public void SaveLoad_OnlyTheFirstEqualsSignSplitsKeyAndValue()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(MemoStore.FileName);

        var store = new MemoStore(path);
        store.Set("192.168.1.11", "a=b=c");
        store.Save();

        var reloaded = new MemoStore(path);
        reloaded.Load();

        Assert.Equal("a=b=c", reloaded.Lookup(null, "192.168.1.11"));
    }

    [Fact]
    public void Load_SkipsCommentsBlankLinesAndLinesWithoutBothHalves()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(MemoStore.FileName);
        File.WriteAllText(
            path,
            string.Join(
                "\r\n",
                "# 这是注释",
                string.Empty,
                "   ",
                "192.168.1.10=打印机",
                "=没有键",
                "没有等号",
                "00-1A-2B-3C-4D-5E=三楼机房"),
            new UTF8Encoding(true));

        var store = new MemoStore(path);
        store.Load();

        Assert.Equal(2, store.Count);
        Assert.Equal("打印机", store.Lookup(null, "192.168.1.10"));
        Assert.Equal("三楼机房", store.Lookup("00-1A-2B-3C-4D-5E"));
    }

    [Fact]
    public void Load_MissingFile_LeavesTheStoreEmpty()
    {
        using var workspace = new TempWorkspace();

        var store = new MemoStore(workspace.PathFor(MemoStore.FileName));
        store.Load();

        Assert.Equal(0, store.Count);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Load_MigratesTheLegacyFileName()
    {
        using var workspace = new TempWorkspace();
        var current = workspace.PathFor(MemoStore.FileName);
        var legacy = workspace.PathFor(MemoStore.LegacyFileName);
        File.WriteAllText(legacy, "192.168.1.10=旧文件", new UTF8Encoding(true));

        var store = new MemoStore(current);
        store.Load();

        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(current));
        Assert.Equal("旧文件", store.Lookup(null, "192.168.1.10"));
    }

    // ---- lookup precedence -------------------------------------------------

    [Fact]
    public void Lookup_PrefersTheMacEntryOverTheIpEntry()
    {
        using var workspace = new TempWorkspace();
        var store = new MemoStore(workspace.PathFor(MemoStore.FileName));
        store.Set("192.168.1.10", "IP备注");
        store.Set("00-1A-2B-3C-4D-5E", "MAC备注");

        Assert.Equal("MAC备注", store.Lookup("00-1A-2B-3C-4D-5E", "192.168.1.10"));
        Assert.Equal("IP备注", store.Lookup("AA-BB-CC-DD-EE-FF", "192.168.1.10"));
        Assert.Equal("IP备注", store.Lookup(null, "192.168.1.10"));
        Assert.Equal(string.Empty, store.Lookup("11-22-33-44-55-66", null));
        Assert.Equal(string.Empty, store.Lookup(null, null));

        // Sharp edge (kept as the original behaved): the "mac" slot is a plain
        // dictionary lookup, so passing an IP there matches the IP entry too.
        Assert.Equal("IP备注", store.Lookup("192.168.1.10", null));
    }

    [Fact]
    public void Lookup_IsCaseInsensitiveForMacKeys()
    {
        using var workspace = new TempWorkspace();
        var store = new MemoStore(workspace.PathFor(MemoStore.FileName));
        store.Set("00-1A-2B-3C-4D-5E", "机房");

        Assert.Equal("机房", store.Lookup("00-1a-2b-3c-4d-5e"));
        Assert.Equal("机房", store.Lookup("00:1A:2B:3C:4D:5E"));
    }

    [Fact]
    public void Lookup_CanonicalisesDashAndColonMacShapesToOneKey()
    {
        // FIXED: the original compared raw text, so a note saved as "00-1A-..." was
        // NOT found when the caller passed "00:1A:...". ArpTable's row regex accepts
        // either separator, so the shape handed to Lookup depends on where the MAC
        // text came from — a memo imported with colons silently stopped matching.
        // MemoStore now canonicalises MAC keys to upper-case dash form.
        using var workspace = new TempWorkspace();
        var store = new MemoStore(workspace.PathFor(MemoStore.FileName));

        store.Set("00-1A-2B-3C-4D-5E", "机房");
        Assert.Equal("机房", store.Lookup("00-1A-2B-3C-4D-5E"));
        Assert.Equal("机房", store.Lookup("00:1A:2B:3C:4D:5E"));

        // Writing the colon shape updates the same entry rather than adding a second.
        store.Set("00:1a:2b:3c:4d:5e", "机房(改)");
        Assert.Single(store.Entries);
        Assert.Equal("机房(改)", store.Lookup("00-1A-2B-3C-4D-5E"));

        // And the canonical form is what gets persisted.
        Assert.True(store.Entries.ContainsKey("00-1A-2B-3C-4D-5E"));
    }

    // ---- mutation ----------------------------------------------------------

    [Fact]
    public void Set_WithEmptyOrNullValue_RemovesTheEntry()
    {
        using var workspace = new TempWorkspace();
        var store = new MemoStore(workspace.PathFor(MemoStore.FileName));

        store.Set("192.168.1.10", "备注");
        Assert.Equal(1, store.Count);

        store.Set("192.168.1.10", string.Empty);
        Assert.Equal(0, store.Count);
        Assert.False(store.TryGet("192.168.1.10", out _));
        Assert.Equal(string.Empty, store.Lookup(null, "192.168.1.10"));

        store.Set("192.168.1.11", "备注");
        store.Set("192.168.1.11", null);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Set_TrimsTheKey_AndIgnoresBlankKeys()
    {
        using var workspace = new TempWorkspace();
        var store = new MemoStore(workspace.PathFor(MemoStore.FileName));

        store.Set("  192.168.1.10  ", "备注");
        store.Set("   ", "没有键");
        store.Set(string.Empty, "没有键");

        Assert.Equal(1, store.Count);
        Assert.Equal("备注", store.Lookup(null, "192.168.1.10"));
    }

    [Fact]
    public void Remove_ClearAndReplaceAll_BehaveAsDocumented()
    {
        using var workspace = new TempWorkspace();
        var store = new MemoStore(workspace.PathFor(MemoStore.FileName));
        store.Set("192.168.1.10", "A");
        store.Set("192.168.1.11", "B");

        Assert.True(store.Remove("192.168.1.10"));
        Assert.False(store.Remove("192.168.1.10"));
        Assert.Equal(1, store.Count);

        store.ReplaceAll(MemoStore.ParseClipboard("192.168.1.20=第一台\n192.168.1.21=第二台\n裸键"));
        Assert.Equal(2, store.Count);
        Assert.Equal("第一台", store.Lookup(null, "192.168.1.20"));

        store.Clear();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void SaveThenLoad_KeepsEveryEntry()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(MemoStore.FileName);
        var store = new MemoStore(path);
        store.Set("192.168.1.10", "打印机");
        store.Set("192.168.1.11", "财务部");
        store.Set("00-1A-2B-3C-4D-5E", "机房");
        store.Save();

        var reloaded = new MemoStore(path);
        reloaded.Load();

        Assert.Equal(3, reloaded.Count);
        Assert.Equal("打印机", reloaded.Lookup(null, "192.168.1.10"));
        Assert.Equal("财务部", reloaded.Lookup(null, "192.168.1.11"));
        Assert.Equal("机房", reloaded.Lookup("00-1A-2B-3C-4D-5E"));
    }

    [Fact]
    public void Save_ToAnExplicitPath_LeavesTheConfiguredFileAlone()
    {
        using var workspace = new TempWorkspace();
        var configured = workspace.PathFor(MemoStore.FileName);
        var alternate = workspace.PathFor("copy.dat");

        var store = new MemoStore(configured);
        store.Set("192.168.1.10", "备注");
        store.Save(alternate);

        Assert.False(File.Exists(configured));
        Assert.True(File.Exists(alternate));
    }

    // ---- IsMacKey ----------------------------------------------------------

    [Theory]
    [InlineData("00-1A-2B-3C-4D-5E", true)]   // dash shape
    [InlineData("00:1A:2B:3C:4D:5E", true)]   // colon shape
    [InlineData("00-1a-2b-3c-4d-5e", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("192.168", false)]
    [InlineData("", false)]
    [InlineData("printer", false)]
    // FIXED: the original classified any dot-less token containing a dash as a MAC,
    // so a hyphenated host name was mistaken for a MAC key. The check is now a real
    // six-octet MAC pattern.
    [InlineData("file-server", false)]
    [InlineData("3C-7C-3F", false)]           // truncated, not a full MAC
    [InlineData("00-1A-2B-3C-4D-5E-6F", false)]
    public void IsMacKey_RequiresASixOctetMacShape(string key, bool expected)
        => Assert.Equal(expected, MemoStore.IsMacKey(key));

    [Theory]
    [InlineData("00-1A-2B-3C-4D-5E", "00-1A-2B-3C-4D-5E")]
    [InlineData("00:1a:2b:3c:4d:5e", "00-1A-2B-3C-4D-5E")]
    [InlineData("  192.168.1.10  ", "192.168.1.10")]  // non-MAC keys are only trimmed
    public void NormalizeKey_CanonicalisesMacsOnly(string input, string expected)
        => Assert.Equal(expected, MemoStore.NormalizeKey(input));

    // ---- ParseClipboard ----------------------------------------------------

    [Fact]
    public void ParseClipboard_AcceptsEqualsTabCommaAndSpaceForms()
    {
        var text = string.Join(
            "\n",
            "192.168.1.5=打印机",        // KEY=VALUE
            "192.168.1.6\t文件服务器",   // KEY<TAB>VALUE
            "192.168.1.7,会议室",        // KEY,VALUE
            "192.168.1.8 前台",          // KEY VALUE
            "# 注释行",
            "192.168.1.9",               // no value -> skipped
            "192.168.1.13=",             // empty value -> skipped
            "=缺少键",                   // no key -> skipped
            string.Empty,
            "192.168.1.12=a=b");         // only the first '=' splits

        var items = MemoStore.ParseClipboard(text);

        Assert.Equal(5, items.Count);
        Assert.Equal(new KeyValuePair<string, string>("192.168.1.5", "打印机"), items[0]);
        Assert.Equal(new KeyValuePair<string, string>("192.168.1.6", "文件服务器"), items[1]);
        Assert.Equal(new KeyValuePair<string, string>("192.168.1.7", "会议室"), items[2]);
        Assert.Equal(new KeyValuePair<string, string>("192.168.1.8", "前台"), items[3]);
        Assert.Equal(new KeyValuePair<string, string>("192.168.1.12", "a=b"), items[4]);
        Assert.DoesNotContain(items, i => i.Key == "192.168.1.9");
        Assert.DoesNotContain(items, i => i.Key.Length == 0);
    }

    [Fact]
    public void ParseClipboard_HandlesCrLfMacAndColonSeparatedValues()
    {
        var items = MemoStore.ParseClipboard("00-1A-2B-3C-4D-5E=机房\r\n192.168.1.10=财务部：三楼\r\n");

        Assert.Equal(2, items.Count);
        Assert.Equal("机房", items[0].Value);
        Assert.Equal("财务部：三楼", items[1].Value); // a full-width colon is not a separator
    }

    [Fact]
    public void ParseClipboard_TrimsSurroundingWhitespaceOfKeyAndValue()
    {
        var items = MemoStore.ParseClipboard("  192.168.1.10 = 打印机  ");

        Assert.Single(items);
        Assert.Equal("192.168.1.10", items[0].Key);
        Assert.Equal("打印机", items[0].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    [InlineData("# only a comment")]
    [InlineData("192.168.1.10")]
    public void ParseClipboard_ReturnsNothing_ForInputWithoutEntries(string text)
        => Assert.Empty(MemoStore.ParseClipboard(text));
}
