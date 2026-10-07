using Xunit;
using IPScaner.Core.Net;

namespace IPScaner.Core.Tests;

/// <summary>
/// Coverage for <see cref="IpMath"/>. Test #1 of the port checklist is the
/// regression guard for the original's prefix-matching regex.
/// </summary>
public class IpMathTests
{
    // ---- IsValidIPv4 -------------------------------------------------------

    [Theory]
    // The original regex matched a *prefix* of the text, so these three reached
    // IPAddress.Parse / the octet shift and blew up with an OverflowException or
    // produced a bogus address. They are the reason this helper exists.
    [InlineData("999.1.1.1")]
    [InlineData("10.0.0.1abc")]
    [InlineData("256.1.1.1")]
    // Plain malformed input.
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("1.2.3.")]
    [InlineData(".1.2.3")]
    [InlineData("1..3.4")]
    [InlineData("1.2.3.1234")]
    [InlineData("-1.2.3.4")]
    [InlineData("1.2.3.-4")]
    [InlineData("0x10.1.1.1")]
    [InlineData("1.2.3.4a")]
    [InlineData("1,2,3,4")]
    public void IsValidIPv4_RejectsMalformedInput(string? text)
        => Assert.False(IpMath.IsValidIPv4(text));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("1.2.3.4")]
    [InlineData(" 192.168.1.1 ")] // surrounding whitespace is trimmed like the original
    public void IsValidIPv4_AcceptsWellFormedInput(string text)
        => Assert.True(IpMath.IsValidIPv4(text));

    [Fact]
    public void IsValidIPv4_AcceptsBoundaryOctetsForEveryPosition()
    {
        Assert.True(IpMath.IsValidIPv4("255.0.0.0"));
        Assert.True(IpMath.IsValidIPv4("0.255.0.0"));
        Assert.True(IpMath.IsValidIPv4("0.0.255.0"));
        Assert.True(IpMath.IsValidIPv4("0.0.0.255"));
    }

    // ---- ParseOctets / TryParseOctets -------------------------------------

    [Fact]
    public void ParseOctets_SplitsDottedQuad()
        => Assert.Equal([192, 168, 1, 55], IpMath.ParseOctets("192.168.1.55"));

    [Fact]
    public void ParseOctets_ThrowsFormatException_InsteadOfOverflowing()
    {
        // Regression: the original crashed with OverflowException / produced a
        // wrapped address for "999.1.1.1" and "10.0.0.1abc".
        Assert.Throws<FormatException>(() => IpMath.ParseOctets("999.1.1.1"));
        Assert.Throws<FormatException>(() => IpMath.ParseOctets("10.0.0.1abc"));
        Assert.Throws<FormatException>(() => IpMath.ParseOctets("1.2.3"));
    }

    [Fact]
    public void TryParseOctets_ReturnsFalseAndEmptyArray_ForMalformedInput()
    {
        Assert.False(IpMath.TryParseOctets("999.1.1.1", out var octets));
        Assert.Empty(octets);

        Assert.False(IpMath.TryParseOctets("10.0.0.1abc", out octets));
        Assert.Empty(octets);

        Assert.False(IpMath.TryParseOctets(null, out octets));
        Assert.Empty(octets);
    }

    [Fact]
    public void TryParseOctets_ReturnsOctets_ForValidInput()
    {
        Assert.True(IpMath.TryParseOctets("0.0.0.0", out var zeros));
        Assert.Equal([0, 0, 0, 0], zeros);

        Assert.True(IpMath.TryParseOctets("255.255.255.255", out var max));
        Assert.Equal([255, 255, 255, 255], max);
    }

    // ---- ToUInt32 / FromUInt32 --------------------------------------------

    [Theory]
    [InlineData("0.0.0.0", 0u)]
    [InlineData("0.0.0.1", 1u)]
    [InlineData("192.168.1.1", 0xC0A80101u)]
    [InlineData("255.255.255.255", uint.MaxValue)]
    public void ToUInt32_ConvertsBigEndian(string ip, uint expected)
        => Assert.Equal(expected, IpMath.ToUInt32(ip));

    [Theory]
    [InlineData(0u, "0.0.0.0")]
    [InlineData(1u, "0.0.0.1")]
    [InlineData(0xC0A80101u, "192.168.1.1")]
    [InlineData(uint.MaxValue, "255.255.255.255")]
    public void FromUInt32_ConvertsBack(uint value, string expected)
        => Assert.Equal(expected, IpMath.FromUInt32(value));

    [Fact]
    public void ToUInt32_ThrowsForMalformedInput()
        => Assert.Throws<FormatException>(() => IpMath.ToUInt32("1.2.3"));

    // ---- GetSegment --------------------------------------------------------

    [Theory]
    [InlineData("192.168.1.55", "192.168.1")]
    [InlineData("192.168.1", "192.168.1")]
    [InlineData("192.168", "")]
    [InlineData("192", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    // A CIDR or range suffix is simply part of the fourth token, which is
    // discarded — exactly what the original's Split('.')/Take(3) produced.
    [InlineData("192.168.1.0/24", "192.168.1")]
    [InlineData("192.168.1.1-192.168.1.50", "192.168.1")]
    [InlineData("  192.168.1.55  ", "192.168.1")]
    public void GetSegment_ReturnsFirstThreeOctets(string? ip, string expected)
        => Assert.Equal(expected, IpMath.GetSegment(ip));

    // ---- IsValidSegment ----------------------------------------------------

    [Theory]
    [InlineData("192.168.1", true)]
    [InlineData("0.0.0", true)]
    [InlineData("255.255.255", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("192.168", false)]
    [InlineData("192.168.1.0/24", false)]
    [InlineData("999.168.1", false)]
    [InlineData("a.b.c", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidSegment_ValidatesThreeOctets(string? segment, bool expected)
        => Assert.Equal(expected, IpMath.IsValidSegment(segment));

    // ---- GetRange ----------------------------------------------------------

    [Fact]
    public void GetRange_IsInclusiveAndKeepsHostAddresses()
    {
        var range = IpMath.GetRange("192.168.1.1", "192.168.1.5");

        Assert.Equal(
            ["192.168.1.1", "192.168.1.2", "192.168.1.3", "192.168.1.4", "192.168.1.5"],
            range);
    }

    [Fact]
    public void GetRange_SkipsDotZeroAndDot255_ByDefault()
    {
        var range = IpMath.GetRange("192.168.1.254", "192.168.2.1");

        Assert.Equal(["192.168.1.254", "192.168.2.1"], range);
    }

    [Fact]
    public void GetRange_KeepsNetworkAndBroadcast_WhenSkippingDisabled()
    {
        var range = IpMath.GetRange("192.168.1.254", "192.168.2.1", skipNetworkAndBroadcast: false);

        Assert.Equal(["192.168.1.254", "192.168.1.255", "192.168.2.0", "192.168.2.1"], range);
    }

    [Fact]
    public void GetRange_ReturnsEmpty_WhenStartIsAfterEnd()
    {
        Assert.Empty(IpMath.GetRange("192.168.1.10", "192.168.1.1"));
    }

    [Fact]
    public void GetRange_ReturnsEmpty_ForInvalidEndpoints()
    {
        Assert.Empty(IpMath.GetRange("999.1.1.1", "192.168.1.1"));
        Assert.Empty(IpMath.GetRange("192.168.1.1", "10.0.0.1abc"));
        Assert.Empty(IpMath.GetRange("", ""));
    }

    [Fact]
    public void GetRange_ReturnsSingleAddress_ForDegenerateRange()
    {
        Assert.Equal(["10.0.0.1"], IpMath.GetRange("10.0.0.1", "10.0.0.1"));

        // .0 is a network address, so the default rule drops it.
        Assert.Empty(IpMath.GetRange("10.0.0.0", "10.0.0.0"));
    }

    [Fact]
    public void GetRange_IsCappedAt65536Addresses()
    {
        var range = IpMath.GetRange("0.0.0.0", "255.255.255.255", skipNetworkAndBroadcast: false);

        Assert.Equal(65536, range.Count);
        Assert.Equal("0.0.0.0", range[0]);
        Assert.Equal("0.0.255.255", range[^1]);
    }

    [Fact]
    public void GetRange_CapAlsoAppliesWhenSkippingNetworkAddresses()
    {
        var range = IpMath.GetRange("0.0.0.0", "255.255.255.255");

        Assert.Equal(65536, range.Count);
        Assert.Equal("0.0.0.1", range[0]);
    }

    // ---- GetSegmentHosts ---------------------------------------------------

    [Fact]
    public void GetSegmentHosts_EnumeratesOneTo254ByDefault()
    {
        var hosts = IpMath.GetSegmentHosts("192.168.1");

        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0]);
        Assert.Equal("192.168.1.254", hosts[^1]);
        Assert.DoesNotContain("192.168.1.0", hosts);
        Assert.DoesNotContain("192.168.1.255", hosts);
    }

    [Fact]
    public void GetSegmentHosts_HonoursExplicitBounds()
    {
        var hosts = IpMath.GetSegmentHosts("10.0.0", first: 10, last: 12);

        Assert.Equal(["10.0.0.10", "10.0.0.11", "10.0.0.12"], hosts);
    }

    [Theory]
    [InlineData("192.168.1.5")]
    [InlineData("192.168")]
    [InlineData("")]
    [InlineData("999.1.1")]
    public void GetSegmentHosts_ReturnsEmpty_ForInvalidSegment(string segment)
        => Assert.Empty(IpMath.GetSegmentHosts(segment));

    // ---- IsIgnorableLocalAddress ------------------------------------------

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.1.2.3", true)]
    [InlineData("169.254.10.20", true)]
    [InlineData("192.168.1.0", true)] // ends in .0 -> treated as a network address
    [InlineData("10.0.0.10", false)]
    [InlineData("192.168.1.5", false)]
    [InlineData("", false)]
    public void IsIgnorableLocalAddress_MatchesLoopbackApipaAndDotZero(string ip, bool expected)
        => Assert.Equal(expected, IpMath.IsIgnorableLocalAddress(ip));
}
