using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using IPScaner.Core.Caching;
using IPScaner.Core.Logging;
using IPScaner.Core.Memo;
using IPScaner.Core.Storage;

namespace IPScaner.Core.Net;

/// <summary>
/// SSH / RDP quick-connect helpers ("远程连接").
/// </summary>
/// <remarks>
/// <para>
/// <b>Command-line safety is the reason this class exists.</b> The original built
/// <c>cmd /c ssh %s</c> from whatever the user had typed, so a host box containing
/// <c>&amp;</c>, a quote or a redirect ran a second command as the logged-on user.
/// Here the host must be a dotted quad or match
/// <c>^[A-Za-z0-9.-]{1,253}$</c>, an optional user name must match
/// <c>^[A-Za-z0-9._\@-]{1,64}$</c>, and the port must be 1–65535; anything else is
/// refused with a log entry and a <c>false</c> return. Nothing that reaches a
/// command line can contain whitespace, a quote or a shell metacharacter.
/// </para>
/// <para>
/// SSH is started as <c>cmd.exe /k ssh user@host -p port</c> so the window stays
/// open after the session ends (a <c>/c</c> window vanishes instantly and hides
/// the error message). RDP uses <c>mstsc.exe</c>; because mstsc has no
/// command-line user switch, a user name is carried in a temporary <c>.rdp</c>
/// file in <c>%TEMP%</c> instead — never in the current directory.
/// </para>
/// <para>No member of this class throws; failures are logged and reported as <c>false</c>/<c>null</c>.</para>
/// </remarks>
public sealed partial class RemoteLauncher
{
    private const string Category = nameof(RemoteLauncher);

    /// <summary>The SSH default port, matching ssh.exe's own default.</summary>
    public const int DefaultSshPort = 22;

    /// <summary>RDP refuses window sizes outside this range; mstsc's own limits are similar.</summary>
    private const int MinRdpDimension = 200;
    private const int MaxRdpDimension = 10000;

    private static readonly TimeSpan HostKeyTtl = TimeSpan.FromHours(1);

    private readonly MemoStore? _memo;

    /// <summary>IP/MAC → friendly name, learned from the memo store and the scan caches.</summary>
    private readonly ExpiringCache<string> _hostKeys = new();

    private bool? _sshAvailable;
    private bool? _rdpAvailable;

    /// <param name="memo">
    /// Optional 备注 store. When supplied, a host that has a note (by MAC first,
    /// then by IP) resolves to that note — the same precedence the scan grid uses.
    /// </param>
    public RemoteLauncher(MemoStore? memo = null) => _memo = memo;

    /// <summary>A host name may contain letters, digits, dots and hyphens only (RFC 952/1123 shape).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9.-]{1,253}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostnameRegex();

    /// <summary>DOMAIN\user and user@domain are both allowed; nothing with whitespace or quotes.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._\\@-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    // ---- availability ------------------------------------------------------

    /// <summary>True when <c>ssh.exe</c> can be launched (Windows 10+ ships the OpenSSH client).</summary>
    public bool IsSshClientAvailable => _sshAvailable ??= ResolveSshClient();

    /// <summary>True when <c>mstsc.exe</c> is present.</summary>
    public bool IsRdpAvailable => _rdpAvailable ??= File.Exists(MstscPath) || ExistsOnPath("mstsc.exe");

    // ---- validation --------------------------------------------------------

