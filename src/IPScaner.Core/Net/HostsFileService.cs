using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IPScaner.Core.Logging;
using IPScaner.Core.Storage;

namespace IPScaner.Core.Net;

/// <summary>
/// One active mapping line of the hosts file (一键管理 hosts 记录).
/// </summary>
/// <remarks>
/// A line carrying several host names produces one entry per name — each entry
/// shares the physical <see cref="LineNumber"/>. Comment lines, blank lines and
/// malformed lines are never returned by <see cref="HostsFileService.Read"/>.
/// </remarks>
public sealed class HostsEntry
{
    public string IP { get; set; } = string.Empty;

    public string Hostname { get; set; } = string.Empty;

    /// <summary>Text after the trailing '#', without the '#'. Null when there is none.</summary>
    public string? Comment { get; set; }

    /// <summary>1-based physical line number in the file.</summary>
    public int LineNumber { get; set; }

    /// <summary>True when the address black-holes the name (0.0.0.0 / 127.0.0.0/8 / :: / ::1).</summary>
    public bool IsBlocking { get; set; }

    public override string ToString() =>
        Comment is null
            ? $"{IP} {Hostname} (line {LineNumber})"
            : $"{IP} {Hostname} # {Comment} (line {LineNumber})";
}

/// <summary>Outcome of one hosts-file operation.</summary>
/// <param name="Success">True when the requested state was reached (an idempotent no-op counts as success).</param>
/// <param name="Message">Human-readable (Chinese) result, ready for the status bar.</param>
/// <param name="Changed">Number of mappings added, removed or updated; 0 for a no-op.</param>
public sealed record HostsOperationResult(bool Success, string Message, int Changed);

/// <summary>
/// Reads and edits the Windows hosts file ("一键管理 hosts 记录").
/// </summary>
/// <remarks>
/// <para>
/// <b>Byte preservation is the primary contract.</b> The file is read as raw
/// bytes, decoded with the encoding it already uses, split into lines that each
/// keep their own terminator, and written back with the same encoding. Only the
/// tokens an operation actually targets can differ: every comment, blank line,
/// indentation, tab, spacing and line ending — including whether the file ends
/// with a newline at all — survives untouched, and the original order is never
/// re-sorted.
/// </para>
/// <para>
/// The original tool rewrote the whole file from a parsed model, which silently
/// dropped comments, collapsed alignment and changed the encoding; that is the
/// defect this class exists to avoid.
/// </para>
/// <para>
/// Every mutating call takes a backup (<c>hosts.backup-yyyyMMdd-HHmmss</c> beside
/// the file) immediately before its first write, and refuses to write at all when
/// the backup cannot be made. Inputs are validated before the file is even opened,
/// so a rejected call leaves no backup and no trace. Writing the real hosts file
/// needs an elevated process: <see cref="UnauthorizedAccessException"/> is turned
/// into the exact message <see cref="AdminRequiredMessage"/>.
/// </para>
/// <para>
/// <see cref="Read"/> can report a name that <see cref="Add"/> would refuse
/// (for example an IPv6 literal used as the second column); entries are reported
/// as written, writes are validated strictly.
/// </para>
/// </remarks>
public sealed class HostsFileService
{
    /// <summary>The exact message shown when the hosts file cannot be written without elevation.</summary>
    public const string AdminRequiredMessage = "需要管理员权限才能修改 hosts 文件";

    private const string Category = nameof(HostsFileService);

    private readonly object _sync = new();

    /// <summary>Creates a service for the real hosts file, or for a specific path (tests/portable use).</summary>
    public HostsFileService(string? filePath = null) => FilePath = filePath ?? DefaultFilePath();

    /// <summary>Full path of the file this instance edits.</summary>
    public string FilePath { get; }

    /// <summary><c>%SystemRoot%\System32\drivers\etc\hosts</c>.</summary>
    public static string DefaultFilePath()
    {
        var system32 = Environment.SystemDirectory;
        if (string.IsNullOrEmpty(system32))
        {
            system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        }
        return Path.Combine(system32, "drivers", "etc", "hosts");
    }

    // ---- reading -----------------------------------------------------------

