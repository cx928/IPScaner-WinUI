using System.Diagnostics;
using System.Text;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;
using IPScaner.Core.Storage;

namespace IPScaner.Core.Net;

/// <summary>
/// Reads the saved WLAN profiles and their cleartext keys by scraping
/// <c>netsh wlan show profiles</c> / <c>netsh wlan show profile name="…" key=clear</c>.
/// </summary>
/// <remarks>
/// <para>
/// The original <c>FormWiFiViewer</c> matched the <em>localised</em> markers
/// <c>所有用户配置文件</c> and <c>关键内容</c>, so on an English (or any non-Chinese)
/// Windows every attempt returned <c>null</c> and the list stayed empty. This port
/// is locale-independent: profile names are whatever follows the last colon on a
/// line of the profile listing (header/footer lines have no value after their
/// colon, or no colon at all), and the key is taken from the line whose label is
/// <c>关键内容</c> / <c>Key Content</c> / contains both "key" and "content".
/// Both <c>:</c> (U+003A) and <c>：</c> (U+FF1A) are accepted as separators.
/// </para>
/// <para>
/// Encoding follows the original's Windows 11 24H2 workaround: the process is
/// started with <see cref="Encoding.UTF8"/> and, when that produces no usable
/// names (empty set, or replacement characters from a GBK console), the command is
/// retried with <see cref="Encoding.Default"/> and then
/// <see cref="TextFileEncoding.Gbk"/>; the attempt that produced usable output
/// wins. Pure-ASCII output is accepted immediately because re-decoding it cannot
/// change anything.
/// </para>
/// <para>This class never throws: any failure yields an empty list plus a log entry.</para>
/// </remarks>
public sealed class WifiService
{
    private const string Category = nameof(WifiService);
    private const string Netsh = "netsh";
    private const string ShowProfilesArguments = "wlan show profiles";

    /// <summary>Replacement character produced when a byte sequence cannot be decoded.</summary>
    private const char ReplacementChar = '\uFFFD';

