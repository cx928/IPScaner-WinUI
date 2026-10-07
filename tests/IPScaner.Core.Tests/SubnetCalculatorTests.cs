using Xunit;
using IPScaner.Core.Net;

namespace IPScaner.Core.Tests;

/// <summary>
/// Coverage for <see cref="SubnetCalculator"/> — the 网络和IP地址计算器 engine.
/// </summary>
public class SubnetCalculatorTests
{
    // ---- Calculate: table across mask bits ---------------------------------

    /// <summary>
    /// Fixed host 192.168.1.10 against every interesting prefix length. All rows
    /// are complete (a full network/broadcast/first/last quadruple exists).
    /// </summary>
    [Theory]
    [InlineData(8, "192.0.0.0", "192.255.255.255", "192.0.0.1", "192.255.255.254", "16777214")]
    [InlineData(12, "192.160.0.0", "192.175.255.255", "192.160.0.1", "192.175.255.254", "1048574")]
    [InlineData(16, "192.168.0.0", "192.168.255.255", "192.168.0.1", "192.168.255.254", "65534")]
    [InlineData(20, "192.168.0.0", "192.168.15.255", "192.168.0.1", "192.168.15.254", "4094")]
    [InlineData(22, "192.168.0.0", "192.168.3.255", "192.168.0.1", "192.168.3.254", "1022")]
    [InlineData(23, "192.168.0.0", "192.168.1.255", "192.168.0.1", "192.168.1.254", "510")]
    [InlineData(24, "192.168.1.0", "192.168.1.255", "192.168.1.1", "192.168.1.254", "254")]
    [InlineData(25, "192.168.1.0", "192.168.1.127", "192.168.1.1", "192.168.1.126", "126")]
    [InlineData(26, "192.168.1.0", "192.168.1.63", "192.168.1.1", "192.168.1.62", "62")]
    [InlineData(27, "192.168.1.0", "192.168.1.31", "192.168.1.1", "192.168.1.30", "30")]
    [InlineData(28, "192.168.1.0", "192.168.1.15", "192.168.1.1", "192.168.1.14", "14")]
    [InlineData(29, "192.168.1.8", "192.168.1.15", "192.168.1.9", "192.168.1.14", "6")]
    [InlineData(30, "192.168.1.8", "192.168.1.11", "192.168.1.9", "192.168.1.10", "2")]
    public void Calculate_MatchesTable_ForEveryMaskBit(
        int bits, string network, string broadcast, string first, string last, string count)
    {
        var result = SubnetCalculator.Calculate("192.168.1.10", bits);

        Assert.False(result.HasError);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(bits, result.Bits);
        Assert.Equal(SubnetCalculator.MaskString(bits), result.Mask);
        Assert.Equal(network, result.Network);
        Assert.Equal(broadcast, result.Broadcast);
        Assert.Equal(first, result.FirstUsable);
        Assert.Equal(last, result.LastUsable);
        Assert.Equal(count, result.UsableCount);
        Assert.Equal(long.Parse(count), result.UsableCountValue);
        Assert.True(result.IsComplete);
        Assert.Equal($"{network}/{bits}", result.Cidr);
    }

    [Fact]
    public void Calculate_Slash24_MatchesTheDocumentedAnchors()
    {
        var result = SubnetCalculator.Calculate("192.168.1.10", 24);

        Assert.Equal("192.168.1.0", result.Network);
        Assert.Equal("192.168.1.255", result.Broadcast);
        Assert.Equal("192.168.1.1", result.FirstUsable);
        Assert.Equal("192.168.1.254", result.LastUsable);
        Assert.Equal("254", result.UsableCount);
        Assert.Equal(254, result.UsableCountValue);
        Assert.True(result.IsComplete);
        Assert.Equal("192.168.1.0/24", result.Cidr);
    }

    [Fact]
    public void Calculate_Slash31_ReportsTwoHostsAndIsIncomplete()
    {
        var result = SubnetCalculator.Calculate("192.168.1.10", 31);

        Assert.Equal("two hosts", result.UsableCount);
        Assert.False(result.IsComplete);
        Assert.Equal("255.255.255.254", result.Mask);
        Assert.Equal("192.168.1.10", result.Network);
        Assert.Equal("192.168.1.10", result.FirstUsable);
        Assert.Equal("192.168.1.11", result.LastUsable);
        Assert.Null(result.UsableCountValue);
        // The /31 branch never populates Broadcast (kept exactly as the original).
        Assert.Equal(string.Empty, result.Broadcast);
        Assert.Equal("192.168.1.10/31", result.Cidr);
    }

    [Fact]
    public void Calculate_Slash31_NormalisesAnOddHostToThePairBase()
    {
        var result = SubnetCalculator.Calculate("192.168.1.11", 31);

        Assert.Equal("192.168.1.10", result.Network);
        Assert.Equal("192.168.1.11", result.LastUsable);
        Assert.Equal("two hosts", result.UsableCount);
    }

