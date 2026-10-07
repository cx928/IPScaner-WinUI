using Xunit;
using IPScaner.Core.Caching;

namespace IPScaner.Core.Tests;

/// <summary>
/// Coverage for <see cref="ExpiringCache{TValue}"/>. The cache accepts a
/// <see cref="TimeProvider"/>, so a local <see cref="FakeClock"/> is used instead
/// of sleeping — no extra package is needed for deterministic TTL tests.
/// </summary>
public class ExpiringCacheTests
{
    [Fact]
    public void SetThenTryGet_ReturnsTheValue()
    {
        var clock = new FakeClock();
        var cache = new ExpiringCache<string>(clock);

        cache.Set("192.168.1.10", "printer");

        Assert.True(cache.TryGet("192.168.1.10", out var value));
        Assert.Equal("printer", value);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForAnUnknownKey()
    {
        var cache = new ExpiringCache<string>(new FakeClock());

        Assert.False(cache.TryGet("missing", out var value));
        Assert.Null(value);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void KeysAreCaseInsensitive()
    {
        var cache = new ExpiringCache<string>(new FakeClock());
        cache.Set("Printer", "值");

        Assert.True(cache.TryGet("printer", out var value));
        Assert.Equal("值", value);
    }

    [Fact]
    public void Set_OverwritesAnExistingValue()
    {
        var cache = new ExpiringCache<string>(new FakeClock());

        cache.Set("k", "first");
        cache.Set("k", "second");

        Assert.True(cache.TryGet("k", out var value));
        Assert.Equal("second", value);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Entry_ExpiresAfterItsTtl_AndIsEvictedOnRead()
    {
        var clock = new FakeClock();
        var cache = new ExpiringCache<string>(clock);
        cache.Set("k", "v", TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(cache.TryGet("k", out _));

        clock.Advance(TimeSpan.FromSeconds(2)); // 61s total
        Assert.False(cache.TryGet("k", out _));
        Assert.Equal(0, cache.Count); // the stale entry is removed, not just hidden
    }

    [Fact]
    public void Entry_ExpiresExactlyAtTheTtlBoundary()
    {
        var clock = new FakeClock();
        var cache = new ExpiringCache<string>(clock);
        cache.Set("k", "v", TimeSpan.FromMinutes(1));

        // Expiry is absolute and the comparison is strictly ">", so the entry is
        // already gone when the clock reaches ExpiresAt.
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.False(cache.TryGet("k", out _));
    }

    [Fact]
    public void DefaultTtl_IsOneHour()
    {
        Assert.Equal(TimeSpan.FromHours(1), ExpiringCache<string>.DefaultTtl);

        var clock = new FakeClock();
        var cache = new ExpiringCache<string>(clock);
        cache.Set("k", "v");

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.True(cache.TryGet("k", out _));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(cache.TryGet("k", out _));
    }

    [Fact]
    public void GetOrAdd_CallsTheFactoryOnlyOnce()
    {
        var clock = new FakeClock();
        var cache = new ExpiringCache<string>(clock);
        var calls = 0;

        var first = cache.GetOrAdd("192.168.1.10", key =>
        {
            calls++;
            return $"name-of-{key}";
        });
        var second = cache.GetOrAdd("192.168.1.10", _ =>
        {
            calls++;
            return "should-not-be-used";
        });

        Assert.Equal(1, calls);
        Assert.Equal("name-of-192.168.1.10", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void GetOrAdd_CallsTheFactoryAgain_AfterExpiry()
    {
        var clock = new FakeClock();
        var cache = new ExpiringCache<int>(clock);
        var calls = 0;

        Assert.Equal(1, cache.GetOrAdd("k", _ => ++calls, TimeSpan.FromMinutes(1)));
        Assert.Equal(1, cache.GetOrAdd("k", _ => ++calls, TimeSpan.FromMinutes(1)));

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(2, cache.GetOrAdd("k", _ => ++calls, TimeSpan.FromMinutes(1)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void GetOrAdd_StoresTheValueWithTheSuppliedTtl()
    {
        var clock = new FakeClock();
        var cache = new ExpiringCache<string>(clock);

        cache.GetOrAdd("k", _ => "v", TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.False(cache.TryGet("k", out _));
    }

    [Fact]
    public void Remove_DropsASingleEntry()
    {
        var cache = new ExpiringCache<string>(new FakeClock());
        cache.Set("a", "1");
        cache.Set("b", "2");

        cache.Remove("a");

        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.Equal(1, cache.Count);

        cache.Remove("never-seen"); // must not throw
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var clock = new FakeClock();
        var cache = new ExpiringCache<string>(clock);
        cache.Set("a", "1");
        cache.Set("b", "2", TimeSpan.FromHours(5));

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void ScanCaches_AreProcessWideAndClearable()
    {
        ScanCaches.HostNames.Set("192.168.1.10", "printer");
        ScanCaches.MacAddresses.Set("192.168.1.10", "00-1A-2B-3C-4D-5E");

        Assert.True(ScanCaches.HostNames.TryGet("192.168.1.10", out _));
        Assert.True(ScanCaches.MacAddresses.TryGet("192.168.1.10", out _));

        ScanCaches.ClearAll();

        Assert.False(ScanCaches.HostNames.TryGet("192.168.1.10", out _));
        Assert.False(ScanCaches.MacAddresses.TryGet("192.168.1.10", out _));
    }
}