    /// <summary>A single netsh invocation is never expected to take this long.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Known cipher tokens, used to tell the Chinese "密码" (cipher) label from a password label.</summary>
    private static readonly HashSet<string> CipherTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "CCMP", "CCMP-256", "GCMP", "GCMP-256", "TKIP", "WEP", "WEP-40", "WEP-104", "WEP-128",
        "AES", "BIP", "None", "无", "其他", "未知", "Open", "开放",
    };

    /// <summary>
    /// Enumerates every saved WLAN profile (all wireless interfaces) with its
    /// cleartext key when one is stored.
    /// </summary>
    /// <returns>
    /// One <see cref="WifiProfile"/> per unique profile; open networks are included
    /// with <see cref="WifiProfile.IsOpen"/> set and an empty password. Empty on failure.
    /// </returns>
    public async Task<List<WifiProfile>> QueryAsync(CancellationToken ct = default)
    {
        var profiles = new List<WifiProfile>();
        try
        {
            var (names, encoding) = await ReadProfileNamesAsync(ct).ConfigureAwait(false);
            if (names.Count == 0)
            {
                AppLog.Instance.Log(Category, "未读取到任何已保存的WiFi配置文件（netsh 无输出、无无线网卡或全部解码失败）");
                return profiles;
            }

            AppLog.Instance.Log(Category, $"读取到 {names.Count} 个WiFi配置文件，使用编码 {encoding.WebName}");

            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                var profile = await ReadProfileAsync(name, encoding, ct).ConfigureAwait(false);
                if (profile is not null) profiles.Add(profile);
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Instance.Log(Category, $"读取WiFi密码已取消，已返回 {profiles.Count} 条结果");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(Category, "读取WiFi密码失败: " + ex.Message);
        }

        return profiles;
    }

    // ---- profile listing ---------------------------------------------------

    /// <summary>
    /// Runs the profile listing under each candidate encoding until one yields a
    /// usable result, and returns the parsed names with the encoding that produced them.
    /// </summary>
    private static async Task<(List<string> Names, Encoding Encoding)> ReadProfileNamesAsync(CancellationToken ct)
    {
        var candidates = CandidateEncodings();
        var best = new List<string>();
        var bestEncoding = candidates[0];
        var bestScore = int.MinValue;

        foreach (var encoding in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var run = await RunNetshAsync(ShowProfilesArguments, encoding, ct).ConfigureAwait(false);
            if (!run.Started)
            {
                // netsh itself is unavailable — another encoding cannot help.
                AppLog.Instance.Log(Category, $"netsh 启动失败: {run.StandardError}");
                break;
            }

            var names = ParseProfileNames(run.StandardOutput);
            var score = Score(run.StandardOutput, names);
            if (score > bestScore)
            {
                bestScore = score;
                best = names;
                bestEncoding = encoding;
            }

            if (IsUsable(run.StandardOutput, names)) return (names, encoding);

            if (IsPureAscii(run.StandardOutput))
            {
                // ASCII decodes identically under all three encodings; the output
                // really does list no profile, so retrying would only waste spawns.
                AppLog.Instance.Log(Category, "netsh 输出为纯ASCII且未包含配置文件，跳过其余编码重试");
                break;
            }

            AppLog.Instance.Log(Category,
                $"编码 {encoding.WebName} 未能解析出配置文件（退出码 {run.ExitCode}），尝试下一个编码");
        }

        if (best.Count == 0)
        {
            AppLog.Instance.Log(Category, "所有编码均未能解析出WiFi配置文件");
        }

        return (best, bestEncoding);
    }

    /// <summary>
    /// Extracts profile names as "the text after the last colon" of every line that
    /// carries a value. Header/footer lines (<c>Profiles on interface WLAN:</c>, the
    /// <c>-------</c> rules, blank lines) are excluded because they either have no
    /// colon or nothing after it. When the listing contains a rule line, only the
    /// lines below the first rule are considered — that is where the profile entries
    /// always are, in every locale.
    /// </summary>
    internal static List<string> ParseProfileNames(string? output)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(output)) return names;

        var lines = SplitLines(output);
        var start = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsRuleLine(lines[i])) continue;
            start = i + 1;
            break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || IsRuleLine(line)) continue;

            var colon = LastSeparator(line);
            if (colon < 0) continue; // "User profiles", "Group policy profiles (read only)"

            var value = Unquote(line[(colon + 1)..]);
            if (value.Length == 0) continue;  // "Profiles on interface WLAN:"
            if (value.Contains('"')) continue; // cannot be a profile name, and would break the next command line
            if (seen.Add(value)) names.Add(value);
        }

        return names;
    }

    // ---- one profile -------------------------------------------------------

    /// <summary>Reads one profile, retrying the key lookup with the other encodings when needed.</summary>
    private static async Task<WifiProfile?> ReadProfileAsync(string ssid, Encoding preferred, CancellationToken ct)
    {
        var arguments = $"wlan show profile name=\"{ssid}\" key=clear";
        var candidates = OrderedEncodings(preferred);

        NetshRun? last = null;
        var details = default(ProfileDetails);

        foreach (var encoding in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var run = await RunNetshAsync(arguments, encoding, ct).ConfigureAwait(false);
            if (!run.Started) return null;

            last = run;
            details = ParseProfileDetails(run.StandardOutput);

            // Stop as soon as the text decoded cleanly (or is ASCII, which decodes
            // the same everywhere), otherwise try the next encoding.
            if (!ContainsReplacementChars(run.StandardOutput) || IsPureAscii(run.StandardOutput)) break;
        }

        if (last is null) return null;

        if (string.IsNullOrWhiteSpace(last.Value.StandardOutput))
        {
            AppLog.Instance.Log(Category,
                $"读取配置文件 \"{ssid}\" 无输出（退出码 {last.Value.ExitCode}）：{Trim(last.Value.StandardError)}");
            return null;
        }

        return new WifiProfile
        {
            Ssid = ssid,
            Password = details.Password,
            Authentication = details.Authentication,
            Encryption = details.Encryption,
            IsOpen = details.IsOpen,
        };
    }

    /// <summary>Values of the three fields the viewer shows for one profile.</summary>
    internal readonly record struct ProfileDetails(string Password, bool IsOpen, string Authentication, string Encryption);

    /// <summary>
    /// Pulls the cleartext key, the authentication method and the cipher out of one
    /// <c>key=clear</c> listing, locale-independently.
    /// </summary>
    internal static ProfileDetails ParseProfileDetails(string? output)
    {
        var password = string.Empty;
        var authentication = string.Empty;
        var encryption = string.Empty;
        var hasKeyLine = false;
        var keyIsMarker = false;

        foreach (var rawLine in SplitLines(output ?? string.Empty))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var colon = FirstSeparator(line);
            if (colon <= 0) continue; // no label, or the line starts with a colon

            var label = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (label.Length == 0 || value.Length == 0) continue;

            if (IsKeyContentLabel(label))
            {
                hasKeyLine = true;
                password = value;
                keyIsMarker = IsPresenceMarker(value);
                continue;
            }

            if (authentication.Length == 0 && IsAuthenticationLabel(label))
            {
                authentication = value;
                continue;
            }

            if (encryption.Length == 0 && IsEncryptionLabel(label, value))
            {
                encryption = value;
            }
        }

        // A profile with no key line, an empty key, or a key that is really the
        // "present/存在" marker is an open (or key-less) network.
        var isOpen = !hasKeyLine || password.Length == 0 || keyIsMarker;
        if (isOpen) password = string.Empty;

        return new ProfileDetails(password, isOpen, authentication, encryption);
    }

    /// <summary>Label carrying the cleartext key: 关键内容 / Key Content / "key … content".</summary>
    private static bool IsKeyContentLabel(string label)
    {
        if (label.EndsWith("关键内容", StringComparison.Ordinal)) return true;
        if (label.EndsWith("Key Content", StringComparison.OrdinalIgnoreCase)) return true;
        return label.Contains("key", StringComparison.OrdinalIgnoreCase)
               && label.Contains("content", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Label carrying the authentication method: Authentication / 身份验证 / 认证.</summary>
    private static bool IsAuthenticationLabel(string label) =>
        label.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("认证", StringComparison.Ordinal) ||
        label.Contains("身份验证", StringComparison.Ordinal);

    /// <summary>
    /// Label carrying the cipher: Cipher / Encryption / 加密. The Chinese netsh uses
    /// <c>密码</c> for this field as well, so that label is accepted only when its
    /// value is a known cipher token — otherwise it would be mistaken for a password.
    /// </summary>
    private static bool IsEncryptionLabel(string label, string value)
    {
        if (label.Contains("Cipher", StringComparison.OrdinalIgnoreCase)) return true;
        if (label.Contains("Encryption", StringComparison.OrdinalIgnoreCase)) return true;
        if (label.Contains("加密", StringComparison.Ordinal)) return true;
        return label.Equals("密码", StringComparison.Ordinal) && CipherTokens.Contains(value);
    }

    /// <summary>True for the "the key exists" markers netsh prints instead of a real key.</summary>
    private static bool IsPresenceMarker(string value)
    {
        ReadOnlySpan<string> markers =
        [
            "present", "exists", "exists.", "present.",
            "存在", "不存在", "无", "absent", "not present", "none",
        ];
        foreach (var marker in markers)
        {
            if (value.Equals(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---- process plumbing --------------------------------------------------

    /// <summary>Outcome of one netsh invocation.</summary>
    private readonly record struct NetshRun(bool Started, int ExitCode, string StandardOutput, string StandardError);

    /// <summary>Runs netsh with the given encoding, capturing stdout+stderr and waiting for exit.</summary>
    private static async Task<NetshRun> RunNetshAsync(string arguments, Encoding encoding, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(Netsh, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null) return new NetshRun(false, -1, string.Empty, "Process.Start 返回 null");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = new CancellationTokenSource(CommandTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    AppLog.Instance.Log(Category, $"netsh {arguments} 超时（{CommandTimeout.TotalSeconds:0} 秒），已终止");
                    return new NetshRun(false, -1, string.Empty, "netsh 执行超时");
                }
                throw;
            }

            return new NetshRun(true, SafeExitCode(process), await SafeReadAsync(stdoutTask), await SafeReadAsync(stderrTask));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(Category, $"执行 netsh {arguments} 失败: {ex.Message}");
            return new NetshRun(false, -1, string.Empty, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>The encodings to try, in the original's fallback order.</summary>
    private static List<Encoding> CandidateEncodings()
    {
        var list = new List<Encoding>();
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Default, TextFileEncoding.Gbk })
        {
            // Encoding.Default is always UTF-8 on .NET (Core), so an identical
            // attempt would only cost another netsh spawn.
            if (list.Any(e => e.CodePage == encoding.CodePage)) continue;
            list.Add(encoding);
        }
        return list;
    }

    /// <summary>The candidate encodings with <paramref name="preferred"/> first.</summary>
    private static List<Encoding> OrderedEncodings(Encoding preferred)
    {
        var list = new List<Encoding> { preferred };
        foreach (var encoding in CandidateEncodings())
        {
            if (list.Any(e => e.CodePage == encoding.CodePage)) continue;
            list.Add(encoding);
        }
        return list;
    }

    /// <summary>Usable means "names were found and the text decoded cleanly".</summary>
    private static bool IsUsable(string output, List<string> names) =>
        names.Count > 0 && !ContainsReplacementChars(output);

    /// <summary>Ranks an attempt: more names is better, undecodable characters are worse.</summary>
    private static int Score(string output, List<string> names)
    {
        if (output.Length == 0) return 0;
        var replacements = 0;
        foreach (var c in output) if (c == ReplacementChar) replacements++;
        return (names.Count * 100) - Math.Min(replacements, 99);
    }

    private static bool ContainsReplacementChars(string output) => output.Contains(ReplacementChar);

    private static bool IsPureAscii(string text)
    {
        foreach (var c in text) if (c > 0x7F) return false;
        return true;
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n' && text[i] != '\r') continue;
            lines.Add(text[start..i]);
            if (i != text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        return lines;
    }

    /// <summary>True for a "--------" rule line (the section underlines netsh prints).</summary>
    private static bool IsRuleLine(string line)
    {
        var seenDash = false;
        foreach (var c in line)
        {
            if (c is '-' or '=' or '_') { seenDash = true; continue; }
            if (char.IsWhiteSpace(c) || c == '\uFEFF') continue;
            return false;
        }
        return seenDash;
    }

    /// <summary>Index of the last <c>:</c> / <c>：</c> on the line, or -1.</summary>
    private static int LastSeparator(string line)
    {
        for (var i = line.Length - 1; i >= 0; i--)
        {
            if (line[i] is ':' or '：') return i;
        }
        return -1;
    }

    /// <summary>Index of the first <c>:</c> / <c>：</c> on the line, or -1.</summary>
    private static int FirstSeparator(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] is ':' or '：') return i;
        }
        return -1;
    }

    /// <summary>Trims whitespace/BOM and removes one layer of surrounding double quotes.</summary>
    private static string Unquote(string value)
    {
        var text = value.Trim().Trim('\uFEFF').Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') return text[1..^1].Trim();
        return text;
    }

    private static string Trim(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();

    private static async Task<string> SafeReadAsync(Task<string> read)
    {
        try { return await read.ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch { return -1; }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone, or not ours to kill
        }
    }
}