    [Fact]
    public void Calculate_Slash32_ReportsOneHostAndIsIncomplete()
    {
        var result = SubnetCalculator.Calculate("192.168.1.10", 32);

        Assert.Equal("one host", result.UsableCount);
        Assert.False(result.IsComplete);
        Assert.Equal("255.255.255.255", result.Mask);
        Assert.Equal("192.168.1.10", result.Network);
        Assert.Equal("192.168.1.10", result.FirstUsable);
        Assert.Equal(string.Empty, result.LastUsable);
        Assert.Equal(string.Empty, result.Broadcast);
        Assert.Null(result.UsableCountValue);
        Assert.Equal("192.168.1.10/32", result.Cidr);
    }

    [Fact]
    public void Calculate_Slash0_CoversTheWholeAddressSpace()
    {
        var result = SubnetCalculator.Calculate("192.168.1.10", 0);

        Assert.False(result.HasError);
        Assert.Equal("0.0.0.0", result.Mask);
        Assert.Equal("0.0.0.0", result.Network);
        Assert.Equal("255.255.255.255", result.Broadcast);
        Assert.Equal("0.0.0.1", result.FirstUsable);
        Assert.Equal("255.255.255.254", result.LastUsable);
        Assert.Equal("4294967294", result.UsableCount);
        Assert.Equal(4294967294L, result.UsableCountValue);
        Assert.True(result.IsComplete);
        Assert.Equal("0.0.0.0/0", result.Cidr);
    }

    // ---- Calculate: error paths -------------------------------------------

    [Fact]
    public void Calculate_OutOfRangeOctet_YieldsTheOriginalErrorMarker()
    {
        var result = SubnetCalculator.Calculate([192, 168, 1, 300], 24);

        Assert.Equal("错误", result.Error);
        Assert.Equal("错误", result.UsableCount);
        Assert.True(result.HasError);
        Assert.False(result.IsComplete);
        Assert.Equal(string.Empty, result.Mask);
        Assert.Equal(string.Empty, result.Network);
        Assert.Equal(string.Empty, result.Cidr);
    }

    [Fact]
    public void Calculate_TooFewOctets_YieldsTheErrorMarker()
    {
        // A short list means "no octet supplied" for the missing positions.
        var result = SubnetCalculator.Calculate([192, 168], 24);

        Assert.Equal("错误", result.Error);
        Assert.False(result.IsComplete);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Calculate_MaskBitOutOfRange_PutsErrorInMaskField(int bits)
    {
        var result = SubnetCalculator.Calculate([192, 168, 1, 10], bits);

        Assert.Equal("错误", result.Error);
        Assert.Equal("错误", result.Mask);
        Assert.True(result.HasError);
        Assert.False(result.IsComplete);
        Assert.Equal(bits, result.Bits);
    }

    [Theory]
    [InlineData("999.1.1.1")]
    [InlineData("10.0.0.1abc")]
    [InlineData("1.2.3")]
    [InlineData("")]
    public void Calculate_FromString_RejectsMalformedAddresses(string ip)
    {
        var result = SubnetCalculator.Calculate(ip, 24);

        Assert.Equal("错误", result.Error);
        Assert.Equal("错误", result.UsableCount);
        Assert.Equal(24, result.Bits);
    }

    [Fact]
    public void Calculate_StringOverload_AgreesWithTheOctetOverload()
    {
        var fromString = SubnetCalculator.Calculate("192.168.1.10", 26);
        var fromOctets = SubnetCalculator.Calculate([192, 168, 1, 10], 26);

        Assert.Equal(fromOctets.Network, fromString.Network);
        Assert.Equal(fromOctets.Broadcast, fromString.Broadcast);
        Assert.Equal(fromOctets.FirstUsable, fromString.FirstUsable);
        Assert.Equal(fromOctets.LastUsable, fromString.LastUsable);
        Assert.Equal(fromOctets.UsableCount, fromString.UsableCount);
        Assert.Equal(fromOctets.Cidr, fromString.Cidr);
    }

    // ---- MaskOctets / MaskString / WildcardString --------------------------

    [Theory]
    [InlineData(0, "0.0.0.0")]
    [InlineData(1, "128.0.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(9, "255.128.0.0")]
    [InlineData(16, "255.255.0.0")]
    [InlineData(23, "255.255.254.0")]
    [InlineData(24, "255.255.255.0")]
    [InlineData(25, "255.255.255.128")]
    [InlineData(26, "255.255.255.192")]
    [InlineData(27, "255.255.255.224")]
    [InlineData(28, "255.255.255.240")]
    [InlineData(30, "255.255.255.252")]
    [InlineData(31, "255.255.255.254")]
    [InlineData(32, "255.255.255.255")]
    public void MaskString_ReturnsDottedQuadMask(int bits, string expected)
    {
        Assert.Equal(expected, SubnetCalculator.MaskString(bits));
        Assert.Equal(expected.Split('.').Select(int.Parse), SubnetCalculator.MaskOctets(bits));
    }

    [Theory]
    [InlineData(0, "255.255.255.255")]
    [InlineData(8, "0.255.255.255")]
    [InlineData(24, "0.0.0.255")]
    [InlineData(26, "0.0.0.63")]
    [InlineData(30, "0.0.0.3")]
    [InlineData(32, "0.0.0.0")]
    public void WildcardString_IsTheInverseOfTheMask(int bits, string expected)
        => Assert.Equal(expected, SubnetCalculator.WildcardString(bits));

    [Fact]
    public void WildcardString_IsComplementaryForEveryPrefixLength()
    {
        for (var bits = 0; bits <= 32; bits++)
        {
            var mask = SubnetCalculator.MaskOctets(bits);
            var wildcard = SubnetCalculator.WildcardString(bits).Split('.').Select(int.Parse).ToArray();

            for (var i = 0; i < 4; i++) Assert.Equal(255, mask[i] + wildcard[i]);
        }
    }

    [Fact]
    public void MaskOctets_IsAlwaysContiguous()
    {
        for (var bits = 0; bits <= 32; bits++)
        {
            var mask = string.Join(".", SubnetCalculator.MaskOctets(bits));
            Assert.Equal(bits, SubnetCalculator.BitsFromMask(mask));
        }
    }

    // ---- BitsFromMask ------------------------------------------------------

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("/26", 26)]
    [InlineData("26", 26)]
    [InlineData("/0", 0)]
    [InlineData("0", 0)]
    [InlineData("0.0.0.0", 0)]
    [InlineData("255.255.255.255", 32)]
    [InlineData("255.255.255.254", 31)]
    [InlineData("255.255.255.128", 25)]
    [InlineData(" 255.255.255.0 ", 24)] // trimmed
    [InlineData("255.0.255.0", -1)]     // non-contiguous
    [InlineData("255.255.0.255", -1)]
    [InlineData("0.255.255.255", -1)]
    [InlineData("garbage", -1)]
    [InlineData("999.1.1.1", -1)]
    [InlineData("", -1)]
    [InlineData("   ", -1)]
    [InlineData(null, -1)]
    [InlineData("/33", -1)]
    [InlineData("33", -1)]
    [InlineData("-1", -1)]
    public void BitsFromMask_ConvertsOrRejects(string? maskText, int expected)
        => Assert.Equal(expected, SubnetCalculator.BitsFromMask(maskText));

    // ---- HostsForMask ------------------------------------------------------

    [Fact]
    public void HostsForMask_Slash24_Returns254Hosts()
    {
        var hosts = SubnetCalculator.HostsForMask("192.168.1.10", 24);

        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0]);
        Assert.Equal("192.168.1.254", hosts[^1]);
        Assert.DoesNotContain("192.168.1.0", hosts);
        Assert.DoesNotContain("192.168.1.255", hosts);
    }