    /// <summary>True when the host is a dotted quad or a conservative host name.</summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var text = host.Trim();
        return IpMath.IsValidIPv4(text) || HostnameRegex().IsMatch(text);
    }

    /// <summary>True when the user name is safe to place on a command line.</summary>
    public static bool IsValidUser(string? user) =>
        !string.IsNullOrWhiteSpace(user) && UsernameRegex().IsMatch(user.Trim());

    // ---- SSH ---------------------------------------------------------------

    /// <summary>
    /// Opens an SSH session in a real terminal. The console is kept open after the
    /// session ends so the user can read ssh's output.
    /// </summary>
    public bool LaunchSsh(string host, string? user = null, int port = DefaultSshPort)
    {
        try
        {
            if (!IsValidHost(host))
            {
                Log($"拒绝SSH连接：主机地址不合法 \"{host}\"");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(user) && !IsValidUser(user))
            {
                Log($"拒绝SSH连接：用户名不合法 \"{user}\"");
                return false;
            }

            if (port is < 1 or > 65535)
            {
                Log($"拒绝SSH连接：端口不合法 {port}");
                return false;
            }

            if (!IsSshClientAvailable)
            {
                Log("未找到 ssh.exe：Windows 10 及以上自带 OpenSSH 客户端，可在“设置 → 应用 → 可选功能”中安装。");
                return false;
            }

            var target = string.IsNullOrWhiteSpace(user) ? host.Trim() : $"{user.Trim()}@{host.Trim()}";
            var arguments = $"/k ssh {target} -p {port}";
            Log($"启动SSH：ssh {target} -p {port}");

            // /k (not /c) keeps the terminal on screen after ssh exits, so an
            // authentication error is actually readable instead of flashing past.
            var startInfo = new ProcessStartInfo("cmd.exe", arguments) { UseShellExecute = true };
            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex)
        {
            Log("启动SSH失败: " + ex.Message);
            return false;
        }
    }

    // ---- RDP ---------------------------------------------------------------

    /// <summary>
    /// Opens a Remote Desktop session. <paramref name="user"/> is carried in a
    /// temporary <c>.rdp</c> file because <c>mstsc.exe</c> has no user switch.
    /// </summary>
    public bool LaunchRdp(string host, string? user = null, int? width = null, int? height = null)
    {
        try
        {
            if (!IsValidHost(host))
            {
                Log($"拒绝远程桌面连接：主机地址不合法 \"{host}\"");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(user) && !IsValidUser(user))
            {
                Log($"拒绝远程桌面连接：用户名不合法 \"{user}\"");
                return false;
            }

            if (!IsRdpAvailable)
            {
                Log("未找到 mstsc.exe：无法启动远程桌面连接。");
                return false;
            }

            var name = host.Trim();
            var size = NormalizeSize(width, height);

            if (!string.IsNullOrWhiteSpace(user))
            {
                return LaunchRdpFileCore(name, user.Trim(), size);
            }

            var arguments = $"/v:{name}";
            if (size is not null) arguments += $" /w:{size.Value.Width} /h:{size.Value.Height}";
            Log($"启动远程桌面：mstsc {arguments}");

            var startInfo = new ProcessStartInfo(MstscPath, arguments) { UseShellExecute = true };
            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex)
        {
            Log("启动远程桌面失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Writes a temporary <c>.rdp</c> file into <c>%TEMP%</c> and opens it with
    /// mstsc. The file is deliberately left behind — mstsc reads it at launch, and
    /// deleting it too early makes the connection fail; it never lands in the
    /// current directory.
    /// </summary>
    public bool LaunchRdpFile(string host, string? user = null)
    {
        if (!IsValidHost(host))
        {
            Log($"拒绝远程桌面连接：主机地址不合法 \"{host}\"");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(user) && !IsValidUser(user))
        {
            Log($"拒绝远程桌面连接：用户名不合法 \"{user}\"");
            return false;
        }

        return LaunchRdpFileCore(host.Trim(), string.IsNullOrWhiteSpace(user) ? null : user.Trim(), null);
    }

    /// <summary>
    /// The exact <c>.rdp</c> body written by <see cref="LaunchRdpFile"/>: the full
    /// address, an optional pre-filled user name, a windowed session and no
    /// credential prompt.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The host is not a dotted quad or conservative host name, or the user name is
    /// unsafe. Callers that face the user should use the <c>Launch*</c> members,
    /// which validate and return <c>false</c> instead.
    /// </exception>
    public static string BuildRdpFileContent(string host, string? user = null, int? width = null, int? height = null)
    {
        if (!IsValidHost(host)) throw new ArgumentException($"主机地址不合法：\"{host}\"", nameof(host));
        if (!string.IsNullOrWhiteSpace(user) && !IsValidUser(user))
        {
            throw new ArgumentException($"用户名不合法：\"{user}\"", nameof(user));
        }

        var size = NormalizeSize(width, height);
        var builder = new StringBuilder();
        builder.Append("full address:s:").Append(host.Trim()).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(user)) builder.Append("username:s:").Append(user.Trim()).Append("\r\n");
        builder.Append("screen mode id:i:1\r\n");
        if (size is not null)
        {
            builder.Append("desktopwidth:i:").Append(size.Value.Width).Append("\r\n");
            builder.Append("desktopheight:i:").Append(size.Value.Height).Append("\r\n");
        }
        builder.Append("prompt for credentials:i:0\r\n");
        return builder.ToString();
    }

    // ---- host hints --------------------------------------------------------

    /// <summary>
    /// A cached display hint for a host (the user's 备注 for its MAC, else its
    /// cached reverse-DNS name), or null when nothing is cached. Purely
    /// cache-backed: no DNS query, no ARP lookup and no process is started.
    /// </summary>
    public string? ResolveHostKey(string host)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            var key = host.Trim();

            if (IpMath.IsValidIPv4(key))
            {
                var mac = ScanCaches.MacAddresses.TryGet(key, out var knownMac) ? knownMac : null;

                var note = _memo?.Lookup(mac, key);
                if (!string.IsNullOrEmpty(note))
                {
                    Remember(key, mac, note);
                    return note;
                }

                if (ScanCaches.HostNames.TryGet(key, out var name) &&
                    !string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(name, NameResolver.Unknown, StringComparison.Ordinal))
                {
                    Remember(key, mac, name);
                    return name;
                }

                return _hostKeys.TryGet(key, out var cached) ? cached : null;
            }

            if (MemoStore.IsMacKey(key))
            {
                var canonical = MemoStore.NormalizeKey(key);
                var note = _memo?.Lookup(canonical);
                if (!string.IsNullOrEmpty(note))
                {
                    _hostKeys.Set(canonical, note, HostKeyTtl);
                    return note;
                }

                return _hostKeys.TryGet(canonical, out var cached) ? cached : null;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log("解析主机名缓存失败: " + ex.Message);
            return null;
        }
    }

    // ---- internals ---------------------------------------------------------

    private bool LaunchRdpFileCore(string host, string? user, (int Width, int Height)? size)
    {
        try
        {
            if (!IsRdpAvailable)
            {
                Log("未找到 mstsc.exe：无法启动远程桌面连接。");
                return false;
            }

            var content = BuildRdpFileContent(host, user, size?.Width, size?.Height);
            var path = Path.Combine(Path.GetTempPath(), $"ipscaner-{host}-{Guid.NewGuid():N}.rdp");
            File.WriteAllText(path, content, TextFileEncoding.Utf8NoBom);
            Log($"已生成远程桌面文件：{path}");

            var startInfo = new ProcessStartInfo(path) { UseShellExecute = true };
            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex)
        {
            Log("启动远程桌面文件失败: " + ex.Message);
            return false;
        }
    }

    private void Remember(string ip, string? mac, string hint)
    {
        _hostKeys.Set(ip, hint, HostKeyTtl);
        if (!string.IsNullOrEmpty(mac)) _hostKeys.Set(MemoStore.NormalizeKey(mac), hint, HostKeyTtl);
    }

    /// <summary>Both dimensions or neither: mstsc rejects a half-specified size.</summary>
    private static (int Width, int Height)? NormalizeSize(int? width, int? height)
    {
        if (width is null || height is null) return null;
        if (width is < MinRdpDimension or > MaxRdpDimension) return null;
        if (height is < MinRdpDimension or > MaxRdpDimension) return null;
        return (width.Value, height.Value);
    }

    private static string MstscPath
    {
        get
        {
            var system32 = Environment.SystemDirectory;
            return string.IsNullOrEmpty(system32) ? "mstsc.exe" : Path.Combine(system32, "mstsc.exe");
        }
    }

    private static bool ResolveSshClient()
    {
        if (ExistsOnPath("ssh.exe")) return true;

        // The inbox client normally sits on PATH; check it directly in case PATH was trimmed.
        var system32 = Environment.SystemDirectory;
        return !string.IsNullOrEmpty(system32) && File.Exists(Path.Combine(system32, "OpenSSH", "ssh.exe"));
    }

    private static bool ExistsOnPath(string fileName)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return false;

            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim('"'), fileName))) return true;
                }
                catch
                {
                    // A malformed PATH entry must not stop the search.
                }
            }
        }
        catch
        {
            // Environment access failed — treat the tool as unavailable.
        }

        return false;
    }

    private static void Log(string message) => AppLog.Instance.Log(Category, message);
}
