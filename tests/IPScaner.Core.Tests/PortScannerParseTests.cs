using Xunit;
using IPScaner.Core.Net;

namespace IPScaner.Core.Tests;

/// <summary>
/// Coverage for the pure text parsers on <see cref="PortScanner"/>.
/// </summary>
public class PortScannerParseTests
{
    // ---- ParsePorts --------------------------------------------------------

    [Fact]
    public void ParsePorts_SplitsACommaList()
        => Assert.Equal([80, 443], PortScanner.ParsePorts("80,443"));

    [Fact]
    public void ParsePorts_SortsAscendingAndRemovesDuplicates()
    {
        Assert.Equal([80, 443], PortScanner.ParsePorts("443,80"));
        Assert.Equal([80, 443], PortScanner.ParsePorts("80,80,443,80"));
        Assert.Equal([80, 443, 8080], PortScanner.ParsePorts("8080,80,443,80"));
    }

    [Fact]
    public void ParsePorts_ExpandsARange()
    {
        var ports = PortScanner.ParsePorts("1000-1005");

        Assert.Equal(6, ports.Count);
        Assert.Equal([1000, 1001, 1002, 1003, 1004, 1005], ports);
    }

    [Fact]
    public void ParsePorts_SwapsAReversedRange()
        => Assert.Equal([1000, 1001, 1002, 1003, 1004, 1005], PortScanner.ParsePorts("1005-1000"));

    [Fact]
    public void ParsePorts_AllExpandsToTheWholePortSpace()
    {
        var ports = PortScanner.ParsePorts("all");

        Assert.Equal(65535, ports.Count);
        Assert.Equal(1, ports[0]);
        Assert.Equal(65535, ports[^1]);
    }

    [Fact]
    public void ParsePorts_AllIsCaseInsensitiveAndHasChineseAliases()
    {
        Assert.Equal(65535, PortScanner.ParsePorts("ALL").Count);
        Assert.Equal(65535, PortScanner.ParsePorts("全部").Count);
        Assert.Equal(65535, PortScanner.ParsePorts("全部端口").Count);
    }

    [Fact]
    public void ParsePorts_AllCanBeRefused()
        => Assert.Empty(PortScanner.ParsePorts("all", allowAll: false));

    [Theory]
    [InlineData("80，443")]   // full-width comma
    [InlineData("80、443")]   // ideographic comma
    [InlineData("80 443")]    // space
    [InlineData("80;443")]    // semicolon
    [InlineData("80 , 443")]
    public void ParsePorts_AcceptsEverySeparatorTheOriginalAccepted(string text)
        => Assert.Equal([80, 443], PortScanner.ParsePorts(text));

    [Theory]
    [InlineData("abc,80,xyz", new[] { 80 })]
    [InlineData("80,,,443", new[] { 80, 443 })]
    [InlineData("0,65536,70000,-5,80", new[] { 80 })]
    [InlineData("80-abc,443", new[] { 443 })]
    [InlineData(" 80 ", new[] { 80 })]
    public void ParsePorts_SkipsJunkAndOutOfRangeTokens(string text, int[] expected)
        => Assert.Equal(expected, PortScanner.ParsePorts(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(" , ; ")]
    [InlineData("not-a-port")]
    public void ParsePorts_ReturnsEmpty_ForInputWithoutPorts(string? text)
        => Assert.Empty(PortScanner.ParsePorts(text));

    [Fact]
    public void ParsePorts_ClampsRangeEndpointsAndCapsASillyRange()
    {
        // Endpoints are clamped into 1..65535 ...
        Assert.Equal([1, 2, 65535], PortScanner.ParsePorts("0-2,65535-70000"));

        // ... and a single range is capped at 20001 entries so that "1-65535"
        // cannot be typed by accident into a 254-host scan.
        var capped = PortScanner.ParsePorts("1-65535");
        Assert.Equal(20001, capped.Count);
        Assert.Equal(1, capped[0]);
        Assert.Equal(20001, capped[^1]);
    }

    // ---- ParseTargets ------------------------------------------------------

    [Fact]
    public void ParseTargets_ExpandsAnExplicitAddressRange()
    {
        var targets = PortScanner.ParseTargets("192.168.1.1-192.168.1.5");

        Assert.Equal(["192.168.1.1", "192.168.1.2", "192.168.1.3", "192.168.1.4", "192.168.1.5"], targets);
    }

    [Fact]
    public void ParseTargets_ExpandsAShortRangeSuffix()
    {
        var targets = PortScanner.ParseTargets("192.168.1.1-5");

        Assert.Equal(5, targets.Count);
        Assert.Equal("192.168.1.1", targets[0]);
        Assert.Equal("192.168.1.5", targets[^1]);
    }

    [Fact]
    public void ParseTargets_KeepsNetworkAndBroadcastAddressesInsideARange()
    {
        // Unlike IpMath.GetRange's default, a hand-typed range is taken literally.
        var targets = PortScanner.ParseTargets("192.168.1.254-192.168.2.1");

        Assert.Equal(["192.168.1.254", "192.168.1.255", "192.168.2.0", "192.168.2.1"], targets);
    }

    [Fact]
    public void ParseTargets_ExpandsACidrBlockToItsUsableHosts()
    {
        var targets = PortScanner.ParseTargets("192.168.1.0/30");

        Assert.Equal(["192.168.1.1", "192.168.1.2"], targets);
    }

    [Fact]
    public void ParseTargets_ExpandsAWiderCidrBlock()
    {
        var targets = PortScanner.ParseTargets("192.168.1.0/24");

        Assert.Equal(254, targets.Count);
        Assert.Equal("192.168.1.1", targets[0]);
        Assert.Equal("192.168.1.254", targets[^1]);
    }

    [Fact]
    public void ParseTargets_Slash31AndSlash32FollowTheCalculator()
    {
        Assert.Equal(["192.168.1.10", "192.168.1.11"], PortScanner.ParseTargets("192.168.1.10/31"));
        Assert.Equal(["192.168.1.10"], PortScanner.ParseTargets("192.168.1.10/32"));
    }

    [Fact]
    public void ParseTargets_ExpandsABareSegmentTo254Hosts()
    {
        var targets = PortScanner.ParseTargets("192.168.1");

        Assert.Equal(254, targets.Count);
        Assert.Equal("192.168.1.1", targets[0]);
        Assert.Equal("192.168.1.254", targets[^1]);
    }

    [Fact]
    public void ParseTargets_KeepsASingleAddress()
        => Assert.Equal(["192.168.1.10"], PortScanner.ParseTargets("192.168.1.10"));

    [Fact]
    public void ParseTargets_TrimsSurroundingWhitespace()
        => Assert.Equal(["192.168.1.10"], PortScanner.ParseTargets("  192.168.1.10  "));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("999.1.1.1")]
    [InlineData("192.168.1.1abc")]
    [InlineData("not-an-address")]
    [InlineData("192.168.1.1-abc")]
    [InlineData("192.168.1.0/99")]
    public void ParseTargets_ReturnsEmpty_ForUnusableInput(string? text)
        => Assert.Empty(PortScanner.ParseTargets(text));

    [Fact]
    public void ParseTargets_StopsAtTheFirstFormThatMatches()
    {
        // A range is tried before CIDR/segment/single-address handling.
        var targets = PortScanner.ParseTargets("192.168.1.1-192.168.1.2");

        Assert.Equal(["192.168.1.1", "192.168.1.2"], targets);
    }
}
