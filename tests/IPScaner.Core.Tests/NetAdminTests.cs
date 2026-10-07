using System.Text;
using System.Text.RegularExpressions;
using IPScaner.Core.Caching;
using IPScaner.Core.Memo;
using IPScaner.Core.Net;
using IPScaner.Core.Storage;
using Xunit;

namespace IPScaner.Core.Tests;

/// <summary>
/// hosts-file fixtures for <see cref="NetAdminHostsFileServiceTests"/>.
/// </summary>
/// <remarks>
/// The machine's real hosts file is copied into the scratch workspace whenever it
/// can be read (that is the realistic input the round-trip test must survive); the
/// embedded fixture below is the fallback, and it deliberately reproduces every
/// awkward shape the real file has: comment lines, tab indentation, a
/// whitespace-only line, a leading space before the address, IPv6 literals, the
/// same name mapped by several lines, one line carrying two names, a trailing
/// comment — and no newline at the end of the file.
/// </remarks>
internal static class NetAdminHostsFixture
{
    public const string RealHostsPath = @"C:\Windows\System32\drivers\etc\hosts";

    public const string MultiNameLine = "140.82.112.22   multi.example.com  second.example.com   # two names";

    public static readonly string[] Lines =
    [
        "# Copyright (c) 1993-2009 Microsoft Corp.",
        "#",
        "# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.",
        "#",
        "# For example:",
        "#",
        "#\t102.54.94.97     rhino.acme.com          # source server",
        "#\t 38.25.63.10     x.acme.com              # x client host",
        "",
        "# localhost name resolution is handled within DNS itself.",
        "#\t127.0.0.1       localhost",
        "#\t::1             localhost",
        " ",
        " 127.0.0.1 local.id.seewo.com",
        "160.30.231.176 map.xiaorin.cn",
        "127.0.0.1 240e:978:302:178:df44:5ca7:447f:4acb",
        "2404:6800:4005:81b::200e play.google.com",
        "140.82.121.3 github.com",
        "20.207.73.82 github.com",
        "20.27.177.113 github.com",
        "140.82.112.25   alive.github.com",
        MultiNameLine,
        "0.0.0.0 ads.example.com",
    ];

    /// <summary>The fixture with CRLF endings and <b>no</b> trailing newline.</summary>
    public static string Text { get; } = string.Join("\r\n", Lines);

    /// <summary>The same fixture in the (also common) "every line terminated" style.</summary>
    public static string TextWithTrailingNewline { get; } = Text + "\r\n";

    /// <summary>Writes the fixture into the workspace and returns its path.</summary>
    public static string Create(TempWorkspace workspace, string fileName, string? text = null, Encoding? encoding = null)
    {
        var path = workspace.PathFor(fileName);
        File.WriteAllText(path, text ?? Text, encoding ?? TextFileEncoding.Utf8NoBom);
        return path;
    }

    /// <summary>
    /// Copies <c>C:\Windows\System32\drivers\etc\hosts</c> byte-for-byte into the
    /// workspace. Returns false (and leaves the embedded fixture in place) when the
    /// real file cannot be read.
    /// </summary>
    public static bool TryCopyReal(TempWorkspace workspace, string fileName, out string path)
    {
        path = workspace.PathFor(fileName);
        try
        {
            File.WriteAllBytes(path, File.ReadAllBytes(RealHostsPath));
            return true;
        }
        catch
        {
            File.WriteAllText(path, Text, TextFileEncoding.Utf8NoBom);
            return false;
        }
    }

    /// <summary>Independent line splitter used to check the service's output (keeps no terminators).</summary>
    public static List<string> SplitContent(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            result.Add(text[start..i]);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < text.Length) result.Add(text[start..]);
        return result;
    }

    /// <summary>True when the line is an active mapping of <paramref name="hostname"/>.</summary>
    public static bool IsMappingOf(string line, string hostname)
    {
        var hash = line.IndexOf('#');
        var code = hash < 0 ? line : line[..hash];
        var tokens = code.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false;
        if (!IpMath.IsValidIPv4(tokens[0]) && !tokens[0].Contains(':')) return false;
        return tokens.Skip(1).Any(t => string.Equals(t, hostname, StringComparison.OrdinalIgnoreCase));
    }

    public static string Eol(byte[] bytes)
    {
        for (var i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && bytes[i + 1] == (byte)'\n') return "\r\n";
        }
        return bytes.Contains((byte)'\n') ? "\n" : "\r\n";
    }

    public static string[] BackupFiles(string directory, string fileName = "hosts") =>
        Directory.GetFiles(directory, $"{fileName}.backup-*");
}

/// <summary>
/// 一键管理 hosts 记录 — <see cref="HostsFileService"/>.
/// </summary>
/// <remarks>
/// Every mutating test runs against a copy. The real
/// <c>C:\Windows\System32\drivers\etc\hosts</c> is only ever <i>read</i>.
/// </remarks>
public class NetAdminHostsFileServiceTests
{
    // ---- read --------------------------------------------------------------

    [Fact]
    public void Read_EmbeddedFixture_ReturnsActiveMappingsInFileOrder()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        var entries = service.Read();

        Assert.NotEmpty(entries);
        Assert.Equal(entries.Select(e => e.LineNumber).OrderBy(n => n), entries.Select(e => e.LineNumber));