    /// <summary>
    /// Returns every active mapping, in file order. A missing, empty or unreadable
    /// file yields an empty list — this method never throws.
    /// </summary>
    public List<HostsEntry> Read()
    {
        lock (_sync)
        {
            var document = TryLoad(out var error);
            if (document is null)
            {
                AppLog.Instance.Log(Category, error ?? "读取 hosts 文件失败。");
                return [];
            }

            return ReadEntries(document);
        }
    }

    // ---- mutation ----------------------------------------------------------

    /// <summary>
    /// Adds <paramref name="ip"/> → <paramref name="hostname"/>.
    /// </summary>
    /// <remarks>
    /// Idempotent: when the name already resolves to that address the file is not
    /// touched and no backup is taken. When the name is mapped to a different
    /// address the existing single-name line is re-pointed in place (a hosts file
    /// resolves the <i>first</i> match, so appending a second line would have no
    /// effect at all); a line that carries several names is left alone and the new
    /// mapping is inserted at the top, where it wins.
    /// </remarks>
    public HostsOperationResult Add(string ip, string hostname, string? comment = null)
    {
        if (!TryValidateWrite(ip, hostname, out var failure)) return failure;

        lock (_sync)
        {
            var document = LoadForWrite(out var loadFailure);
            if (document is null) return loadFailure ?? new HostsOperationResult(false, "无法读取 hosts 文件。", 0);

            var added = ApplyAdd(document, ip, hostname, comment);
            return added
                ? Commit(document, $"已添加记录：{ip} {hostname}", 1)
                : new HostsOperationResult(true, $"已存在相同记录：{ip} {hostname}，未做修改", 0);
        }
    }

    /// <summary>
    /// Adds a batch of mappings with a single backup and a single write. Every
    /// entry is validated first, so a bad address cannot leave a half-applied
    /// batch behind.
    /// </summary>
    public HostsOperationResult AddMany(IEnumerable<(string Ip, string Hostname)> entries, string? comment = null)
    {
        if (entries is null) return new HostsOperationResult(false, "没有需要添加的记录。", 0);

        var list = new List<(string Ip, string Hostname)>();
        foreach (var (ip, hostname) in entries)
        {
            if (!TryValidateWrite(ip, hostname, out var failure)) return failure;
            list.Add((ip.Trim(), hostname.Trim()));
        }

        if (list.Count == 0) return new HostsOperationResult(false, "没有需要添加的记录。", 0);

        lock (_sync)
        {
            var document = LoadForWrite(out var loadFailure);
            if (document is null) return loadFailure ?? new HostsOperationResult(false, "无法读取 hosts 文件。", 0);

            var changed = 0;
            foreach (var (ip, hostname) in list)
            {
                if (ApplyAdd(document, ip, hostname, comment)) changed++;
            }

            return changed == 0
                ? new HostsOperationResult(true, $"共 {list.Count} 条记录，全部已存在，未做修改", 0)
                : Commit(document, $"已批量添加 {changed} 条记录", changed);
        }
    }

    /// <summary>
    /// Removes every active mapping of <paramref name="hostname"/>. Also used for
    /// "清理失效记录": removing a name that is not present succeeds with
    /// <c>Changed = 0</c>.
    /// </summary>
    public HostsOperationResult Remove(string hostname)
    {
        if (!TryValidateHostname(hostname, out var clean, out var failure)) return failure;

        lock (_sync)
        {
            var document = LoadForWrite(out var loadFailure);
            if (document is null) return loadFailure ?? new HostsOperationResult(false, "无法读取 hosts 文件。", 0);

            var removed = ApplyRemove(document, clean);
            return removed == 0
                ? new HostsOperationResult(true, $"hosts 文件中没有 {clean} 的有效记录", 0)
                : Commit(document, $"已删除 {removed} 条记录：{clean}", removed);
        }
    }

    /// <summary>Removes several names with a single backup and a single write.</summary>
    public HostsOperationResult RemoveMany(IEnumerable<string> hostnames)
    {
        if (hostnames is null) return new HostsOperationResult(false, "没有需要删除的记录。", 0);

        var list = new List<string>();
        foreach (var hostname in hostnames)
        {
            if (!TryValidateHostname(hostname, out var clean, out var failure)) return failure;
            list.Add(clean);
        }

        if (list.Count == 0) return new HostsOperationResult(false, "没有需要删除的记录。", 0);

        lock (_sync)
        {
            var document = LoadForWrite(out var loadFailure);
            if (document is null) return loadFailure ?? new HostsOperationResult(false, "无法读取 hosts 文件。", 0);

            var removed = list.Sum(hostname => ApplyRemove(document, hostname));
            return removed == 0
                ? new HostsOperationResult(true, $"共 {list.Count} 个名称，hosts 文件中没有对应记录", 0)
                : Commit(document, $"已批量删除 {removed} 条记录", removed);
        }
    }