    [Fact]
    public void HostsForMask_Slash31_ReturnsExactlyTwoHosts()
    {
        var hosts = SubnetCalculator.HostsForMask("192.168.1.10", 31);

        Assert.Equal(2, hosts.Count);
        Assert.Equal(["192.168.1.10", "192.168.1.11"], hosts);
    }

    [Fact]
    public void HostsForMask_Slash32_ReturnsExactlyOneHost()
    {
        var hosts = SubnetCalculator.HostsForMask("192.168.1.10", 32);

        Assert.Single(hosts);
        Assert.Equal("192.168.1.10", hosts[0]);
    }

    [Fact]
    public void HostsForMask_Slash30_ReturnsTheTwoUsableHosts()
    {
        var hosts = SubnetCalculator.HostsForMask("192.168.1.10", 30);

        Assert.Equal(["192.168.1.9", "192.168.1.10"], hosts);
    }

    [Fact]
    public void HostsForMask_Slash23_KeepsDotZeroAndDot255Hosts()
    {
        // Documented improvement over the original, which dropped every .0/.255
        // address even when it was a legitimate host of a /23 or shorter prefix.
        var hosts = SubnetCalculator.HostsForMask("192.168.1.10", 23);

        Assert.Equal(510, hosts.Count);
        Assert.Equal("192.168.0.1", hosts[0]);
        Assert.Equal("192.168.1.254", hosts[^1]);
        Assert.Contains("192.168.1.0", hosts);
        Assert.Contains("192.168.0.255", hosts);
    }

    [Fact]
    public void HostsForMask_Slash16_Returns65534Hosts()
    {
        var hosts = SubnetCalculator.HostsForMask("192.168.1.10", 16);

        Assert.Equal(65534, hosts.Count);
        Assert.Equal("192.168.0.1", hosts[0]);
        Assert.Equal("192.168.255.254", hosts[^1]);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(-1)]
    public void HostsForMask_RejectsOutOfRangeBits(int bits)
        => Assert.Empty(SubnetCalculator.HostsForMask("192.168.1.10", bits));

    [Theory]
    [InlineData("999.1.1.1")]
    [InlineData("192.168.1")]
    [InlineData("")]
    public void HostsForMask_RejectsMalformedSeed(string seed)
        => Assert.Empty(SubnetCalculator.HostsForMask(seed, 24));
}