        // Commented-out examples and the commented localhost lines are not mappings.
        Assert.DoesNotContain(entries, e => e.Hostname is "localhost" or "rhino.acme.com" or "x.acme.com");

        var first = Assert.Single(entries, e => e.Hostname == "local.id.seewo.com");
        Assert.Equal("127.0.0.1", first.IP);
        Assert.Equal(14, first.LineNumber);           // 1-based, the whitespace-only line counts
        Assert.True(first.IsBlocking);                // 127.0.0.1 black-holes the name

        Assert.Equal(3, entries.Count(e => e.Hostname == "github.com"));

        // One physical line carrying two names yields one entry per name, same line.
        var multi = entries.Where(e => e.Hostname is "multi.example.com" or "second.example.com").ToList();
        Assert.Equal(2, multi.Count);
        Assert.Equal(multi[0].LineNumber, multi[1].LineNumber);
        Assert.Equal("140.82.112.22", multi[0].IP);
        Assert.Equal("two names", multi[0].Comment);

        // A blackhole address is flagged.
        Assert.True(Assert.Single(entries, e => e.Hostname == "ads.example.com").IsBlocking);
        Assert.True(Assert.Single(entries, e => e.Hostname == "240e:978:302:178:df44:5ca7:447f:4acb").IsBlocking);

