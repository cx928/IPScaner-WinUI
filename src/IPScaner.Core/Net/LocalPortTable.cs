using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;

namespace IPScaner.Core.Net;

/// <summary>
/// Enumerates the machine's local TCP/UDP endpoint table (本机端口占用查看) through the
/// Windows IP Helper API, with a <c>netstat -ano</c> shell-out kept only as a fallback.
/// </summary>
/// <remarks>
/// <para>
/// The original WinForms tool ran <c>cmd.exe /c netstat -ano | findstr TCP|UDP</c> twice per
/// refresh and regex-parsed stdout, which hid every IPv6 row, discarded the TCP state column,
/// discarded the remote endpoint, dropped rows whose process could not be inspected and broke
/// on non-English output. This implementation instead calls <c>GetExtendedTcpTable</c> /
/// <c>GetExtendedUdpTable</c> with the <c>*_OWNER_PID_*</c> table classes, so IPv6 rows, the TCP
/// state and the owning PID all come straight from the OS. Rows that cannot be attributed to a
/// process are kept with <see cref="LocalPortInfo.ProcessMissing"/> set rather than dropped.
/// </para>
/// <para>
/// If the P/Invoke path fails as a whole (older OS, blocked DLL, unexpected win32 error) or
/// returns nothing at all, the enumeration falls back to parsing <c>netstat -ano</c>; the parser
/// is structural (it never matches localized header text) so Chinese and English output are
/// handled identically, IPv6 lines included.
/// </para>
/// <para>
/// Set the environment variable <c>IPSCANER_LOCALPORT_FORCE_FALLBACK=1</c> to force the
/// <c>netstat</c> path — used for diagnostics and for comparing both paths on one machine.
/// </para>
/// </remarks>
public sealed partial class LocalPortTable
{
    // ── IP Helper / socket constants ────────────────────────────────────────
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const uint TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>Size of the <c>DWORD dwNumEntries</c> header that precedes every MIB table.</summary>
    private const int TableHeaderBytes = 4;

    private const int MaxTableQueryAttempts = 3;
    private const long MaxTableBytes = 64L * 1024 * 1024;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int MaxProcessImagePathChars = 1024;
    private const int KillWaitMilliseconds = 3000;
    private const int NetstatTimeoutMilliseconds = 15000;

    private const string ForceFallbackVariable = "IPSCANER_LOCALPORT_FORCE_FALLBACK";

    /// <summary>State text used when a TCP state cannot be named.</summary>
    private const string UnknownState = "未知";

