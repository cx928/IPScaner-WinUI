using Xunit;
using System.Reflection;
using IPScaner.Core.Configuration;

namespace IPScaner.Core.Tests;

/// <summary>
/// Coverage for <see cref="AppConfig"/> — every factory default must match the
/// original WinForms tool, or an existing IPScaner.cfg silently changes meaning.
/// </summary>
public class AppConfigTests
{
    [Fact]
    public void Defaults_MatchTheOriginalApplication()
    {
        var cfg = new AppConfig();

        Assert.Equal("1.0", cfg.Version);
        Assert.Equal(500, cfg.PingTimeout);
        Assert.Equal(4, cfg.PingCount);
        Assert.Equal(200, cfg.DoubleClickTime);
        Assert.Equal(9, cfg.BtnFontSize);
        Assert.Equal(50, cfg.PortTimeout);
        Assert.Equal("80,135,445,500", cfg.PrePortArray);
        Assert.Equal(70, cfg.DesktopOverlayOpacity);
        Assert.Equal(2, cfg.DesktopOverlayLocation);
        Assert.Equal("本地IP地址：", cfg.DesktopOverlayPre);
    }

    [Theory]
    [InlineData(-7876885)]   // SkyBlue    -> DefaultColorArgb
    [InlineData(-13447886)]  // LimeGreen  -> NetworkOKColorArgb
    [InlineData(-3318692)]   // IndianRed  -> NetworkNGColorArgb
    [InlineData(-16776961)]  // Blue       -> MemoColorArgb
    [InlineData(-16777216)]  // Black      -> DesktopForeColorArgb
    [InlineData(-256)]       // Yellow     -> DesktopBgColorArgb
    public void Defaults_UseTheSixWellKnownWinFormsColours(int argb)
        => Assert.Contains(argb, new[]
        {
            new AppConfig().DefaultColorArgb,
            new AppConfig().NetworkOKColorArgb,
            new AppConfig().NetworkNGColorArgb,
            new AppConfig().MemoColorArgb,
            new AppConfig().DesktopForeColorArgb,
            new AppConfig().DesktopBgColorArgb,
        });

    [Fact]
    public void Defaults_ColoursAreBoundToTheNamedConstants()
    {
        var cfg = new AppConfig();

        Assert.Equal(AppConfig.SkyBlueArgb, cfg.DefaultColorArgb);
        Assert.Equal(AppConfig.LimeGreenArgb, cfg.NetworkOKColorArgb);
        Assert.Equal(AppConfig.IndianRedArgb, cfg.NetworkNGColorArgb);
        Assert.Equal(AppConfig.BlueArgb, cfg.MemoColorArgb);
        Assert.Equal(AppConfig.BlackArgb, cfg.DesktopForeColorArgb);
        Assert.Equal(AppConfig.YellowArgb, cfg.DesktopBgColorArgb);
    }

    [Fact]
    public void Defaults_CoverEveryRemainingSetting()
    {
        var cfg = new AppConfig();

        Assert.False(cfg.QueryHostNameEnabled);
        Assert.False(cfg.ARPInsteadPingEnabled);
        Assert.False(cfg.PortInsteadPingEnabled);
        Assert.False(cfg.MenuAutoOpen);
        Assert.Equal(EventName.Ping, cfg.DoubleEvent);
        Assert.False(cfg.DesktopOverlayEnabled);
        Assert.Equal(100, cfg.DesktopOverlayOffsetX);
        Assert.Equal(100, cfg.DesktopOverlayOffsetY);
        Assert.False(cfg.LogEnabled);
        Assert.Null(cfg.StarMenu);
        Assert.False(cfg.HideMainEnabled);
    }

    [Fact]
    public void ResetToDefaults_RestoresEveryProperty()
    {
        var cfg = Mutate(new AppConfig());

        cfg.ResetToDefaults();

        var snapshot = Snapshot(new AppConfig());
        AssertMatchesSnapshot(snapshot, cfg);
    }

    [Fact]
    public void Clone_IsADeepCopy()
    {
        var original = new AppConfig { StarMenu = "mnuTools", PrePortArray = "80,443", PingTimeout = 1234 };
        var before = Snapshot(original);

        var clone = original.Clone();

        // The clone starts out identical ...
        AssertMatchesSnapshot(before, clone);

        // ... and mutating every settable property on it leaves the original alone.
        var properties = SettableProperties();
        foreach (var property in properties) property.SetValue(clone, MutatedValue(property, property.GetValue(clone)));

        AssertMatchesSnapshot(before, original);
        Assert.All(properties, p => Assert.NotEqual(p.GetValue(original), p.GetValue(clone)));
    }