    /// <summary>Re-points an existing mapping to <paramref name="newIp"/>.</summary>
    public HostsOperationResult Update(string hostname, string newIp)
    {
        if (!TryValidateHostname(hostname, out var clean, out var failure)) return failure;
        if (!IpMath.IsValidIPv4(newIp)) return Failure($"IP地址格式不正确：\"{newIp}\"（应为 192.168.1.100 这样的格式）");

        lock (_sync)
        {
            var document = LoadForWrite(out var loadFailure);
            if (document is null) return loadFailure ?? new HostsOperationResult(false, "无法读取 hosts 文件。", 0);

            var target = FindMappings(document, clean).FirstOrDefault();
            if (target is null)
            {
                return new HostsOperationResult(false, $"hosts 文件中没有 {clean} 的记录，无法更新", 0);
            }

            if (string.Equals(target.Ip, newIp.Trim(), StringComparison.Ordinal))
            {
                return new HostsOperationResult(true, $"{clean} 的IP已是 {newIp.Trim()}，未做修改", 0);
            }

            ApplyUpdate(document, target, clean, newIp.Trim());
            return Commit(document, $"已更新 {clean} 的IP为 {newIp.Trim()}", 1);
        }
    }

    // ---- backup ------------------------------------------------------------

    /// <summary>
    /// Copies the file to <c>hosts.backup-yyyyMMdd-HHmmss</c> beside it and returns
    /// that path. Returns null when there is nothing to copy or the copy failed —
    /// this method never throws.
    /// </summary>
    public string? Backup()
    {
        lock (_sync) return BackupUnlocked();
    }

    /// <summary>Runs <c>ipconfig /flushdns</c>. Never throws and never reports failure to the caller.</summary>
    public static async Task FlushDnsCacheAsync(CancellationToken ct = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo("ipconfig", "/flushdns")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = TextFileEncoding.Gbk,
                StandardErrorEncoding = TextFileEncoding.Gbk,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                AppLog.Instance.Log(Category, "无法启动 ipconfig /flushdns。");
                return;
            }

