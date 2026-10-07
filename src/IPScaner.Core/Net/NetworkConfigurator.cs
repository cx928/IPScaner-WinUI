using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using IPScaner.Core.Logging;
using IPScaner.Core.Storage;

namespace IPScaner.Core.Net;

/// <summary>Outcome of one network-configuration command.</summary>
/// <param name="Success">True only when the underlying <c>netsh</c> process exited with code 0.</param>
/// <param name="Message">Human-readable (Chinese) result, including netsh's own error text on failure.</param>
/// <param name="ExitCode">netsh's exit code, or <see cref="NetworkConfigurator.NotRunExitCode"/> when nothing was executed.</param>
public readonly record struct NetConfigResult(bool Success, string Message, int ExitCode);

/// <summary>
/// Applies IP / subnet mask / gateway / DNS / adapter-state changes through
/// <c>netsh</c>, the way the original <c>FormLocalIP</c> did.
/// </summary>
/// <remarks>
/// <para>
/// Behavioural fix over the original: <c>FormLocalIP.ExceCmd</c> wrote the command
/// into <c>cmd.exe</c>'s standard input, never waited and never looked at an exit
/// code, so it always reported 本地IP地址修改成功 even when netsh had rejected the
/// values. Here every command is executed directly (no cmd.exe), awaited, and its
/// stdout+stderr plus exit code are captured; <see cref="NetConfigResult.Success"/>
/// is <c>ExitCode == 0</c> and the message carries netsh's real text.
/// </para>
/// <para>
/// Inputs are validated with <see cref="IpMath.IsValidIPv4"/> <em>before</em> any
/// process is started; a malformed address returns a failure result with exit code
/// <see cref="NotRunExitCode"/> and spawns nothing.
/// </para>
/// <para>
/// netsh output is decoded with <see cref="TextFileEncoding.Gbk"/> (code page 936)
/// — its localized text on Chinese Windows — for both stdout and stderr, while
/// still accepting a UTF-8 console (see <c>DecodeNetshBytes</c>).
/// <c>set address</c>/<c>set dns</c>/<c>set interface</c> require an elevated
/// process; when the exit code or the output indicates access denied the message
/// says so explicitly.
/// </para>
/// </remarks>
public sealed class NetworkConfigurator
{
    /// <summary>Exit code used when no process was spawned (validation failure, or netsh could not start).</summary>
    public const int NotRunExitCode = -1;

    private const string Category = nameof(NetworkConfigurator);
    private const string Netsh = "netsh";

    /// <summary>A netsh configuration command normally returns in well under a second.</summary>
    private const int CommandTimeoutSeconds = 60;

    /// <summary>Grace period given to netsh to finish when the caller cancels mid-flight.</summary>
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(5);

    // The exact command shapes the original issued (legacy "interface ip" namespace,
    // every value quoted) — quoted identically so netsh's behaviour and error text match.
    private static readonly string[] AccessDeniedMarkers =
    [
        "需要提升", "提升", "需要管理员", "以管理员身份", "管理员权限", "拒绝访问", "访问被拒绝",
        "requires elevation", "elevated", "access is denied", "access denied", "administrator",
    ];

    // ---- IP address --------------------------------------------------------

    /// <summary>
    /// Sets a static IPv4 address. The gateway argument is omitted entirely when
    /// <paramref name="gateway"/> is null or empty, matching netsh's optional 4th value.
    /// </summary>
    public async Task<NetConfigResult> SetStaticAsync(
        string adapterName, string ip, string subnetMask, string? gateway, CancellationToken ct = default)
    {
        if (!ValidateAdapterName(adapterName, out var nameError)) return nameError;
        if (!IpMath.IsValidIPv4(ip))
            return Failure($"IP地址格式不正确：\"{ip}\"（应为 192.168.1.100 这样的格式）");

        if (!IpMath.IsValidIPv4(subnetMask))
            return Failure($"子网掩码格式不正确：\"{subnetMask}\"（应为 255.255.255.0 这样的格式）");

        var gw = gateway?.Trim() ?? string.Empty;
        if (gw.Length > 0 && !IpMath.IsValidIPv4(gw))
            return Failure($"网关地址格式不正确：\"{gateway}\"（应为 192.168.1.1 这样的格式）");

        var arguments = $"interface ip set address \"{adapterName}\" \"static\" \"{ip}\" \"{subnetMask}\"";
        if (gw.Length > 0) arguments += $" \"{gw}\"";

        var description = gw.Length > 0
            ? $"{adapterName} → IP {ip}，掩码 {subnetMask}，网关 {gw}"
            : $"{adapterName} → IP {ip}，掩码 {subnetMask}";
        return await RunAsync(arguments, description, ct).ConfigureAwait(false);
    }

    /// <summary>Switches the adapter back to DHCP: <c>netsh interface ip set address "&lt;name&gt;" "dhcp"</c>.</summary>
    public async Task<NetConfigResult> SetDhcpAsync(string adapterName, CancellationToken ct = default)
    {
        if (!ValidateAdapterName(adapterName, out var nameError)) return nameError;

        var arguments = $"interface ip set address \"{adapterName}\" \"dhcp\"";
        return await RunAsync(arguments, $"{adapterName} → 自动获取 IP 地址", ct).ConfigureAwait(false);
    }

