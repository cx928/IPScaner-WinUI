using System.Text;
using IPScaner.Core.Storage;

namespace IPScaner.Core.Memo;

/// <summary>
/// The user's notes about hosts (备注管理).
/// </summary>
/// <remarks>
/// On-disk format is preserved exactly so an existing IPScanerMemo.dat keeps
/// working:
/// <code>
/// 192.168.1.10=财务部打印机
/// 00-1A-2B-3C-4D-5E=三楼机房
/// </code>
/// One <c>KEY=VALUE</c> entry per line, <c>#</c> starts a comment, and an embedded
/// newline is stored as the two-character escape <c>^v^</c>. Values may contain
/// '=' (only the first one separates). Files are written as UTF-8 with a BOM,
/// matching the original writer.
/// <para>
/// A key is either an IPv4 address or a MAC address. <b>MAC wins</b>: when a host
/// has both, the MAC entry is used, so a note follows a machine whose DHCP lease
/// changed (this is the documented v1.27 behaviour).
/// </para>
/// </remarks>
public sealed partial class MemoStore
{
    public const string FileName = "IPScanerMemo.dat";

    /// <summary>Legacy file name, migrated on first load.</summary>
    public const string LegacyFileName = "IPScaner.dat";

    private const string NewlineEscape = "^v^";

    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    public string FilePath { get; }

    public MemoStore(string? filePath = null)
        => FilePath = filePath ?? Path.Combine(Storage.AppPaths.DataDirectory, FileName);

    public int Count => _entries.Count;

    /// <summary>Snapshot of every entry.</summary>
    public IReadOnlyDictionary<string, string> Entries => _entries;

    // ---- persistence -------------------------------------------------------

    /// <summary>
    /// Loads the memo file, first migrating a legacy <c>IPScaner.dat</c> if the
    /// current file does not exist yet.
    /// </summary>
    public void Load()
    {
        TryMigrateLegacy();

        _entries.Clear();
        if (!File.Exists(FilePath)) return;

        string[] lines;
        try { lines = TextFileEncoding.ReadAllLines(FilePath); }
        catch { return; }

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.StartsWith('#')) continue;

            var idx = raw.IndexOf('=');
            if (idx <= 0) continue;

            var key = NormalizeKey(raw[..idx]);
            if (key.Length == 0) continue;

            // Rejoin the remainder so '=' inside a note survives.
            var value = raw[(idx + 1)..].Replace(NewlineEscape, Environment.NewLine);
            _entries[key] = value;
        }
    }

    /// <summary>Writes every non-empty entry back to disk.</summary>
    public void Save(string? filePath = null)
    {
        var path = filePath ?? FilePath;
        var sb = new StringBuilder();
        foreach (var (key, value) in _entries)
        {
            if (string.IsNullOrEmpty(value)) continue;
            sb.AppendLine(key + "=" + value.Replace("\r\n", NewlineEscape).Replace("\n", NewlineEscape));
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), TextFileEncoding.Utf8Bom);
    }

    private void TryMigrateLegacy()
    {
        if (File.Exists(FilePath)) return;
        var legacy = Path.Combine(Path.GetDirectoryName(FilePath) ?? ".", LegacyFileName);
        if (!File.Exists(legacy)) return;
        try { File.Move(legacy, FilePath); }
        catch { /* keep going with whatever is readable */ }
    }

    // ---- lookup ------------------------------------------------------------

    /// <summary>
    /// Resolves a note by MAC first, then by IP (v1.27 precedence).
    /// Returns an empty string when nothing matches.
    /// </summary>
    /// <remarks>
    /// Both arguments are canonicalised first: the ARP table can report a MAC
    /// with either '-' or ':' separators depending on the source, and treating
    /// those as different keys silently loses a note the user can see on screen.
    /// </remarks>
    public string Lookup(string? mac, string? ipAddress = null)
    {
        if (!string.IsNullOrEmpty(mac) && _entries.TryGetValue(NormalizeKey(mac), out var byMac)) return byMac;
        if (!string.IsNullOrEmpty(ipAddress) && _entries.TryGetValue(ipAddress.Trim(), out var byIp)) return byIp;
        return string.Empty;
    }

    public bool TryGet(string key, out string value) => _entries.TryGetValue(NormalizeKey(key), out value!);

    /// <summary>True when the key is a MAC rather than an IP.</summary>
    public static bool IsMacKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var text = key.Trim();
        if (text.Contains('.')) return false; // IPv4 dotted quad
        return MacKeyRegex().IsMatch(text);
    }

    /// <summary>
    /// Canonical key form: MACs become upper-case dash-separated, everything else
    /// is only trimmed. This is what makes a note written as "00:1A:…" match a
    /// lookup for "00-1A-…" and vice versa.
    /// </summary>
    public static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        var text = key.Trim();
        if (!MacKeyRegex().IsMatch(text)) return text;
        return text.ToUpperInvariant().Replace(':', '-');
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$")]
    private static partial System.Text.RegularExpressions.Regex MacKeyRegex();

    // ---- mutation ----------------------------------------------------------

    /// <summary>Adds or updates a note. An empty value removes the entry.</summary>
    public void Set(string key, string? value)
    {
        var canonical = NormalizeKey(key);
        if (canonical.Length == 0) return;
        if (string.IsNullOrEmpty(value)) _entries.Remove(canonical);
        else _entries[canonical] = value;
    }

    public bool Remove(string key) => _entries.Remove(NormalizeKey(key));

    public void Clear() => _entries.Clear();

    /// <summary>Replaces every entry (used by the clipboard-import feature).</summary>
    public void ReplaceAll(IEnumerable<KeyValuePair<string, string>> items)
    {
        _entries.Clear();
        foreach (var (k, v) in items) Set(k, v);
    }

    /// <summary>
    /// Parses pasted text into memo entries (从剪贴板导入).
    /// Accepts, per line, either <c>KEY=VALUE</c>, <c>KEY&lt;TAB&gt;VALUE</c>,
    /// <c>KEY,VALUE</c> or <c>KEY VALUE</c>; a bare key with no value is skipped.
    /// Lines beginning with '#' are ignored.
    /// </summary>
    public static List<KeyValuePair<string, string>> ParseClipboard(string text)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string? key = null, value = null;

            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                key = line[..eq].Trim();
                value = line[(eq + 1)..].Trim();
            }
            else
            {
                var sep = line.IndexOfAny(['\t', ',']);
                if (sep < 0)
                {
                    // Fall back to the first run of whitespace.
                    sep = line.IndexOf(' ');
                }
                if (sep > 0)
                {
                    key = line[..sep].Trim();
                    value = line[(sep + 1)..].Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            result.Add(new KeyValuePair<string, string>(key, value));
        }

        return result;
    }
}