            // Drain both pipes before waiting so a chatty ipconfig cannot block.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            AppLog.Instance.Log(Category, $"已刷新DNS缓存（ipconfig /flushdns 退出码 {process.ExitCode}）");
        }
        catch (Exception ex)
        {
            // Cancellation and a missing ipconfig both land here on purpose.
            AppLog.Instance.Log(Category, "刷新DNS缓存失败: " + ex.Message);
        }
    }

    // ---- validation --------------------------------------------------------

    /// <summary>
    /// Validates an address/name pair that is about to be written. Matches the
    /// original's rule: a dotted quad, and a name without whitespace or '#'.
    /// </summary>
    private static bool TryValidateWrite(string? ip, string? hostname, [NotNullWhen(false)] out HostsOperationResult? failure)
    {
        failure = null;

        if (!IpMath.IsValidIPv4(ip))
        {
            failure = Failure($"IP地址格式不正确：\"{ip}\"（应为 192.168.1.100 这样的格式）");
            return false;
        }

        if (string.IsNullOrWhiteSpace(hostname))
        {
            failure = Failure("主机名不能为空");
            return false;
        }

        if (hostname.Any(char.IsWhiteSpace))
        {
            failure = Failure($"主机名不合法：\"{hostname}\"（不能包含空格）");
            return false;
        }

        if (hostname.Contains('#'))
        {
            failure = Failure($"主机名不合法：\"{hostname}\"（不能包含 # ）");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validation for names that are only ever compared, never written — removal
    /// has to accept IPv6 literals so "清理失效记录" can drop those lines too.
    /// </summary>
    private static bool TryValidateHostname(string? hostname, out string clean, [NotNullWhen(false)] out HostsOperationResult? failure)
    {
        failure = null;
        clean = hostname?.Trim() ?? string.Empty;

        if (clean.Length == 0)
        {
            failure = Failure("主机名不能为空");
            return false;
        }

        if (clean.Any(char.IsWhiteSpace) || clean.Contains('#'))
        {
            failure = Failure($"主机名不合法：\"{hostname}\"（不能包含空格或 # ）");
            return false;
        }

        return true;
    }

    private static HostsOperationResult Failure(string message) => new(false, message, 0);

    // ---- file model --------------------------------------------------------

    /// <summary>One physical line, kept with its own terminator so a round trip is byte-exact.</summary>
    private readonly record struct RawLine(string Text, string Ending)
    {
        public string Raw => Text + Ending;
    }

    private sealed class HostsDocument
    {
        public List<RawLine> Lines { get; set; } = [];

        public Encoding Encoding { get; set; } = TextFileEncoding.Utf8NoBom;

        /// <summary>False when the path did not exist — then there is nothing to back up.</summary>
        public bool Exists { get; set; }

        public string Eol { get; set; } = "\r\n";

        /// <summary>Whether the original file ended with a terminator; the convention is preserved.</summary>
        public bool EndsWithNewline { get; set; }

        public string Text => string.Concat(Lines.Select(line => line.Raw));
    }

    /// <summary>A whitespace-delimited token together with its span in the line.</summary>
    private readonly record struct Token(string Text, int Start, int Length)
    {
        public int End => Start + Length;
    }

    /// <summary>The result of interpreting one line.</summary>
    private sealed class ParsedLine
    {
        public int LineIndex { get; init; }

        public int LineNumber => LineIndex + 1;

        /// <summary>Span of the address token, so an update can replace exactly that token.</summary>
        public Token IpToken { get; set; }

        public string? Ip => IpToken.Text;

        public List<Token> Hostnames { get; } = [];

        public string? Comment { get; set; }

        public bool IsMapping => Ip is not null && Hostnames.Count > 0;
    }

    private static List<RawLine> SplitLines(string text)
    {
        var lines = new List<RawLine>();
        var index = 0;
        var start = 0;

        while (index < text.Length)
        {
            var c = text[index];
            if (c is not ('\r' or '\n'))
            {
                index++;
                continue;
            }

            var ending = c == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : c.ToString();
            lines.Add(new RawLine(text[start..index], ending));
            index += ending.Length;
            start = index;
        }

        // A trailing fragment without a terminator is a real line (the file simply
        // does not end with a newline); an empty fragment means the file did, and
        // adding it would duplicate the last terminator.
        if (start < text.Length) lines.Add(new RawLine(text[start..], string.Empty));

        return lines;
    }

    private static string DetectEol(List<RawLine> lines)
    {
        int crlf = 0, lf = 0, cr = 0;
        foreach (var line in lines)
        {
            switch (line.Ending)
            {
                case "\r\n": crlf++; break;
                case "\n": lf++; break;
                case "\r": cr++; break;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0) return Environment.NewLine;
        if (crlf >= lf && crlf >= cr) return "\r\n";
        return lf >= cr ? "\n" : "\r";
    }

    private HostsDocument? TryLoad(out string? error)
    {
        error = null;
        var document = new HostsDocument();

        try
        {
            if (!File.Exists(FilePath)) return document;

            document.Exists = true;
            var bytes = File.ReadAllBytes(FilePath);
            document.Encoding = ResolveEncoding(bytes, out var text);

            document.Lines = SplitLines(text);
            document.EndsWithNewline = text.Length > 0 && text[^1] is '\r' or '\n';
            document.Eol = DetectEol(document.Lines);
            return document;
        }
        catch (Exception ex)
        {
            error = $"无法读取 hosts 文件（{FilePath}）：{ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Chooses the encoding the file is written back with, and decodes it.
    /// </summary>
    /// <remarks>
    /// <see cref="TextFileEncoding.Detect"/> is the file's own encoding and is used
    /// whenever it can reproduce the file byte-for-byte, but on its own it is not
    /// safe here: its "lead byte &gt;= 161" heuristic cannot tell a BOM-less UTF-8
    /// Chinese file from GBK, so a comment written in UTF-8 would come back as
    /// mojibake — and a byte sequence that is <i>invalid</i> GBK (a dangling lead
    /// byte at the end of a comment) decodes to U+003F and would corrupt that line
    /// on the way back out. Every candidate therefore has to prove it can reproduce
    /// the bytes, and a BOM-less file whose bytes are valid UTF-8 <i>and</i> contain
    /// non-ASCII is taken to be UTF-8 — the same decisive signal
    /// <see cref="NetworkConfigurator"/> uses for netsh output, because GBK text
    /// practically never forms valid UTF-8. Latin-1, which maps every byte to one
    /// character, is the last resort so a truly odd file still survives untouched.
    /// </remarks>
    private static Encoding ResolveEncoding(byte[] bytes, out string text)
    {
        var detected = TextFileEncoding.Detect(bytes);
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var hasPreamble = HasPreamble(bytes, detected);

        // 1. A byte-order mark is authoritative.
        if (hasPreamble && TryDecodeLosslessly(detected, bytes, out text)) return detected;

        // 2. BOM-less: valid UTF-8 with non-ASCII bytes in it is UTF-8.
        if (!hasPreamble && HasNonAscii(bytes, detected) && TryDecodeLosslessly(strictUtf8, bytes, out text))
        {
            return strictUtf8;
        }

        // 3. Whatever the file already used.
        if (TryDecodeLosslessly(detected, bytes, out text)) return detected;

        // 4. Last resorts: readable UTF-8, else byte-preserving Latin-1.
        if (TryDecodeLosslessly(strictUtf8, bytes, out text)) return strictUtf8;

        AppLog.Instance.Log(Category, "hosts 文件的编码无法逐字节还原，改用 Latin-1 以保护原有内容。");
        _ = TryDecodeLosslessly(Encoding.Latin1, bytes, out text);
        return Encoding.Latin1;
    }

    /// <summary>The bytes without <paramref name="encoding"/>'s preamble.</summary>
    private static ReadOnlySpan<byte> BodyOf(byte[] bytes, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        if (preamble.Length > 0 && bytes.Length >= preamble.Length && bytes.AsSpan(0, preamble.Length).SequenceEqual(preamble))
        {
            return bytes.AsSpan(preamble.Length);
        }
        return bytes;
    }

    private static bool HasPreamble(byte[] bytes, Encoding encoding) =>
        BodyOf(bytes, encoding).Length != bytes.Length;

    private static bool HasNonAscii(byte[] bytes, Encoding encoding)
    {
        foreach (var b in BodyOf(bytes, encoding))
        {
            if (b >= 0x80) return true;
        }
        return false;
    }

    /// <summary>Decodes with <paramref name="encoding"/> only when re-encoding reproduces the very same bytes.</summary>
    private static bool TryDecodeLosslessly(Encoding encoding, byte[] bytes, out string text)
    {
        text = string.Empty;
        try
        {
            text = encoding.GetString(bytes);
            // GetString keeps a BOM as U+FEFF; it must not become part of line 1,
            // and it is written back out as the encoding's preamble.
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

            return encoding.GetBytes(text).AsSpan().SequenceEqual(BodyOf(bytes, encoding));
        }
        catch (Exception ex) when (ex is DecoderFallbackException or EncoderFallbackException)
        {
            return false;
        }
    }

    private HostsDocument? LoadForWrite(out HostsOperationResult? failure)
    {
        var document = TryLoad(out var error);
        if (document is not null)
        {
            failure = null;
            return document;
        }

        AppLog.Instance.Log(Category, error ?? "读取 hosts 文件失败。");
        failure = new HostsOperationResult(false, error ?? "无法读取 hosts 文件。", 0);
        return null;
    }

    private static List<ParsedLine> ParseAll(List<RawLine> lines)
    {
        var result = new List<ParsedLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++) result.Add(ParseLine(lines[i].Text, i));
        return result;
    }

    private static ParsedLine ParseLine(string text, int index)
    {
        var parsed = new ParsedLine { LineIndex = index };

        var cursor = 0;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        if (cursor >= text.Length) return parsed;      // blank (or whitespace-only) line
        if (text[cursor] == '#')
        {
            // Whole-line comment: '# localhost' style entries are NOT active mappings.
            return parsed;
        }

        var hash = text.IndexOf('#', cursor);
        var codeEnd = hash < 0 ? text.Length : hash;
        if (hash >= 0)
        {
            var comment = text[(hash + 1)..].Trim();
            parsed.Comment = comment.Length == 0 ? null : comment;
        }

        var tokens = new List<Token>();
        while (cursor < codeEnd)
        {
            if (char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
                continue;
            }

            var start = cursor;
            while (cursor < codeEnd && !char.IsWhiteSpace(text[cursor])) cursor++;
            tokens.Add(new Token(text[start..cursor], start, cursor - start));
        }

        if (tokens.Count < 2 || !IsHostsAddress(tokens[0].Text)) return parsed;

        parsed.IpToken = tokens[0];
        for (var i = 1; i < tokens.Count; i++) parsed.Hostnames.Add(tokens[i]);
        return parsed;
    }

    /// <summary>Strict dotted quad, or any well-formed IPv6 literal (hosts files often carry both).</summary>
    private static bool IsHostsAddress(string text)
    {
        if (IpMath.IsValidIPv4(text)) return true;
        return IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static bool IsBlockingAddress(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            return octets[0] == 127 || (octets[0] | octets[1] | octets[2] | octets[3]) == 0;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
               (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any));
    }

    private static List<HostsEntry> ReadEntries(HostsDocument document)
    {
        var entries = new List<HostsEntry>();
        foreach (var line in ParseAll(document.Lines))
        {
            if (!line.IsMapping) continue;
            var blocking = IsBlockingAddress(line.Ip!);
            foreach (var hostname in line.Hostnames)
            {
                entries.Add(new HostsEntry
                {
                    IP = line.Ip!,
                    Hostname = hostname.Text,
                    Comment = line.Comment,
                    LineNumber = line.LineNumber,
                    IsBlocking = blocking,
                });
            }
        }
        return entries;
    }

    // ---- edit helpers ------------------------------------------------------

    private static bool NameMatches(string token, string hostname) =>
        string.Equals(token, hostname, StringComparison.OrdinalIgnoreCase);

    private static List<ParsedLine> FindMappings(HostsDocument document, string hostname) =>
        [.. ParseAll(document.Lines).Where(line => line.IsMapping && line.Hostnames.Any(t => NameMatches(t.Text, hostname)))];

    /// <summary>Replaces a token in place, which is the smallest possible edit.</summary>
    private static void ReplaceToken(HostsDocument document, int lineIndex, Token token, string replacement)
    {
        var text = document.Lines[lineIndex].Text;
        var updated = text[..token.Start] + replacement + text[token.End..];
        document.Lines[lineIndex] = document.Lines[lineIndex] with { Text = updated };
    }

    /// <summary>
    /// Deletes a token plus one adjacent whitespace run, so the surviving names keep
    /// their relative spacing instead of leaving a double space behind.
    /// </summary>
    private static void RemoveToken(HostsDocument document, int lineIndex, Token token)
    {
        var text = document.Lines[lineIndex].Text;
        var after = token.End;
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;

        string updated;
        if (after > token.End)
        {
            updated = text[..token.Start] + text[after..];
        }
        else
        {
            var before = token.Start;
            while (before > 0 && char.IsWhiteSpace(text[before - 1])) before--;
            updated = text[..before] + text[token.End..];
        }

        document.Lines[lineIndex] = document.Lines[lineIndex] with { Text = updated };
    }

    private static void RemoveLine(HostsDocument document, int lineIndex) => document.Lines.RemoveAt(lineIndex);

    /// <summary>Appends a mapping, terminating the previous last line when needed and preserving the EOF style.</summary>
    private static void AppendMapping(HostsDocument document, string text)
    {
        if (document.Lines.Count == 0)
        {
            document.Lines.Add(new RawLine(text, document.Exists && !document.EndsWithNewline ? string.Empty : document.Eol));
            return;
        }

        var last = document.Lines[^1];
        if (last.Ending.Length == 0) document.Lines[^1] = last with { Ending = document.Eol };
        document.Lines.Add(new RawLine(text, document.EndsWithNewline ? document.Eol : string.Empty));
    }

    /// <summary>
    /// Inserts a mapping at the top of the file. Used when the name is already
    /// carried by a multi-name line: a hosts file honours the first match, so only
    /// a line above it can change the answer without disturbing the other names.
    /// </summary>
    private static void InsertMappingAtTop(HostsDocument document, string text)
    {
        var ending = document.Lines.Count == 0 && document.Exists && !document.EndsWithNewline ? string.Empty : document.Eol;
        document.Lines.Insert(0, new RawLine(text, ending));
    }

    private static string BuildMappingText(string ip, string hostname, string? comment)
    {
        var text = $"{ip} {hostname}";
        var trimmed = comment?.Trim();
        if (!string.IsNullOrEmpty(trimmed)) text += $" # {trimmed}";
        return text;
    }

    // ---- edit plans --------------------------------------------------------

    /// <summary>Adds or re-points one mapping. Returns false when the file already satisfies it.</summary>
    private static bool ApplyAdd(HostsDocument document, string ip, string hostname, string? comment)
    {
        var target = FindMappings(document, hostname).FirstOrDefault();

        if (target is null)
        {
            AppendMapping(document, BuildMappingText(ip, hostname, comment));
            return true;
        }

        if (string.Equals(target.Ip, ip, StringComparison.Ordinal)) return false;

        if (target.Hostnames.Count == 1)
        {
            // Single-name line: re-point it, keeping spacing, comment and terminator.
            ReplaceToken(document, target.LineIndex, target.IpToken, ip);
            return true;
        }

        InsertMappingAtTop(document, BuildMappingText(ip, hostname, comment));
        return true;
    }

    /// <summary>Removes every mapping of a name; returns how many mappings were removed.</summary>
    private static int ApplyRemove(HostsDocument document, string hostname)
    {
        var removed = 0;

        // Walk backwards so line indices stay valid while lines disappear.
        foreach (var parsed in ParseAll(document.Lines).AsEnumerable().Reverse())
        {
            if (!parsed.IsMapping) continue;

            var matches = parsed.Hostnames.Where(t => NameMatches(t.Text, hostname)).ToList();
            if (matches.Count == 0) continue;

            if (matches.Count == parsed.Hostnames.Count)
            {
                RemoveLine(document, parsed.LineIndex);
            }
            else
            {
                // Keep the other names on the line, dropping only the matched token.
                foreach (var token in matches.OrderByDescending(t => t.Start))
                {
                    RemoveToken(document, parsed.LineIndex, token);
                }
            }

            removed += matches.Count;
        }

        return removed;
    }

    private static void ApplyUpdate(HostsDocument document, ParsedLine target, string hostname, string newIp)
    {
        if (target.Hostnames.Count == 1)
        {
            ReplaceToken(document, target.LineIndex, target.IpToken, newIp);
            return;
        }

        // The line carries several names and only this one moves: take the name off
        // that line (the others keep their address) and put the new mapping on top,
        // where a first-match-wins resolver will honour it.
        var token = target.Hostnames.First(t => NameMatches(t.Text, hostname));
        RemoveToken(document, target.LineIndex, token);
        InsertMappingAtTop(document, $"{newIp} {token.Text}");
    }

    // ---- writing -----------------------------------------------------------

    private HostsOperationResult Commit(HostsDocument document, string message, int changed)
    {
        string? backupName = null;
        if (document.Exists)
        {
            var backupPath = BackupUnlocked();
            if (backupPath is null)
            {
                return new HostsOperationResult(false, "备份 hosts 文件失败，为安全起见已取消本次修改。", 0);
            }
            backupName = Path.GetFileName(backupPath);
        }

        try
        {
            File.WriteAllText(FilePath, document.Text, document.Encoding);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Instance.Log(Category, $"写入 hosts 被拒绝（{FilePath}）: {ex.Message}");
            return new HostsOperationResult(false, AdminRequiredMessage, 0);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(Category, $"写入 hosts 失败（{FilePath}）: {ex.Message}");
            return new HostsOperationResult(false, $"写入 hosts 文件失败：{ex.Message}", 0);
        }

        var full = backupName is null ? $"{message}。如未生效请刷新DNS缓存（ipconfig /flushdns）" : $"{message}（备份 {backupName}）。如未生效请刷新DNS缓存（ipconfig /flushdns）";
        AppLog.Instance.Log(Category, full);
        return new HostsOperationResult(true, full, changed);
    }

    private string? BackupUnlocked()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(directory)) directory = ".";

            var name = Path.GetFileName(FilePath);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var target = Path.Combine(directory, $"{name}.backup-{stamp}");

            // Two changes inside the same second must not lose the first backup.
            var suffix = 1;
            while (File.Exists(target))
            {
                target = Path.Combine(directory, $"{name}.backup-{stamp}-{suffix++}");
            }

            File.Copy(FilePath, target, overwrite: false);
            AppLog.Instance.Log(Category, $"已备份 hosts 文件到 {target}");
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(Category, $"备份 hosts 文件失败: {ex.Message}");
            return null;
        }
    }
}