    /// <summary>
    /// netstat state words (English and localized) mapped onto the same Chinese labels the
    /// P/Invoke path produces. Unknown words are passed through unchanged so a locale we have
    /// never seen still shows something meaningful.
    /// </summary>
    private static readonly Dictionary<string, string> NetstatStateLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CLOSED"] = "已关闭",
        ["已关闭"] = "已关闭",
        ["LISTENING"] = "侦听",
        ["LISTEN"] = "侦听",
        ["侦听"] = "侦听",
        ["监听"] = "侦听",
        ["SYN_SENT"] = "已发送SYN",
        ["SYN-SENT"] = "已发送SYN",
        ["已发送SYN"] = "已发送SYN",
        ["SYN_RECEIVED"] = "已接收SYN",
        ["SYN_RCVD"] = "已接收SYN",
        ["SYN_RECV"] = "已接收SYN",
        ["已接收SYN"] = "已接收SYN",
        ["ESTABLISHED"] = "已建立",
        ["ESTAB"] = "已建立",
        ["已建立"] = "已建立",
        ["FIN_WAIT_1"] = "FIN等待1",
        ["FIN_WAIT1"] = "FIN等待1",
        ["FIN等待1"] = "FIN等待1",
        ["FIN_WAIT_2"] = "FIN等待2",
        ["FIN_WAIT2"] = "FIN等待2",
        ["FIN等待2"] = "FIN等待2",
        ["CLOSE_WAIT"] = "关闭等待",
        ["关闭等待"] = "关闭等待",
        ["CLOSING"] = "正在关闭",
        ["正在关闭"] = "正在关闭",
        ["LAST_ACK"] = "最后确认",
        ["最后确认"] = "最后确认",
        ["TIME_WAIT"] = "时间等待",
        ["时间等待"] = "时间等待",
        ["DELETE_TCB"] = "删除",
        ["删除"] = "删除",
    };

    private static readonly Lazy<Encoding> NetstatOutputEncoding = new(ResolveNetstatEncoding);

    /// <summary>
    /// Enumerates local TCP/UDP endpoints with owning-process info.
    /// </summary>
    /// <param name="includeTcp">Include TCP endpoints.</param>
    /// <param name="includeUdp">Include UDP endpoints.</param>
    /// <param name="includeIpv6">
    /// Include IPv6 endpoints. Off by default so the grid matches the original tool's IPv4-only
    /// view; IPv6 rows never affect the IPv4 ones.
    /// </param>
    /// <param name="ct">Cancels the enumeration.</param>
    /// <returns>
    /// The endpoint rows, sorted by protocol, then local port, then PID. Never null; an empty
    /// list means the machine reported no endpoints (or that even the fallback failed, which is
    /// logged).
    /// </returns>
    public async Task<IReadOnlyList<LocalPortInfo>> QueryAsync(
        bool includeTcp = true,
        bool includeUdp = true,
        bool includeIpv6 = false,
        CancellationToken ct = default)
    {
        return await Task.Run(() => Query(includeTcp, includeUdp, includeIpv6, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort process kill used by the double-click-to-kill feature.
    /// </summary>
    /// <param name="pid">Process id taken from a <see cref="LocalPortInfo"/> row.</param>
    /// <param name="error">Human-readable Chinese reason when the kill fails; empty on success.</param>
    /// <returns><c>true</c> when the process was killed and confirmed gone.</returns>
    /// <remarks>
    /// Mirrors the original flow (<c>Process.GetProcessById(pid).Kill()</c> then a 3 s wait) but
    /// reports failures instead of throwing. Unlike the original it refuses to kill this very
    /// process, and it never kills the child processes of the target.
    /// </remarks>
    public static bool TryKillProcess(int pid, out string error)
    {
        error = string.Empty;

        if (pid <= 0)
        {
            error = $"无效的进程 ID：{pid}。";
            return false;
        }

        if (pid == Environment.ProcessId)
        {
            error = "不能结束本程序自身的进程。";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            if (!process.WaitForExit(KillWaitMilliseconds))
            {
                error = $"已请求结束进程 {pid}，但在 {KillWaitMilliseconds / 1000} 秒内未确认退出，可能权限不足。";
                AppLog.Instance.Log(nameof(LocalPortTable), error);
                return false;
            }

            AppLog.Instance.Log(nameof(LocalPortTable), $"已结束进程 {pid}。");
            return true;
        }
        catch (ArgumentException)
        {
            error = $"进程 {pid} 不存在或已退出。";
        }
        catch (Win32Exception ex)
        {
            error = $"结束进程 {pid} 失败，拒绝访问或权限不足：{ex.Message}";
        }
        catch (InvalidOperationException)
        {
            error = $"进程 {pid} 已经退出。";
        }
        catch (NotSupportedException ex)
        {
            error = $"当前系统不支持结束进程 {pid}：{ex.Message}";
        }
        catch (Exception ex)
        {
            error = $"结束进程 {pid} 失败：{ex.Message}";
        }

        AppLog.Instance.Log(nameof(LocalPortTable), error);
        return false;
    }

    // ── Orchestration ───────────────────────────────────────────────────────

    private static IReadOnlyList<LocalPortInfo> Query(
        bool includeTcp,
        bool includeUdp,
        bool includeIpv6,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var rows = new List<LocalPortInfo>(256);
        var cache = new Dictionary<int, ProcessIdentity>();
        var wantRows = includeTcp || includeUdp;

        var forceFallback = IsFallbackForced();
        if (forceFallback)
        {
            AppLog.Instance.Log(nameof(LocalPortTable), $"环境变量 {ForceFallbackVariable} 已设置，直接使用 netstat 回退。");
        }
        else
        {
            // A failed table invalidates the whole native snapshot: mixing a partial native
            // result with a netstat one would duplicate rows.
            var nativeOk = true;
            if (includeTcp) nativeOk &= TryCollect(() => AppendTcp(rows, includeIpv6, ct), "TCP");
            if (includeUdp) nativeOk &= TryCollect(() => AppendUdp(rows, includeIpv6, ct), "UDP");

            if (!nativeOk || (wantRows && rows.Count == 0))
            {
                AppLog.Instance.Log(nameof(LocalPortTable), "IP Helper API 未返回可用数据，改用 netstat 回退。");
                rows.Clear();
            }
            else
            {
                Finish(rows, cache, ct);
                return rows;
            }
        }

        rows.AddRange(CollectNetstat(includeTcp, includeUdp, includeIpv6, ct));
        Finish(rows, cache, ct);
        return rows;
    }

    private static void Finish(List<LocalPortInfo> rows, Dictionary<int, ProcessIdentity> cache, CancellationToken ct)
    {
        AttachProcessInfo(rows, cache, ct);
        SortRows(rows);
    }

    /// <summary>Runs one table append, converting any failure into a logged <c>false</c>.</summary>
    private static bool TryCollect(Action append, string table)
    {
        try
        {
            append();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(LocalPortTable), $"枚举 {table} 表失败：{ex}");
            return false;
        }
    }

    private static bool IsFallbackForced()
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(ForceFallbackVariable);
            return !string.IsNullOrEmpty(value)
                && !string.Equals(value, "0", StringComparison.Ordinal)
                && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ── Native enumeration (iphlpapi) ───────────────────────────────────────

    private static void AppendTcp(List<LocalPortInfo> rows, bool includeIpv6, CancellationToken ct)
    {
        AppendTcp4(rows, ct);

        // IPv6 problems must never invalidate the IPv4 result.
        if (includeIpv6) TryCollect(() => AppendTcp6(rows, ct), "TCP6");
    }

    private static void AppendUdp(List<LocalPortInfo> rows, bool includeIpv6, CancellationToken ct)
    {
        AppendUdp4(rows, ct);
        if (includeIpv6) TryCollect(() => AppendUdp6(rows, ct), "UDP6");
    }

    private static void AppendTcp4(List<LocalPortInfo> rows, CancellationToken ct)
    {
        var data = QueryTable(QueryTcpTable4);
        if (data is null) return;

        var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
        var count = ReadRowCount(data, rowSize);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var start = handle.AddrOfPinnedObject();
            for (var i = 0; i < count; i++)
            {
                if ((i & 0x3F) == 0) ct.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(IntPtr.Add(start, TableHeaderBytes + (i * rowSize)));
                rows.Add(new LocalPortInfo
                {
                    Protocol = "TCP",
                    LocalAddress = FormatIPv4(row.LocalAddr),
                    LocalPort = ReadPort(row.LocalPort),
                    RemoteAddress = FormatIPv4(row.RemoteAddr),
                    RemotePort = ReadPort(row.RemotePort),
                    State = MapTcpState(row.State),
                    Pid = unchecked((int)row.OwningPid),
                });
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private static void AppendTcp6(List<LocalPortInfo> rows, CancellationToken ct)
    {
        var data = QueryTable(QueryTcpTable6);
        if (data is null) return;

        var rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
        var count = ReadRowCount(data, rowSize);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var start = handle.AddrOfPinnedObject();
            for (var i = 0; i < count; i++)
            {
                if ((i & 0x3F) == 0) ct.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(IntPtr.Add(start, TableHeaderBytes + (i * rowSize)));
                rows.Add(new LocalPortInfo
                {
                    Protocol = "TCP",
                    LocalAddress = FormatIPv6(row.LocalAddr, row.LocalScopeId),
                    LocalPort = ReadPort(row.LocalPort),
                    RemoteAddress = FormatIPv6(row.RemoteAddr, row.RemoteScopeId),
                    RemotePort = ReadPort(row.RemotePort),
                    State = MapTcpState(row.State),
                    Pid = unchecked((int)row.OwningPid),
                });
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private static void AppendUdp4(List<LocalPortInfo> rows, CancellationToken ct)
    {
        var data = QueryTable(QueryUdpTable4);
        if (data is null) return;

        var rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
        var count = ReadRowCount(data, rowSize);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var start = handle.AddrOfPinnedObject();
            for (var i = 0; i < count; i++)
            {
                if ((i & 0x3F) == 0) ct.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(IntPtr.Add(start, TableHeaderBytes + (i * rowSize)));
                rows.Add(new LocalPortInfo
                {
                    Protocol = "UDP",
                    LocalAddress = FormatIPv4(row.LocalAddr),
                    LocalPort = ReadPort(row.LocalPort),
                    RemoteAddress = "*",
                    RemotePort = 0,
                    State = LocalPortInfo.UdpState,
                    Pid = unchecked((int)row.OwningPid),
                });
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private static void AppendUdp6(List<LocalPortInfo> rows, CancellationToken ct)
    {
        var data = QueryTable(QueryUdpTable6);
        if (data is null) return;

        var rowSize = Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
        var count = ReadRowCount(data, rowSize);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var start = handle.AddrOfPinnedObject();
            for (var i = 0; i < count; i++)
            {
                if ((i & 0x3F) == 0) ct.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(IntPtr.Add(start, TableHeaderBytes + (i * rowSize)));
                rows.Add(new LocalPortInfo
                {
                    Protocol = "UDP",
                    LocalAddress = FormatIPv6(row.LocalAddr, row.LocalScopeId),
                    LocalPort = ReadPort(row.LocalPort),
                    RemoteAddress = "*",
                    RemotePort = 0,
                    State = LocalPortInfo.UdpState,
                    Pid = unchecked((int)row.OwningPid),
                });
            }
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Two-call pattern: ask for the required size, allocate, then fill. A table that grows
    /// between the two calls answers <c>ERROR_INSUFFICIENT_BUFFER</c> again, in which case the
    /// query is retried with the newly reported size.
    /// </summary>
    private static byte[]? QueryTable(TableQuery query)
    {
        uint result;
        uint size = 0;
        try
        {
            result = query(IntPtr.Zero, ref size);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException("无法加载 IP Helper API（iphlpapi.dll）。", ex);
        }

        for (var attempt = 0; ; attempt++)
        {
            if (result == 0 && size == 0) return null; // empty table
            if (result != 0 && result != ERROR_INSUFFICIENT_BUFFER)
            {
                throw new Win32Exception((int)result, $"GetExtendedTable 失败（错误码 {result}）。");
            }

            if (size == 0) return null;
            if (size > MaxTableBytes)
            {
                throw new Win32Exception((int)result, $"GetExtendedTable 报告的缓冲区大小异常（{size} 字节）。");
            }

            if (attempt >= MaxTableQueryAttempts)
            {
                throw new Win32Exception((int)result, "GetExtendedTable 反复报告缓冲区不足。");
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var capacity = size;
                result = query(buffer, ref capacity);
                if (result == 0)
                {
                    var length = (int)Math.Min(capacity, size);
                    var data = new byte[length];
                    Marshal.Copy(buffer, data, 0, length);
                    return data;
                }

                if (result != ERROR_INSUFFICIENT_BUFFER)
                {
                    throw new Win32Exception((int)result, $"GetExtendedTable 失败（错误码 {result}）。");
                }

                // The table grew between the size probe and the fill: retry bigger.
                size = Math.Max(capacity, size * 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Reads <c>dwNumEntries</c>, clamped by how many whole rows the buffer actually holds so a
    /// struct-size surprise can never read past the end of the allocation.
    /// </summary>
    private static int ReadRowCount(byte[] data, int rowSize)
    {
        if (data.Length < TableHeaderBytes || rowSize <= 0) return 0;

        var declared = BitConverter.ToInt32(data, 0);
        if (declared <= 0) return 0;

        var available = (data.Length - TableHeaderBytes) / rowSize;
        if (declared > available)
        {
            AppLog.Instance.Log(
                nameof(LocalPortTable),
                $"表项数量 {declared} 超过缓冲区可容纳的 {available} 行，已截断（行大小 {rowSize} 字节）。");
            return available;
        }

        return declared;
    }

    // ── Native interop ──────────────────────────────────────────────────────

    private delegate uint TableQuery(IntPtr table, ref uint size);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref uint pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        uint ulAf,
        uint tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref uint pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        uint ulAf,
        uint tableClass,
        uint reserved);

    /// <summary>IPv4 TCP table, <c>TCP_TABLE_OWNER_PID_ALL</c>.</summary>
    private static uint QueryTcpTable4(IntPtr table, ref uint size) =>
        GetExtendedTcpTable(table, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

    /// <summary>IPv6 TCP table, <c>TCP_TABLE_OWNER_PID_ALL</c>.</summary>
    private static uint QueryTcpTable6(IntPtr table, ref uint size) =>
        GetExtendedTcpTable(table, ref size, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);

    /// <summary>IPv4 UDP table, <c>UDP_TABLE_OWNER_PID</c>.</summary>
    private static uint QueryUdpTable4(IntPtr table, ref uint size) =>
        GetExtendedUdpTable(table, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);

    /// <summary>IPv6 UDP table, <c>UDP_TABLE_OWNER_PID</c>.</summary>
    private static uint QueryUdpTable6(IntPtr table, ref uint size) =>
        GetExtendedUdpTable(table, ref size, false, AF_INET6, UDP_TABLE_OWNER_PID, 0);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        StringBuilder lpExeName,
        ref uint lpdwSize);

    /// <summary>MIB_TCPROW_OWNER_PID — 24 bytes (6 DWORDs).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>MIB_TCP6ROW_OWNER_PID — 56 bytes (16 + 4 + 4 + 16 + 4 + 4 + 4 + 4).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;

        public uint LocalScopeId;
        public uint LocalPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;

        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    /// <summary>MIB_UDPROW_OWNER_PID — 12 bytes (3 DWORDs).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    /// <summary>MIB_UDP6ROW_OWNER_PID — 28 bytes (16 + 4 + 4 + 4).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;

        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    // ── Address / state formatting ──────────────────────────────────────────

    /// <summary>Formats a network-order IPv4 DWORD as a dotted quad.</summary>
    private static string FormatIPv4(uint address)
    {
        var host = unchecked((uint)IPAddress.NetworkToHostOrder(unchecked((int)address)));
        return $"{(host >> 24) & 0xFF}.{(host >> 16) & 0xFF}.{(host >> 8) & 0xFF}.{host & 0xFF}";
    }

    /// <summary>Formats a 16-byte IPv6 address, keeping the scope id when it is meaningful.</summary>
    private static string FormatIPv6(byte[]? address, uint scopeId)
    {
        if (address is null || address.Length < 16) return string.Empty;

        try
        {
            var ip = scopeId == 0 ? new IPAddress(address) : new IPAddress(address, scopeId);
            return ip.ToString();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(LocalPortTable), "IPv6 地址格式化失败：" + ex.Message);
            return string.Empty;
        }
    }

    /// <summary>Ports arrive in network byte order in the low word of the DWORD.</summary>
    private static int ReadPort(uint value)
    {
        var port = ((value & 0x000000FFu) << 8) | ((value & 0x0000FF00u) >> 8);
        return (int)(port & 0xFFFF);
    }

    /// <summary>Maps the MIB_TCP_STATE value (1..12) to its Chinese label.</summary>
    private static string MapTcpState(uint state) => state switch
    {
        1 => "已关闭",      // CLOSED
        2 => "侦听",        // LISTEN
        3 => "已发送SYN",   // SYN_SENT
        4 => "已接收SYN",   // SYN_RCVD
        5 => "已建立",      // ESTAB
        6 => "FIN等待1",    // FIN_WAIT1
        7 => "FIN等待2",    // FIN_WAIT2
        8 => "关闭等待",    // CLOSE_WAIT
        9 => "正在关闭",    // CLOSING
        10 => "最后确认",   // LAST_ACK
        11 => "时间等待",   // TIME_WAIT
        12 => "删除",       // DELETE_TCB
        _ => UnknownState,
    };

    private static string MapNetstatState(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UnknownState;
        var text = raw.Trim();
        return NetstatStateLabels.TryGetValue(text, out var label) ? label : text;
    }

    // ── Process resolution ──────────────────────────────────────────────────

    private readonly record struct ProcessIdentity(string Name, string Path, bool Missing);

    private static void AttachProcessInfo(
        List<LocalPortInfo> rows,
        Dictionary<int, ProcessIdentity> cache,
        CancellationToken ct)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if ((i & 0x3F) == 0) ct.ThrowIfCancellationRequested();

            var row = rows[i];
            var identity = ResolveProcess(row.Pid, cache);
            row.ProcessName = identity.Name;
            row.ProcessPath = identity.Path;
            row.ProcessMissing = identity.Missing;
        }
    }

    /// <summary>PID → identity lookups are memoised for the duration of one query.</summary>
    private static ProcessIdentity ResolveProcess(int pid, Dictionary<int, ProcessIdentity> cache)
    {
        if (cache.TryGetValue(pid, out var cached)) return cached;

        var identity = QueryProcessIdentity(pid);
        cache[pid] = identity;
        return identity;
    }

    /// <summary>
    /// Resolves name and path without ever throwing: the native call is preferred (it also works
    /// for processes whose managed wrapper would need more rights), and the managed
    /// <see cref="Process"/> API is the fallback for the name alone.
    /// </summary>
    private static ProcessIdentity QueryProcessIdentity(int pid)
    {
        if (pid > 0)
        {
            var handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (handle != IntPtr.Zero)
                {
                    uint capacity = MaxProcessImagePathChars;
                    var builder = new StringBuilder(MaxProcessImagePathChars);
                    if (QueryFullProcessImageName(handle, 0, builder, ref capacity))
                    {
                        var path = builder.ToString();
                        if (path.Length > 0)
                        {
                            var name = Path.GetFileNameWithoutExtension(path);
                            return new ProcessIdentity(name.Length > 0 ? name : path, path, Missing: false);
                        }
                    }
                }
            }
            catch
            {
                // Fall through to the managed probe.
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                var name = process.ProcessName;
                if (!string.IsNullOrEmpty(name)) return new ProcessIdentity(name, string.Empty, Missing: false);
            }
            catch
            {
                // Process exited, or we lack the rights to look at it.
            }
        }

        // Exited or protected: the row is still worth showing, just without process details.
        return new ProcessIdentity(string.Empty, string.Empty, Missing: true);
    }

    // ── Fallback: netstat -ano ──────────────────────────────────────────────

    /// <summary>
    /// Structural netstat row matcher: protocol, local endpoint, remote endpoint, remainder
    /// (state + PID, or PID alone for UDP). It never matches localized header text, so Chinese
    /// and English output parse identically.
    /// </summary>
    [GeneratedRegex(
        @"^[ \t]*(TCP|UDP)[ \t]+(\S+)[ \t]+(\S+)[ \t]*(.*?)[ \t]*\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetstatRowRegex();

    private static List<LocalPortInfo> CollectNetstat(
        bool includeTcp,
        bool includeUdp,
        bool includeIpv6,
        CancellationToken ct)
    {
        var rows = new List<LocalPortInfo>(256);

        string output;
        try
        {
            output = RunNetstat(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A cancellation kills netstat, which surfaces here as a broken pipe.
            ct.ThrowIfCancellationRequested();
            AppLog.Instance.Log(nameof(LocalPortTable), "运行 netstat 回退失败：" + ex.Message);
            return rows;
        }

        foreach (Match match in NetstatRowRegex().Matches(output))
        {
            ct.ThrowIfCancellationRequested();

            var protocol = match.Groups[1].Value.ToUpperInvariant();
            if (protocol == "TCP" && !includeTcp) continue;
            if (protocol == "UDP" && !includeUdp) continue;

            var localAddress = ParseEndpoint(match.Groups[2].Value, out var localPort);
            if (localAddress.Contains(':') && !includeIpv6) continue;

            var remoteAddress = ParseEndpoint(match.Groups[3].Value, out var remotePort);
            var tail = match.Groups[4].Value.Trim();

            var state = protocol == "UDP" ? LocalPortInfo.UdpState : UnknownState;
            var pid = 0;
            var tokens = tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0
                && int.TryParse(tokens[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPid))
            {
                pid = parsedPid;
                tail = tokens.Length > 1 ? string.Join(" ", tokens, 0, tokens.Length - 1) : string.Empty;
            }

            if (protocol == "TCP") state = MapNetstatState(tail);

            // Rows are deliberately not de-duplicated: the endpoint table really can hold the
            // same local endpoint more than once for one PID (mDNS/DNS clients open several
            // UDP sockets on the same port), and the native path reports each of them too.
            rows.Add(new LocalPortInfo
            {
                Protocol = protocol,
                LocalAddress = localAddress,
                LocalPort = localPort,
                RemoteAddress = remoteAddress,
                RemotePort = remotePort,
                State = state,
                Pid = pid,
            });
        }

        return rows;
    }

    /// <summary>
    /// Splits <c>address:port</c>, <c>[v6address]:port</c> or UDP's <c>*:*</c>. Brackets are
    /// dropped so the value matches what <see cref="IPAddress"/> produces on the native path.
    /// </summary>
    private static string ParseEndpoint(string text, out int port)
    {
        port = 0;
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var colon = text.LastIndexOf(':');
        if (colon < 0) return text.Trim();

        var address = text[..colon].Trim();
        if (address.Length >= 2 && address[0] == '[' && address[^1] == ']') address = address[1..^1];

        var portText = text[(colon + 1)..].Trim();
        if (portText.Length > 0) int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port);

        return address;
    }

    private static string RunNetstat(CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("netstat", "-ano")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = NetstatOutputEncoding.Value,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 netstat 进程。");

        using var registration = ct.Register(static state =>
        {
            try
            {
                var target = (Process)state!;
                if (!target.HasExited) target.Kill();
            }
            catch
            {
                // The process already exited or cannot be killed; the wait below still ends.
            }
        }, process);

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(NetstatTimeoutMilliseconds);
        ct.ThrowIfCancellationRequested();
        return output;
    }

    /// <summary>
    /// netstat writes in the console's OEM code page on Windows; if that lookup is unavailable
    /// we fall back to the console encoding and finally to UTF-8. The parser itself is ASCII
    /// structural, so encoding only affects the readability of a localized state word.
    /// </summary>
    private static Encoding ResolveNetstatEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            try
            {
                return Console.OutputEncoding;
            }
            catch
            {
                return Encoding.UTF8;
            }
        }
    }

    // ── Ordering ────────────────────────────────────────────────────────────

    /// <summary>Protocol, then local port, then PID — the grid's stable order.</summary>
    private static void SortRows(List<LocalPortInfo> rows) => rows.Sort(static (a, b) =>
    {
        var result = string.CompareOrdinal(a.Protocol, b.Protocol);
        if (result != 0) return result;

        result = a.LocalPort.CompareTo(b.LocalPort);
        if (result != 0) return result;

        result = a.Pid.CompareTo(b.Pid);
        if (result != 0) return result;

        result = string.CompareOrdinal(a.LocalAddress, b.LocalAddress);
        if (result != 0) return result;

        result = a.RemotePort.CompareTo(b.RemotePort);
        return result != 0 ? result : string.CompareOrdinal(a.RemoteAddress, b.RemoteAddress);
    });
}
