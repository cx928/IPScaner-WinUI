using System.Collections.Concurrent;

namespace IPScaner.Core.Caching;

/// <summary>
/// Minimal thread-safe cache with absolute expiry, replacing the original's
/// dependency on <c>System.Runtime.Caching.MemoryCache</c> (which is not part of
/// the modern .NET base libraries).
/// </summary>
/// <remarks>
/// The original cached host-name and MAC lookups for one hour. That is preserved
/// as <see cref="DefaultTtl"/>, but the cache is now explicitly clearable so the
/// user can force a refresh without restarting the tool.
/// </remarks>
public sealed class ExpiringCache<TValue>
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;

    public ExpiringCache(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    private readonly record struct Entry(TValue Value, DateTimeOffset ExpiresAt);

    public int Count => _entries.Count;

    public bool TryGet(string key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > _clock.GetUtcNow())
            {
                value = entry.Value;
                return true;
            }
            _entries.TryRemove(key, out _);
        }

        value = default!;
        return false;
    }

    public void Set(string key, TValue value, TimeSpan? ttl = null) =>
        _entries[key] = new Entry(value, _clock.GetUtcNow() + (ttl ?? DefaultTtl));

    /// <summary>Returns the cached value, or computes, stores and returns it.</summary>
    public TValue GetOrAdd(string key, Func<string, TValue> factory, TimeSpan? ttl = null)
    {
        if (TryGet(key, out var existing)) return existing;
        var created = factory(key);
        Set(key, created, ttl);
        return created;
    }

    public void Remove(string key) => _entries.TryRemove(key, out _);

    public void Clear() => _entries.Clear();
}

/// <summary>Process-wide caches shared by every scanner surface.</summary>
public static class ScanCaches
{
    /// <summary>Host-name resolutions, keyed by IP.</summary>
    public static ExpiringCache<string> HostNames { get; } = new();

    /// <summary>MAC addresses learned from the ARP table, keyed by IP.</summary>
    public static ExpiringCache<string> MacAddresses { get; } = new();

    public static void ClearAll()
    {
        HostNames.Clear();
        MacAddresses.Clear();
    }
}