        // IPv6 mappings are reported too.
        Assert.Contains(entries, e => e.Hostname == "play.google.com" && e.IP.StartsWith("2404:", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_RealHostsCopy_ReturnsMappingsWithoutThrowing()
    {
        using var workspace = new TempWorkspace();
        var copiedReal = NetAdminHostsFixture.TryCopyReal(workspace, "hosts", out var path);

        var entries = new HostsFileService(path).Read();

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(e.LineNumber >= 1));
        Assert.All(entries, e => Assert.DoesNotContain("#", e.Hostname));
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.IP)));
        Assert.Equal(entries.Select(e => e.LineNumber).OrderBy(n => n), entries.Select(e => e.LineNumber));

        // Documents which fixture the fidelity tests actually exercised.
        Assert.True(copiedReal || File.Exists(path));
    }

    [Fact]
    public void RealHostsFile_IsAvailableSoTheFidelityTestsUseIt()
    {
        using var workspace = new TempWorkspace();

        var copied = NetAdminHostsFixture.TryCopyReal(workspace, "hosts", out var path);

        Assert.True(copied, $"无法读取 {NetAdminHostsFixture.RealHostsPath}，保真测试只能使用内置样本。");
        Assert.Equal(File.ReadAllBytes(NetAdminHostsFixture.RealHostsPath), File.ReadAllBytes(path));
        Assert.NotEmpty(new HostsFileService(path).Read());
    }

    // ---- add / round-trip fidelity ----------------------------------------

    [Fact]
    public void Add_RealHostsCopy_KeepsEveryOriginalLineAndByte()
    {
        using var workspace = new TempWorkspace();
        NetAdminHostsFixture.TryCopyReal(workspace, "hosts", out var path);
        var service = new HostsFileService(path);

        var original = File.ReadAllBytes(path);
        var originalLines = NetAdminHostsFixture.SplitContent(Encoding.UTF8.GetString(original));
        var eol = NetAdminHostsFixture.Eol(original);
        var endedWithNewline = original.Length > 0 && original[^1] is (byte)'\n' or (byte)'\r';

        var result = service.Add("10.20.30.40", "netadmin-fidelity.local");

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.Changed);

        var after = File.ReadAllBytes(path);

        // 1. The original bytes are an exact prefix of the new file: no original byte
        //    moved, changed or was re-encoded.
        Assert.True(after.AsSpan().StartsWith(original), "原有字节必须原样保留在新文件开头");

        // 2. Every original line is still present, in order and byte-identical.
        var afterLines = NetAdminHostsFixture.SplitContent(Encoding.UTF8.GetString(after));
        Assert.Equal(originalLines.Count + 1, afterLines.Count);
        for (var i = 0; i < originalLines.Count; i++)
        {
            Assert.Equal(originalLines[i], afterLines[i]);
        }
        Assert.Equal("10.20.30.40 netadmin-fidelity.local", afterLines[^1]);

        // 3. Exactly one line was appended, and the file's EOF style is preserved.
        var tail = Encoding.UTF8.GetString(after[original.Length..]);
        Assert.Equal(endedWithNewline ? $"10.20.30.40 netadmin-fidelity.local{eol}" : $"{eol}10.20.30.40 netadmin-fidelity.local", tail);
        Assert.Equal(endedWithNewline, after[^1] is (byte)'\n' or (byte)'\r');
    }

    [Fact]
    public void Add_EmbeddedFixture_KeepsWhitespaceOnlyAndTabLines()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        Assert.True(service.Add("10.20.30.41", "netadmin-tabs.local").Success);

        var text = File.ReadAllText(path);
        Assert.Contains("#\t102.54.94.97     rhino.acme.com          # source server", text);
        Assert.Contains("#\t 38.25.63.10     x.acme.com              # x client host", text);
        Assert.Contains("\r\n \r\n", text);                       // the whitespace-only line survived
        Assert.Contains(" 127.0.0.1 local.id.seewo.com\r\n", text); // leading space kept
        Assert.EndsWith("\r\n10.20.30.41 netadmin-tabs.local", text); // no trailing newline added
    }

    [Fact]
    public void Add_ToAFileThatEndsWithANewline_KeepsThatStyle()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts", NetAdminHostsFixture.TextWithTrailingNewline);
        var service = new HostsFileService(path);

        Assert.True(service.Add("10.20.30.42", "netadmin-eol.local").Success);

        Assert.EndsWith("10.20.30.42 netadmin-eol.local\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Add_MappingThatAlreadyExists_IsIdempotentAndTakesNoBackup()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        Assert.True(service.Add("10.20.30.43", "netadmin-idem.local").Success);
        var afterFirst = File.ReadAllBytes(path);
        var backupsAfterFirst = NetAdminHostsFixture.BackupFiles(workspace.Root);

        var again = service.Add("10.20.30.43", "netadmin-idem.local");

        Assert.True(again.Success, again.Message);
        Assert.Equal(0, again.Changed);
        Assert.Contains("未做修改", again.Message);
        Assert.Equal(afterFirst, File.ReadAllBytes(path));
        Assert.Equal(backupsAfterFirst.Length, NetAdminHostsFixture.BackupFiles(workspace.Root).Length);
    }

    [Fact]
    public void Add_ForANameMappedElsewhere_RepointsTheFirstLineInsteadOfAppending()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        var result = service.Add("203.0.113.9", "github.com");

        Assert.True(result.Success, result.Message);
        var lines = NetAdminHostsFixture.SplitContent(File.ReadAllText(path));
        Assert.Equal(NetAdminHostsFixture.Lines.Length, lines.Count);   // nothing appended
        Assert.Equal("203.0.113.9 github.com", lines[17]);
        Assert.Equal("20.207.73.82 github.com", lines[18]);              // the later duplicates stay
        Assert.Equal("20.27.177.113 github.com", lines[19]);

        // The first match wins in a hosts file, which is the one that had to change.
        Assert.Equal("203.0.113.9", service.Read().First(e => e.Hostname == "github.com").IP);
    }

    [Fact]
    public void Add_ForANameOnAMultiNameLine_InsertsOnTopAndLeavesTheOtherNameMapped()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        var result = service.Add("203.0.113.10", "second.example.com");

        Assert.True(result.Success, result.Message);
        var entries = service.Read();
        Assert.Equal("203.0.113.10", entries.First(e => e.Hostname == "second.example.com").IP);
        Assert.Equal("140.82.112.22", entries.First(e => e.Hostname == "multi.example.com").IP);
        Assert.Contains(NetAdminHostsFixture.MultiNameLine, File.ReadAllText(path));
    }

    [Fact]
    public void Add_WithComment_WritesAndReadsItBack()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts-bom", encoding: TextFileEncoding.Utf8Bom);
        var service = new HostsFileService(path);

        Assert.True(service.Add("10.0.0.5", "c.example.com", "机房服务器").Success);

        var entry = Assert.Single(service.Read(), e => e.Hostname == "c.example.com");
        Assert.Equal("机房服务器", entry.Comment);
        Assert.EndsWith("\r\n10.0.0.5 c.example.com # 机房服务器", File.ReadAllText(path, TextFileEncoding.Utf8Bom).TrimStart('\uFEFF'));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path).Take(3));
    }

    // ---- backup ------------------------------------------------------------

    [Fact]
    public void Backup_IsTakenBesideTheFileAndHoldsTheOriginalBytes()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var original = File.ReadAllBytes(path);

        Assert.True(new HostsFileService(path).Add("10.20.30.44", "netadmin-backup.local").Success);

        var backup = Assert.Single(NetAdminHostsFixture.BackupFiles(workspace.Root));
        Assert.Equal(workspace.Root, Path.GetDirectoryName(backup));
        Assert.Matches(@"^hosts\.backup-\d{8}-\d{6}(-\d+)?$", Path.GetFileName(backup));
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    [Fact]
    public void Backup_ReturnsThePathAndDoesNotThrowForAMissingFile()
    {
        using var workspace = new TempWorkspace();
        var service = new HostsFileService(workspace.PathFor("absent-hosts"));

        Assert.Null(service.Backup());
    }

    [Fact]
    public void Backup_TwiceWithinASecond_KeepsBothCopies()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");

        var first = new HostsFileService(path).Backup();
        var second = new HostsFileService(path).Backup();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(2, NetAdminHostsFixture.BackupFiles(workspace.Root).Length);
    }

    // ---- remove / update ---------------------------------------------------

    [Fact]
    public void Remove_DeletesEveryMappingAndNothingElse()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        var result = service.Remove("github.com");

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, result.Changed);
        Assert.DoesNotContain(service.Read(), e => e.Hostname == "github.com");
        Assert.Contains(service.Read(), e => e.Hostname == "alive.github.com");

        // The only difference from the fixture is the three removed lines.
        var expected = string.Join("\r\n", NetAdminHostsFixture.Lines.Where(l => !NetAdminHostsFixture.IsMappingOf(l, "github.com")));
        Assert.Equal(expected, File.ReadAllText(path));
    }

    [Fact]
    public void Remove_UnknownName_SucceedsWithoutWriting()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var original = File.ReadAllBytes(path);

        var result = new HostsFileService(path).Remove("not-in-the-file.example");

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.Changed);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(NetAdminHostsFixture.BackupFiles(workspace.Root));
    }

    [Fact]
    public void Remove_OnlyTakesTheMatchingNameOffAMultiNameLine()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        var result = service.Remove("multi.example.com");

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.Changed);
        Assert.Contains("140.82.112.22   second.example.com   # two names", File.ReadAllText(path));
        Assert.DoesNotContain(service.Read(), e => e.Hostname == "multi.example.com");
        Assert.Contains(service.Read(), e => e.Hostname == "second.example.com");
    }

    [Fact]
    public void RemoveMany_RemovesSeveralNamesWithOneBackup()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        var result = service.RemoveMany(["github.com", "1440.1.1.1", "ads.example.com"]);

        Assert.True(result.Success, result.Message);
        Assert.Equal(4, result.Changed);            // 3 github.com + ads.example.com
        Assert.Single(NetAdminHostsFixture.BackupFiles(workspace.Root));
        var entries = service.Read();
        Assert.DoesNotContain(entries, e => e.Hostname is "github.com" or "ads.example.com");
        Assert.Contains(entries, e => e.Hostname == "map.xiaorin.cn");
    }

    [Fact]
    public void Update_ChangesOnlyTheTargetLine()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        var result = service.Update("github.com", "198.51.100.7");

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.Changed);
        var lines = NetAdminHostsFixture.SplitContent(File.ReadAllText(path));
        Assert.Equal(NetAdminHostsFixture.Lines.Length, lines.Count);
        Assert.Equal("198.51.100.7 github.com", lines[17]);
        Assert.Equal("198.51.100.7", service.Read().First(e => e.Hostname == "github.com").IP);

        // Every other line is untouched, byte for byte.
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == 17) continue;
            Assert.Equal(NetAdminHostsFixture.Lines[i], lines[i]);
        }
    }

    [Fact]
    public void Update_ToTheSameAddress_WritesNothing()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var original = File.ReadAllBytes(path);

        var result = new HostsFileService(path).Update("github.com", "140.82.121.3");

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.Changed);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(NetAdminHostsFixture.BackupFiles(workspace.Root));
    }

    [Fact]
    public void Update_UnknownName_FailsWithoutWriting()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var original = File.ReadAllBytes(path);

        var result = new HostsFileService(path).Update("nothing-here.example", "10.0.0.9");

        Assert.False(result.Success);
        Assert.Equal(0, result.Changed);
        Assert.Contains("没有", result.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(NetAdminHostsFixture.BackupFiles(workspace.Root));
    }

    // ---- validation --------------------------------------------------------

    [Theory]
    [InlineData("999.1.1.1", "host.example.com")]   // octet out of range
    [InlineData("10.0.0", "host.example.com")]      // too few octets
    [InlineData("10.0.0.1.5", "host.example.com")]  // too many octets
    [InlineData("", "host.example.com")]            // no address
    [InlineData("10.0.0.1", "has space.example")]   // whitespace in the name
    [InlineData("10.0.0.1", "has\ttab.example")]    // tab in the name
    [InlineData("10.0.0.1", "has#hash.example")]    // '#' would start a comment
    [InlineData("10.0.0.1", "")]
    [InlineData("10.0.0.1", "   ")]
    public void Add_InvalidInput_IsRefusedWithoutTouchingTheFile(string ip, string hostname)
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var original = File.ReadAllBytes(path);

        var result = new HostsFileService(path).Add(ip, hostname);

        Assert.False(result.Success);
        Assert.Equal(0, result.Changed);
        Assert.NotEmpty(result.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(NetAdminHostsFixture.BackupFiles(workspace.Root));
    }

    [Fact]
    public void Update_InvalidAddress_IsRefusedWithoutTouchingTheFile()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var original = File.ReadAllBytes(path);

        var result = new HostsFileService(path).Update("github.com", "not-an-ip");

        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(NetAdminHostsFixture.BackupFiles(workspace.Root));
    }

    [Fact]
    public void AddMany_ValidatesEveryEntryBeforeWritingAnything()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var original = File.ReadAllBytes(path);

        var result = new HostsFileService(path).AddMany([("10.0.0.1", "a.example"), ("999.9.9.9", "b.example")]);

        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(NetAdminHostsFixture.BackupFiles(workspace.Root));
    }

    [Fact]
    public void AddMany_AddsEverythingWithASingleBackup()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        var result = service.AddMany([("10.0.0.1", "a.example"), ("10.0.0.2", "b.example"), ("10.0.0.1", "a.example")]);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Changed);
        Assert.Single(NetAdminHostsFixture.BackupFiles(workspace.Root));
        var entries = service.Read();
        Assert.Equal("10.0.0.1", Assert.Single(entries, e => e.Hostname == "a.example").IP);
        Assert.Equal("10.0.0.2", Assert.Single(entries, e => e.Hostname == "b.example").IP);
    }

    // ---- missing / unreadable / unwritable files ---------------------------

    [Fact]
    public void MissingFile_IsHandledGracefully()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("absent-hosts");
        var service = new HostsFileService(path);

        Assert.Empty(service.Read());
        Assert.Null(service.Backup());

        var result = service.Add("10.1.2.3", "fresh.example");

        Assert.True(result.Success, result.Message);
        Assert.Equal("10.1.2.3 fresh.example", File.ReadAllText(path).TrimEnd('\r', '\n'));
        Assert.Empty(NetAdminHostsFixture.BackupFiles(workspace.Root, "absent-hosts"));
    }

    [Fact]
    public void UnreadableFile_IsHandledGracefully()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var original = File.ReadAllBytes(path);
        var service = new HostsFileService(path);

        using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(service.Read());

            var result = service.Add("10.1.2.3", "locked.example");

            Assert.False(result.Success);
            Assert.Contains("无法读取", result.Message);
        }

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReadOnlyFile_ReturnsTheExactAdministratorMessage()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts");
        var service = new HostsFileService(path);

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = service.Add("10.1.2.3", "readonly.example");

            Assert.False(result.Success);
            Assert.Equal("需要管理员权限才能修改 hosts 文件", result.Message);
            Assert.Equal(HostsFileService.AdminRequiredMessage, result.Message);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // ---- encoding preservation --------------------------------------------

    [Fact]
    public void Add_KeepsTheUtf8Bom()
    {
        using var workspace = new TempWorkspace();
        var path = NetAdminHostsFixture.Create(workspace, "hosts", encoding: TextFileEncoding.Utf8Bom);
        var original = File.ReadAllBytes(path);

        Assert.True(new HostsFileService(path).Add("10.1.2.4", "bom.example").Success);

        var after = File.ReadAllBytes(path);
        Assert.True(after.AsSpan().StartsWith(original));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, after.Take(3));
        Assert.Contains("10.1.2.4 bom.example", new HostsFileService(path).Read().Select(e => $"{e.IP} {e.Hostname}"));
    }

    [Fact]
    public void Add_ToAGbkFile_KeepsTheGbkComment()
    {
        using var workspace = new TempWorkspace();
        var text = "# 中文注释\r\n127.0.0.1 localhost\r\n192.168.1.9 打印机";
        var path = NetAdminHostsFixture.Create(workspace, "hosts", text, TextFileEncoding.Gbk);
        var original = File.ReadAllBytes(path);
        var service = new HostsFileService(path);

        Assert.Equal("打印机", service.Read().First(e => e.IP == "192.168.1.9").Hostname);

        Assert.True(service.Add("192.168.1.10", "扫描仪").Success);

        var after = File.ReadAllBytes(path);
        Assert.True(after.AsSpan().StartsWith(original));                    // GBK bytes untouched
        Assert.Equal("扫描仪", service.Read().First(e => e.IP == "192.168.1.10").Hostname);
    }

    [Fact]
    public void Add_ToABomlessUtf8ChineseFile_KeepsTheTextReadable()
    {
        // The shared encoding heuristic calls this file GBK; the service must not,
        // or the comment comes back as mojibake.
        using var workspace = new TempWorkspace();
        var text = "# 中文注释 ’\r\n127.0.0.1 localhost";
        var path = NetAdminHostsFixture.Create(workspace, "hosts", text, TextFileEncoding.Utf8NoBom);
        var original = File.ReadAllBytes(path);
        var service = new HostsFileService(path);

        Assert.True(service.Add("192.168.1.11", "新主机").Success);

        var after = File.ReadAllBytes(path);
        Assert.True(after.AsSpan().StartsWith(original));
        Assert.Equal("新主机", service.Read().First(e => e.IP == "192.168.1.11").Hostname);
        Assert.Contains("中文注释", Encoding.UTF8.GetString(after));
    }

    // ---- defaults and DNS flush -------------------------------------------

    [Fact]
    public void DefaultFilePath_IsTheWindowsHostsFile()
    {
        var service = new HostsFileService();

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts"), service.FilePath);
        Assert.EndsWith(@"drivers\etc\hosts", service.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FilePath_IsUsed() => Assert.Equal(
        @"D:\somewhere\hosts", new HostsFileService(@"D:\somewhere\hosts").FilePath);

    [Fact]
    public async Task FlushDnsCache_NeverThrows()
    {
        // Runs the real ipconfig /flushdns; only "does not throw" is asserted.
        await HostsFileService.FlushDnsCacheAsync(CancellationToken.None);
        await HostsFileService.FlushDnsCacheAsync();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await HostsFileService.FlushDnsCacheAsync(cancelled.Token);
    }
}

/// <summary>一键更换 DNS — <see cref="DnsConfigurator"/>. Nothing here changes this machine's DNS.</summary>
public class NetAdminDnsConfiguratorTests
{
    private const string MissingAdapter = "netadmin-不存在的网卡-0000";

    [Fact]
    public void Presets_ContainEveryRequiredProvider()
    {
        var presets = DnsConfigurator.Presets;

        Assert.True(presets.Count >= 7);
        Assert.Same(DnsConfigurator.Automatic, presets[0]);
        AssertPreset("自动获取(DHCP)", "", "");
        AssertPreset("阿里 DNS", "223.5.5.5", "223.6.6.6");
        AssertPreset("腾讯 DNSPod", "119.29.29.29", "182.254.116.116");
        AssertPreset("114 DNS", "114.114.114.114", "114.114.115.115");
        AssertPreset("百度 DNS", "180.76.76.76", "");
        AssertPreset("Cloudflare", "1.1.1.1", "1.0.0.1");
        AssertPreset("Google", "8.8.8.8", "8.8.4.4");
        return;

        void AssertPreset(string name, string primary, string secondary)
        {
            var preset = Assert.Single(presets, p => p.Name == name);
            Assert.Equal(primary, preset.Primary);
            Assert.Equal(secondary, preset.Secondary);
            Assert.False(string.IsNullOrWhiteSpace(preset.Note));
            Assert.Matches("[\u4e00-\u9fff]", preset.Note);           // every note is Chinese
            Assert.True(preset.Note.Length <= 20, "备注应简短");
        }
    }

    [Fact]
    public void Presets_AddressesAreValidAndNamesAreUnique()
    {
        Assert.Equal(DnsConfigurator.Presets.Count, DnsConfigurator.Presets.Select(p => p.Name).Distinct().Count());

        foreach (var preset in DnsConfigurator.Presets)
        {
            if (!string.IsNullOrEmpty(preset.Primary)) Assert.True(IpMath.IsValidIPv4(preset.Primary), preset.Name);
            if (!string.IsNullOrEmpty(preset.Secondary)) Assert.True(IpMath.IsValidIPv4(preset.Secondary), preset.Name);
        }

        Assert.True(DnsConfigurator.IsAutomatic(DnsConfigurator.Automatic));
        Assert.False(DnsConfigurator.IsAutomatic(DnsConfigurator.Presets[1]));
    }

    [Fact]
    public async Task Apply_UnknownAdapter_FailsWithoutRunningNetsh()
    {
        var result = await new DnsConfigurator().ApplyAsync(MissingAdapter, DnsConfigurator.Presets[1]);

        Assert.False(result.Success);
        Assert.Equal(NetworkConfigurator.NotRunExitCode, result.ExitCode);   // nothing was spawned
        Assert.Contains(MissingAdapter, result.Message);
    }

    [Fact]
    public async Task Apply_AutomaticToUnknownAdapter_FailsWithoutRunningNetsh()
    {
        var result = await new DnsConfigurator().ApplyAsync(MissingAdapter, DnsConfigurator.Automatic);

        Assert.False(result.Success);
        Assert.Equal(NetworkConfigurator.NotRunExitCode, result.ExitCode);
    }

    [Fact]
    public async Task Reset_UnknownAdapter_FailsWithoutRunningNetsh()
    {
        var result = await new DnsConfigurator().ResetToAutomaticAsync(MissingAdapter);

        Assert.False(result.Success);
        Assert.Equal(NetworkConfigurator.NotRunExitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\"adapter")]
    public async Task Apply_InvalidAdapterName_FailsWithoutRunningNetsh(string adapter)
    {
        var result = await new DnsConfigurator().ApplyAsync(adapter, DnsConfigurator.Presets[2]);

        Assert.False(result.Success);
        Assert.Equal(NetworkConfigurator.NotRunExitCode, result.ExitCode);
    }

    [Fact]
    public async Task Apply_UnknownAdapterWithACustomPreset_FailsWithoutRunningNetsh()
    {
        var result = await new DnsConfigurator().ApplyAsync(MissingAdapter, new DnsPreset("坏方案", "1.2.3.999", "", "测试"));

        Assert.False(result.Success);
        Assert.Equal(NetworkConfigurator.NotRunExitCode, result.ExitCode);
    }

    [Fact]
    public async Task ApplyCustom_UnknownAdapter_FailsWithoutRunningNetsh()
    {
        var result = await new DnsConfigurator().ApplyCustomAsync(MissingAdapter, ["223.5.5.5"]);

        Assert.False(result.Success);
        Assert.Equal(NetworkConfigurator.NotRunExitCode, result.ExitCode);
    }

    [Fact]
    public async Task ApplyCustom_InvalidOrMissingServers_AreRefusedBeforeNetshStarts()
    {
        // A real adapter is used on purpose: the refusal has to come from validation,
        // not from the adapter lookup, so that netsh is never started.
        var adapter = DnsConfigurator.GetAdapterNames().FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(adapter));

        var configurator = new DnsConfigurator();
        var empty = await configurator.ApplyCustomAsync(adapter!, []);
        var blank = await configurator.ApplyCustomAsync(adapter!, ["", "  "]);
        var malformed = await configurator.ApplyCustomAsync(adapter!, ["223.5.5.5", "999.9.9.9"]);
        var secondMalformed = await configurator.ApplyCustomAsync(adapter!, ["223.5.5.5", "not-an-ip"]);

        Assert.False(empty.Success);
        Assert.False(blank.Success);
        Assert.False(malformed.Success);
        Assert.False(secondMalformed.Success);
        Assert.Equal(NetworkConfigurator.NotRunExitCode, empty.ExitCode);
        Assert.Equal(NetworkConfigurator.NotRunExitCode, blank.ExitCode);
        Assert.Equal(NetworkConfigurator.NotRunExitCode, malformed.ExitCode);
        Assert.Equal(NetworkConfigurator.NotRunExitCode, secondMalformed.ExitCode);
        Assert.Contains("第 2 个DNS地址", malformed.Message);
    }

    [Fact]
    public async Task GetCurrent_UnknownAdapter_ReturnsAnEmptyList()
    {
        var servers = await new DnsConfigurator().GetCurrentAsync(MissingAdapter);

        Assert.Empty(servers);
        Assert.Empty(await new DnsConfigurator().GetCurrentAsync("   "));
    }

    [Fact]
    public async Task GetCurrent_RealAdapter_ReturnsOnlyIPv4Addresses()
    {
        var adapter = DnsConfigurator.GetAdapterNames().FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(adapter));

        var servers = await new DnsConfigurator().GetCurrentAsync(adapter!);

        Assert.All(servers, s => Assert.True(IpMath.IsValidIPv4(s), s));
        Assert.Equal(servers.Count, servers.Distinct().Count());
    }

    [Fact]
    public async Task GetCurrent_HonoursAnAlreadyCancelledToken()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var servers = await new DnsConfigurator().GetCurrentAsync(DnsConfigurator.GetAdapterNames().First(), cancelled.Token);

        Assert.Empty(servers);
    }

    [Fact]
    public void AdapterExists_MatchesTheMachine()
    {
        var adapter = DnsConfigurator.GetAdapterNames().FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(adapter));

        Assert.True(DnsConfigurator.AdapterExists(adapter));
        Assert.True(DnsConfigurator.AdapterExists(adapter!.ToUpperInvariant()));
        Assert.True(DnsConfigurator.AdapterExists($"  {adapter}  "));
        Assert.False(DnsConfigurator.AdapterExists(MissingAdapter));
        Assert.False(DnsConfigurator.AdapterExists(null));
        Assert.False(DnsConfigurator.AdapterExists(""));
    }

    [Fact]
    public void GetAdapterNames_IsNotEmptyOnThisMachine() => Assert.NotEmpty(DnsConfigurator.GetAdapterNames());
}

