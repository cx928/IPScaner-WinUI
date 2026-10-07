using System.Net;
using IPScaner.Core.Caching;
using IPScaner.Core.Logging;

namespace IPScaner.Core.Net;

/// <summary>
/// Reverse DNS lookups for the 主机名 column and tooltips.
/// </summary>
/// <remarks>
/// The original called <c>Dns.GetHostEntry</c> with no timeout, so a single
/// unresponsive DNS server could stall a host-name sweep for many seconds. A
/// timeout is applied here (default 3&nbsp;s) while keeping the same observable
/// result: the literal "未知" when the lookup fails, and the original's trimming
/// of a trailing "domain" / ".local" suffix.
/// </remarks>
public sealed class NameResolver
{
    private static readonly string[] TrimSuffixes = ["domain", ".local"];

    /// <summary>Returned when the host name cannot be resolved.</summary>
    public const string Unknown = "未知";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Resolves a host name, using the shared one-hour cache.</summary>
    public async Task<string> ResolveAsync(string ipAddress, CancellationToken ct = default)
    {
        if (ScanCaches.HostNames.TryGet(ipAddress, out var cached)) return cached;

        var result = Unknown;
        try
        {
            var task = Dns.GetHostEntryAsync(ipAddress, ct);
            var entry = await task.WaitAsync(Timeout, ct).ConfigureAwait(false);
            result = Normalize(entry.HostName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            result = Unknown;
        }

        AppLog.Instance.Log(nameof(NameResolver), $"DNS {ipAddress} -> {result}");
        ScanCaches.HostNames.Set(ipAddress, result);
        return result;
    }

    /// <summary>Strips the trailing search-domain noise the original removed.</summary>
    public static string Normalize(string hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName)) return Unknown;

        var text = hostName;
        // The original looped until no suffix matched, resetting the scan index
        // each time — reproduced here without the index gymnastics.
        bool changed;
        do
        {
            changed = false;
            foreach (var suffix in TrimSuffixes)
            {
                if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[..^suffix.Length];
                    changed = true;
                    break;
                }
            }
        } while (changed);

        return string.IsNullOrWhiteSpace(text) ? Unknown : text;
    }
}