    // ---- DNS ---------------------------------------------------------------

    /// <summary>
    /// Sets static DNS servers: the first one with <c>set dns … static</c>, every
    /// further one with <c>add dns … index=N</c> (N starting at 2), as the original did.
    /// </summary>
    public async Task<NetConfigResult> SetDnsStaticAsync(
        string adapterName, IReadOnlyList<string> dnsServers, CancellationToken ct = default)
    {
        if (!ValidateAdapterName(adapterName, out var nameError)) return nameError;
        if (dnsServers is null || dnsServers.Count == 0)
            return Failure("请输入DNS服务器地址");

        for (var i = 0; i < dnsServers.Count; i++)
        {
            if (!IpMath.IsValidIPv4(dnsServers[i]))
                return Failure($"第 {i + 1} 个DNS地址格式不正确：\"{dnsServers[i]}\"");
        }

        var primary = dnsServers[0].Trim();
        var primaryArguments = $"interface ip set dns \"{adapterName}\" \"static\" \"{primary}\"";
        var primaryResult = await RunAsync(primaryArguments, $"{adapterName} → 主DNS {primary}", ct).ConfigureAwait(false);
        if (!primaryResult.Success) return primaryResult;

        for (var i = 1; i < dnsServers.Count; i++)
        {
            var server = dnsServers[i].Trim();
            var index = i + 1;
            var arguments = $"interface ip add dns \"{adapterName}\" \"{server}\" index={index}";
            var result = await RunAsync(arguments, $"{adapterName} → 备用DNS {server}（index={index}）", ct).ConfigureAwait(false);
            if (result.Success) continue;

            return new NetConfigResult(
                false,
                $"主DNS {primary} 设置成功，但添加第 {index} 个DNS {server} 失败：{result.Message}",
                result.ExitCode);
        }

        var dnsText = string.Join(", ", dnsServers.Select(d => d.Trim()));
        return new NetConfigResult(true, $"设置成功：{adapterName} 的DNS服务器 → {dnsText}", 0);
    }

    /// <summary>Switches DNS back to DHCP: <c>netsh interface ip set dns "&lt;name&gt;" "dhcp"</c>.</summary>
    public async Task<NetConfigResult> SetDnsDhcpAsync(string adapterName, CancellationToken ct = default)
    {
        if (!ValidateAdapterName(adapterName, out var nameError)) return nameError;

        var arguments = $"interface ip set dns \"{adapterName}\" \"dhcp\"";
        return await RunAsync(arguments, $"{adapterName} → 自动获取 DNS 服务器", ct).ConfigureAwait(false);
    }

    // ---- adapter state -----------------------------------------------------

    /// <summary>Enables the adapter: <c>netsh interface set interface "&lt;name&gt;" enable</c>.</summary>
    public async Task<NetConfigResult> EnableAdapterAsync(string adapterName, CancellationToken ct = default)
    {
        if (!ValidateAdapterName(adapterName, out var nameError)) return nameError;

        var arguments = $"interface set interface \"{adapterName}\" enable";
        return await RunAsync(arguments, $"网卡 {adapterName} 已启用", ct).ConfigureAwait(false);
    }

    /// <summary>Disables the adapter: <c>netsh interface set interface "&lt;name&gt;" disable</c>.</summary>
    public async Task<NetConfigResult> DisableAdapterAsync(string adapterName, CancellationToken ct = default)
    {
        if (!ValidateAdapterName(adapterName, out var nameError)) return nameError;

        var arguments = $"interface set interface \"{adapterName}\" disable";
        return await RunAsync(arguments, $"网卡 {adapterName} 已禁用", ct).ConfigureAwait(false);
    }

    // ---- execution ---------------------------------------------------------