/// <summary>SSH / RDP quick connect — <see cref="RemoteLauncher"/>. No session is ever opened here.</summary>
public class NetAdminRemoteLauncherTests
{
    private static readonly string[] Injected =
    [
        "192.168.1.1 & calc",
        "192.168.1.1&&calc",
        "192.168.1.1 | calc",
        "192.168.1.1;calc",
        "192.168.1.1 > out.txt",
        "192.168.1.1\" /k calc",
        "192.168.1.1 /k calc",
        "host name",
        "$(calc)",
        "`calc`",
        "%TEMP%",
        "192.168.1.1\0",
        "",
        "   ",
    ];

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.255")]
    [InlineData("nas.local")]
    [InlineData("server-01.lan.example.com")]
    [InlineData("my-pc")]
    [InlineData("A1B2C3")]
    public void IsValidHost_AcceptsAddressesAndPlainNames(string host)
    {
        Assert.True(RemoteLauncher.IsValidHost(host));
        Assert.True(RemoteLauncher.IsValidHost($"  {host}  "));
    }

    [Fact]
    public void IsValidHost_RefusesAnythingThatCouldReachACommandLine()
    {
        foreach (var host in Injected) Assert.False(RemoteLauncher.IsValidHost(host), host);
        Assert.False(RemoteLauncher.IsValidHost(null));
        Assert.False(RemoteLauncher.IsValidHost(new string('a', 254)));
    }

