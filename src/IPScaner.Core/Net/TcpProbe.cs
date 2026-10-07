using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace IPScaner.Core.Net;

/// <summary>TCP connect probe used both as a ping fallback and by the port scanner.</summary>
public static class TcpProbe
{
    /// <summary>
    /// Attempts a full TCP handshake. Returns true only when the connection was
    /// established; a refused or timed-out connect is false.
    /// </summary>
    /// <remarks>
    /// The original used <c>TcpClient.BeginConnect</c> plus
    /// <c>AsyncWaitHandle.WaitOne(timeout)</c> and then closed the client on
    /// timeout, leaving the connect attempt to finish in the background. This
    /// version uses a cancellable <see cref="Socket.ConnectAsync(EndPoint, CancellationToken)"/>
    /// so a 50&nbsp;ms scan of 5 ports across 254 hosts does not leave thousands
    /// of orphaned half-open connections behind.
    /// </remarks>
    public static async Task<bool> IsOpenAsync(string ip, int port, int timeoutMs, CancellationToken ct = default)
    {
        if (port is <= 0 or > 65535) return false;
        if (!IPAddress.TryParse(ip, out var address)) return false;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutMs > 0) cts.CancelAfter(timeoutMs);
            await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
            return socket.Connected;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            try { socket.Close(); } catch { /* already gone */ }
        }
    }

    /// <summary>Blocking convenience wrapper around <see cref="IsOpenAsync"/>.</summary>
    public static bool IsOpen(string ip, int port, int timeoutMs, CancellationToken ct = default) =>
        IsOpenAsync(ip, port, timeoutMs, ct).GetAwaiter().GetResult();

    /// <summary>
    /// Tries each port in turn and stops at the first success — the original's
    /// <c>TcpPortTestMuli</c>. <paramref name="onResult"/> receives the verdict.
    /// </summary>
    public static async Task<bool> AnyPortOpenAsync(
        string ip,
        IEnumerable<int> ports,
        int timeoutMs,
        Action<bool>? onResult = null,
        CancellationToken ct = default)
    {
        var any = false;
        foreach (var port in ports)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsOpenAsync(ip, port, timeoutMs, ct).ConfigureAwait(false))
            {
                any = true;
                break;
            }
        }

        onResult?.Invoke(any);
        return any;
    }

    /// <summary>Measures connect latency; -1 when the port is closed or filtered.</summary>
    public static async Task<long> MeasureAsync(string ip, int port, int timeoutMs, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var open = await IsOpenAsync(ip, port, timeoutMs, ct).ConfigureAwait(false);
        sw.Stop();
        return open ? sw.ElapsedMilliseconds : -1;
    }
}