    /// <summary>
    /// Runs one netsh command line, waits for it to exit and turns the exit code plus
    /// the captured text into a <see cref="NetConfigResult"/>. Never throws.
    /// </summary>
    private static async Task<NetConfigResult> RunAsync(string arguments, string description, CancellationToken ct)
    {
        AppLog.Instance.Log(Category, $"执行 netsh {arguments}");

        Process? process = null;
        try
        {
            // netsh's native output code page on Chinese Windows; also the decoder's
            // fallback when the captured bytes turn out not to be UTF-8 (see DecodeNetshBytes).
            var consoleEncoding = TextFileEncoding.Gbk;

            var startInfo = new ProcessStartInfo(Netsh, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = consoleEncoding,
                StandardErrorEncoding = consoleEncoding,
            };

            process = Process.Start(startInfo);
            if (process is null)
                return new NetConfigResult(false, "无法启动 netsh。", NotRunExitCode);

            var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, ct);
            var stderrTask = ReadAllBytesAsync(process.StandardError.BaseStream, ct);

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeoutSeconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            {
                try
                {
                    // Drain both pipes first so a chatty netsh cannot block on a full
                    // pipe buffer while we wait for it to exit.
                    await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linked.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    var timedOut = timeout.IsCancellationRequested && !ct.IsCancellationRequested;
                    if (!timedOut) await WaitForGracefulExitAsync(process).ConfigureAwait(false);
                    var wasKilled = TryKill(process);
                    var code = SafeExitCode(process);
                    var reason = timedOut
                        ? $"netsh 执行超时（{CommandTimeoutSeconds} 秒），已终止：netsh {arguments}"
                        : "操作已取消。";
                    AppLog.Instance.Log(Category, $"{reason}（退出码 {code}，已终止={wasKilled}）");
                    return new NetConfigResult(false, reason, code);
                }
            }

            var stdout = DecodeNetshBytes(await stdoutTask.ConfigureAwait(false), consoleEncoding);
            var stderr = DecodeNetshBytes(await stderrTask.ConfigureAwait(false), consoleEncoding);
            var exitCode = SafeExitCode(process);
            var detail = Condense(stdout, stderr);

            if (exitCode == 0)
            {
                var message = $"设置成功：{description}";
                if (detail.Length > 0) message += $"（netsh: {detail}）";
                AppLog.Instance.Log(Category, message);
                return new NetConfigResult(true, message, 0);
            }

            if (IsAccessDenied(exitCode, detail))
            {
                var denied = "修改网络配置需要管理员权限（访问被拒绝）。请以管理员身份重新运行本程序后再试。"
                             + (detail.Length > 0 ? $" netsh: {detail}" : $" 退出码 {exitCode}。");
                AppLog.Instance.Log(Category, "netsh 访问被拒绝: " + arguments);
                return new NetConfigResult(false, denied, exitCode);
            }

            var failure = $"设置失败（netsh 退出码 {exitCode}）：{description}"
                          + (detail.Length > 0 ? $"。netsh 返回：{detail}" : "。netsh 未返回任何信息。");
            AppLog.Instance.Log(Category, failure);
            return new NetConfigResult(false, failure, exitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            AppLog.Instance.Log(Category, "启动 netsh 被拒绝: " + ex.Message);
            return new NetConfigResult(
                false, "修改网络配置需要管理员权限（无法启动 netsh，访问被拒绝）。请以管理员身份重新运行本程序后再试。", 5);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(Category, $"执行 netsh {arguments} 失败: {ex.Message}");
            return new NetConfigResult(false, $"执行 netsh 失败：{ex.Message}", NotRunExitCode);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>True when the exit code or the captured text says "elevation required".</summary>
    private static bool IsAccessDenied(int exitCode, string detail)
    {
        if (exitCode == 5) return true; // ERROR_ACCESS_DENIED
        if (detail.Length == 0) return false;
        foreach (var marker in AccessDeniedMarkers)
        {
            if (detail.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// A user-issued cancellation should not cut a configuration write in half, so
    /// netsh gets a few seconds to finish on its own before it is killed.
    /// </summary>
    private static async Task WaitForGracefulExitAsync(Process process)
    {
        try
        {
            using var grace = new CancellationTokenSource(CancelGrace);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch
        {
            // still running (or already gone) — the caller kills it
        }
    }

    /// <summary>Collapses netsh's multi-line output into one readable line.</summary>
    private static string Condense(string stdout, string stderr)
    {
        var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var parts = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", parts);
    }

    /// <summary>Strict UTF-8: throws on byte sequences that are not valid UTF-8.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes captured netsh bytes. <paramref name="fallback"/> is the configured
    /// console code page (<see cref="TextFileEncoding.Gbk"/> — what netsh writes on a
    /// Chinese system), but netsh writes UTF-8 where a UTF-8 console is active (the
    /// "Beta: Use Unicode UTF-8 for worldwide language support" option, Windows 11
    /// 24H2 behaviour), and GBK-decoding those bytes yields mojibake. Valid UTF-8 is a
    /// decisive signal — Chinese GBK text practically never forms valid UTF-8 — so it
    /// is preferred, and the configured code page is used for everything else.
    /// (<see cref="ProcessStartInfo.StandardOutputEncoding"/> /
    /// <see cref="ProcessStartInfo.StandardErrorEncoding"/> are still set to GBK; this
    /// is the same setting applied to the raw bytes.)
    /// </summary>
    private static string DecodeNetshBytes(byte[] bytes, Encoding fallback)
    {
        if (bytes.Length == 0) return string.Empty;

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return fallback.GetString(bytes);
        }
    }

    /// <summary>Reads a redirected pipe to EOF as raw bytes.</summary>
    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static bool ValidateAdapterName(string? adapterName, out NetConfigResult error)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            error = Failure("网卡名称不能为空");
            return false;
        }

        if (adapterName.Contains('"'))
        {
            error = Failure($"网卡名称不合法（不能包含引号）：\"{adapterName}\"");
            return false;
        }

        error = default;
        return true;
    }

    private static NetConfigResult Failure(string message) => new(false, message, NotRunExitCode);

    private static int SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : NotRunExitCode; }
        catch { return NotRunExitCode; }
    }

    private static bool TryKill(Process process)
    {
        try
        {
            if (process.HasExited) return false;
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