    [Theory]
    [InlineData("administrator")]
    [InlineData("DOMAIN\\administrator")]
    [InlineData("user@example.com")]
    [InlineData("user.name-01")]
    public void IsValidUser_AcceptsDomainAndUpnForms(string user) => Assert.True(RemoteLauncher.IsValidUser(user));

    [Theory]
    [InlineData("bad user")]
    [InlineData("user&calc")]
    [InlineData("user\"x")]
    [InlineData("user|x")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("user;calc")]
    public void IsValidUser_RefusesUnsafeNames(string user) => Assert.False(RemoteLauncher.IsValidUser(user));

    [Fact]
    public void LaunchSsh_RefusesAnInjectedHost()
    {
        var launcher = new RemoteLauncher();

        foreach (var host in Injected)
        {
            Assert.False(launcher.LaunchSsh(host), host);
            Assert.False(launcher.LaunchSsh(host, "administrator"), host);
        }
    }

    [Fact]
    public void LaunchSsh_RefusesAnUnsafeUserOrPort()
    {
        var launcher = new RemoteLauncher();

        Assert.False(launcher.LaunchSsh("192.168.1.10", "bad user"));
        Assert.False(launcher.LaunchSsh("192.168.1.10", "user&calc"));
        Assert.False(launcher.LaunchSsh("192.168.1.10", "administrator", 0));
        Assert.False(launcher.LaunchSsh("192.168.1.10", "administrator", -1));
        Assert.False(launcher.LaunchSsh("192.168.1.10", "administrator", 65536));
    }