    [Fact]
    public void Clone_DetachesStarMenu()
    {
        var original = new AppConfig { StarMenu = "mnuOriginal" };

        var clone = original.Clone();
        clone.StarMenu = "mnuChanged";

        Assert.Equal("mnuOriginal", original.StarMenu);
        Assert.Equal("mnuChanged", clone.StarMenu);
    }

    // ---- GetPrePorts -------------------------------------------------------

    [Fact]
    public void GetPrePorts_SplitsTheDefaultPrePortArray()
        => Assert.Equal([80, 135, 445, 500], new AppConfig().GetPrePorts());

    [Fact]
    public void GetPrePorts_TrimsTokensAndKeepsTheirOrder()
        => Assert.Equal([80, 443, 8080], new AppConfig { PrePortArray = " 80 ,443,  8080 " }.GetPrePorts());

    [Theory]
    [InlineData("abc,80,xyz", new[] { 80 })]
    [InlineData("80,0,65536,70000,-5", new[] { 80 })]    // out of range tokens are dropped
    [InlineData("65535,1", new[] { 65535, 1 })]          // both bounds are legal
    [InlineData("80,,,443", new[] { 80, 443 })]          // empty tokens removed
    [InlineData(" , , ", new int[0])]
    [InlineData("", new int[0])]
    [InlineData(null, new int[0])]
    [InlineData("   ", new int[0])]
    [InlineData("08,0x10", new[] { 8 })]                 // "0x10" is not a port
    // DISCREPANCY (reported; library not modified): unlike PortScanner.ParsePorts,
    // GetPrePorts does not de-duplicate, so a hand-edited "80,443,80" probes 80 twice.
    [InlineData("80,443,80", new[] { 80, 443, 80 })]
    public void GetPrePorts_SkipsJunkAndOutOfRangeTokens(string? prePorts, int[] expected)
        => Assert.Equal(expected, new AppConfig { PrePortArray = prePorts! }.GetPrePorts());

    [Fact]
    public void GetPrePorts_ReturnsEmpty_WhenPrePortArrayIsNull()
    {
        var cfg = new AppConfig { PrePortArray = null! };

        Assert.Empty(cfg.GetPrePorts());
    }

    [Fact]
    public void IsHostNameLookupEnabled_TracksQueryHostNameEnabled()
    {
        var cfg = new AppConfig();

        Assert.False(cfg.IsHostNameLookupEnabled);
        cfg.QueryHostNameEnabled = true;
        Assert.True(cfg.IsHostNameLookupEnabled);
    }

    // ---- helpers -----------------------------------------------------------

    private static PropertyInfo[] SettableProperties() =>
        [.. typeof(AppConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)];

    /// <summary>Sets every settable property to a value that differs from the default.</summary>
    private static AppConfig Mutate(AppConfig cfg)
    {
        foreach (var property in SettableProperties())
        {
            property.SetValue(cfg, MutatedValue(property, property.GetValue(cfg)));
        }
        return cfg;
    }

    private static object MutatedValue(PropertyInfo property, object? current)
    {
        var type = property.PropertyType;
        if (type == typeof(bool)) return !(bool)current!;
        if (type == typeof(int)) return (int)current! + 7;
        if (type == typeof(string)) return (current as string ?? string.Empty) + "-changed";
        if (type == typeof(EventName)) return (EventName)(((int)(EventName)current! + 1) % 7);
        if (type == typeof(ViewMode)) return (ViewMode)(((int)(ViewMode)current! + 1) % 4);
        if (type == typeof(ThemeMode)) return (ThemeMode)(((int)(ThemeMode)current! + 1) % 3);

        throw new InvalidOperationException(
            $"AppConfig.{property.Name} has type {type.Name}; add it to the round-trip helpers.");
    }

    private static Dictionary<string, object?> Snapshot(AppConfig cfg) =>
        SettableProperties().ToDictionary(p => p.Name, p => p.GetValue(cfg), StringComparer.Ordinal);

    private static void AssertMatchesSnapshot(IReadOnlyDictionary<string, object?> snapshot, AppConfig cfg)
    {
        Assert.True(snapshot.Count >= 27, $"expected at least 27 settable AppConfig properties, saw {snapshot.Count}");

        foreach (var (name, expected) in snapshot)
        {
            var actual = typeof(AppConfig).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(cfg);
            Assert.True(
                Equals(expected, actual),
                $"AppConfig.{name}: expected '{expected ?? "<null>"}', got '{actual ?? "<null>"}'");
        }
    }
}