    [Fact]
    public void LaunchRdp_RefusesAnInjectedHostOrUser()
    {
        var launcher = new RemoteLauncher();

        foreach (var host in Injected) Assert.False(launcher.LaunchRdp(host), host);
        Assert.False(launcher.LaunchRdp("192.168.1.10", "bad user"));
        Assert.False(launcher.LaunchRdpFile("192.168.1.10", "user|calc"));
    }

    [Fact]
    public void LaunchRdpFile_RefusalsWriteNothingToTemp()
    {
        var launcher = new RemoteLauncher();
        var before = TempRdpFiles();

        Assert.False(launcher.LaunchRdpFile("192.168.1.1 & calc"));
        Assert.False(launcher.LaunchRdpFile("192.168.1.1;calc", "admin"));
        Assert.False(launcher.LaunchRdpFile("192.168.1.10", "bad user"));
        Assert.False(launcher.LaunchRdpFile(""));
        Assert.False(launcher.LaunchRdp(null!));

        Assert.True(before.SetEquals(TempRdpFiles()));
    }

    [Fact]
    public void BuildRdpFileContent_WritesTheRequiredKeys()
    {
        var content = RemoteLauncher.BuildRdpFileContent("192.168.1.10", "administrator");
        var lines = content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("full address:s:192.168.1.10", lines);
        Assert.Contains("username:s:administrator", lines);
        Assert.Contains("screen mode id:i:1", lines);
        Assert.Contains("prompt for credentials:i:0", lines);
        Assert.EndsWith("\r\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRdpFileContent_OmitsTheUserNameWhenThereIsNone()
        => Assert.DoesNotContain("username:s:", RemoteLauncher.BuildRdpFileContent("nas.local"));

    [Fact]
    public void BuildRdpFileContent_IncludesACompleteSizeOnly()
    {
        var sized = RemoteLauncher.BuildRdpFileContent("nas.local", null, 1280, 800);
        Assert.Contains("desktopwidth:i:1280", sized);
        Assert.Contains("desktopheight:i:800", sized);

        Assert.DoesNotContain("desktopwidth", RemoteLauncher.BuildRdpFileContent("nas.local", null, 1280, null));
        Assert.DoesNotContain("desktopwidth", RemoteLauncher.BuildRdpFileContent("nas.local", null, 10, 10));
        Assert.DoesNotContain("desktopheight", RemoteLauncher.BuildRdpFileContent("nas.local", null, 99999, 800));
    }

    [Fact]
    public void BuildRdpFileContent_RefusesAnInvalidHost()
    {
        Assert.Throws<ArgumentException>(() => RemoteLauncher.BuildRdpFileContent("192.168.1.1 & calc"));
        Assert.Throws<ArgumentException>(() => RemoteLauncher.BuildRdpFileContent("nas.local", "bad user"));
    }

    [Fact]
    public void IsSshClientAvailable_AgreesWithAnIndependentPathSearch()
    {
        var launcher = new RemoteLauncher();
        var available = launcher.IsSshClientAvailable;

        Assert.Equal(available, launcher.IsSshClientAvailable);   // stable / cached
        Assert.Equal(FindOnPath("ssh.exe") || File.Exists(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe")), available);
    }

    [Fact]
    public void IsRdpAvailable_IsTrueOnWindows()
    {
        var launcher = new RemoteLauncher();

        Assert.True(launcher.IsRdpAvailable);
        Assert.True(File.Exists(Path.Combine(Environment.SystemDirectory, "mstsc.exe")));
    }

    [Fact]
    public void ResolveHostKey_ReturnsNullWhenNothingIsCached()
    {
        var launcher = new RemoteLauncher();

        Assert.Null(launcher.ResolveHostKey("203.0.113.77"));
        Assert.Null(launcher.ResolveHostKey("192.168.1.1 & calc"));
        Assert.Null(launcher.ResolveHostKey(""));
        Assert.Null(launcher.ResolveHostKey(null!));
    }

    [Fact]
    public void ResolveHostKey_UsesTheCachedReverseName()
    {
        const string ip = "203.0.113.78";
        ScanCaches.HostNames.Set(ip, "printer-01");
        ScanCaches.MacAddresses.Set(ip, "00-1A-2B-3C-4D-5E");
        try
        {
            var launcher = new RemoteLauncher();

            Assert.Equal("printer-01", launcher.ResolveHostKey(ip));
            // …and the MAC now resolves too, from the same cache entry.
            Assert.Equal("printer-01", launcher.ResolveHostKey("00-1A-2B-3C-4D-5E"));
        }
        finally
        {
            ScanCaches.ClearAll();
        }
    }

    [Fact]
    public void ResolveHostKey_PrefersTheMemoNoteForTheMac()
    {
        const string ip = "203.0.113.79";
        const string mac = "00-1A-2B-3C-4D-5F";
        ScanCaches.HostNames.Set(ip, "some-dns-name");
        ScanCaches.MacAddresses.Set(ip, mac);
        var memo = new MemoStore(Path.Combine(Path.GetTempPath(), $"netadmin-memo-{Guid.NewGuid():N}.dat"));
        memo.Set(mac, "三楼机房打印机");
        try
        {
            var launcher = new RemoteLauncher(memo);

            Assert.Equal("三楼机房打印机", launcher.ResolveHostKey(ip));
            Assert.Equal("三楼机房打印机", launcher.ResolveHostKey(mac));
            Assert.Equal("三楼机房打印机", launcher.ResolveHostKey(mac.ToLowerInvariant().Replace('-', ':')));
        }
        finally
        {
            ScanCaches.ClearAll();
        }
    }

    [Fact]
    public void ResolveHostKey_IgnoresTheUnknownPlaceholder()
    {
        const string ip = "203.0.113.80";
        ScanCaches.HostNames.Set(ip, NameResolver.Unknown);
        try
        {
            Assert.Null(new RemoteLauncher().ResolveHostKey(ip));
        }
        finally
        {
            ScanCaches.ClearAll();
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static HashSet<string> TempRdpFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(Path.GetTempPath(), "ipscaner-*.rdp"))
        {
            files.Add(Path.GetFileName(path) ?? path);
        }
        return files;
    }

    private static bool FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(Path.Combine(directory.Trim('"'), fileName))) return true;
        }
        return false;
    }
}
