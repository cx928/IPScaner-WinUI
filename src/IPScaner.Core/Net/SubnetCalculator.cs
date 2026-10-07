namespace IPScaner.Core.Net;

/// <summary>Outcome of a subnet calculation, including the original's error strings.</summary>
public sealed class SubnetResult
{
    /// <summary>错误 marker used by the original tool; empty when the input was valid.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>True when the mask could be computed and full first/last addresses exist.</summary>
    public bool IsComplete { get; init; }

    public int Bits { get; init; }

    public string Mask { get; init; } = string.Empty;
    public string Network { get; init; } = string.Empty;
    public string Broadcast { get; init; } = string.Empty;
    public string FirstUsable { get; init; } = string.Empty;
    public string LastUsable { get; init; } = string.Empty;

    /// <summary>
    /// Number of usable addresses. Rendered as a plain count for ordinary masks,
    /// or the literal phrases "two hosts" / "one host" for /31 and /32 — kept
    /// verbatim from the original calculator.
    /// </summary>
    public string UsableCount { get; init; } = string.Empty;

    /// <summary>Non-null usable count when <see cref="UsableCount"/> is numeric.</summary>
    public long? UsableCountValue { get; init; }

    /// <summary>e.g. "192.168.1.0/24".</summary>
    public string Cidr => string.IsNullOrEmpty(Network) ? string.Empty : $"{Network}/{Bits}";

    public bool HasError => !string.IsNullOrEmpty(Error);
}

/// <summary>
/// Subnet / CIDR calculator — the engine behind 网络和IP地址计算器.
/// </summary>
/// <remarks>
/// A faithful port of the original <c>NetworkCalculator.CalNBFL</c>. The observable
/// behaviour is preserved exactly, including the quirky cases:
/// <list type="bullet">
/// <item>An out-of-range octet yields the literal text "错误" in the count field.</item>
/// <item>An unparsable mask bit yields "错误" in the mask field only.</item>
/// <item>/31 and /32 produce the English phrases "two hosts" / "one host" and are
/// reported as incomplete.</item>
/// </list>
/// The mask-bit range accepted here is 0..32 (the engine always supported this);
/// the original dialog simply capped its input control at 30, leaving /31 and /32
/// unreachable. Callers may apply a narrower range if they want the old limits.
/// </remarks>
public static class SubnetCalculator
{
    public const string ErrorText = "错误";
    public const string TwoHostsText = "two hosts";
    public const string OneHostText = "one host";

    /// <summary>Calculates from four octets and a mask bit count.</summary>
    public static SubnetResult Calculate(IReadOnlyList<int> octets, int bits)
    {
        for (var i = 0; i < 4; i++)
        {
            var v = i < octets.Count ? octets[i] : -1;
            if (v is < 0 or > 255)
            {
                return new SubnetResult { Error = ErrorText, UsableCount = ErrorText, Bits = bits };
            }
        }

        if (bits is < 0 or > 32)
        {
            return new SubnetResult { Error = ErrorText, Mask = ErrorText, Bits = bits };
        }

        var mask = MaskOctets(bits);
        var ip = octets.Take(4).ToArray();
        var maskStr = string.Join(".", mask);

        switch (bits)
        {
            case 31:
                return new SubnetResult
                {
                    Bits = bits,
                    Mask = maskStr,
                    Network = string.Join(".", ip.Zip(mask, (a, m) => a & m)),
                    FirstUsable = string.Join(".", ip.Zip(mask, (a, m) => a & m)),
                    LastUsable = string.Join(".", ip.Zip(mask, (a, m) => a | (~m & 0xFF))),
                    UsableCount = TwoHostsText,
                    IsComplete = false,
                };

            case 32:
                return new SubnetResult
                {
                    Bits = bits,
                    Mask = maskStr,
                    Network = string.Join(".", ip),
                    FirstUsable = string.Join(".", ip),
                    UsableCount = OneHostText,
                    IsComplete = false,
                };

            default:
            {
                var network = ip.Zip(mask, (a, m) => a & m).ToArray();
                var broadcast = ip.Zip(mask, (a, m) => a | (~m & 0xFF)).ToArray();

                var first = (int[])network.Clone();
                first[3] += 1;

                var last = (int[])broadcast.Clone();
                last[3] -= 1;

                var count = (long)Math.Pow(2, 32 - bits) - 2;
                if (count < 0) count = 0;

                return new SubnetResult
                {
                    Bits = bits,
                    Mask = maskStr,
                    Network = string.Join(".", network),
                    Broadcast = string.Join(".", broadcast),
                    FirstUsable = string.Join(".", first),
                    LastUsable = string.Join(".", last),
                    UsableCount = count.ToString(),
                    UsableCountValue = count,
                    IsComplete = true,
                };
            }
        }
    }

    /// <summary>Calculates from "192.168.1.10" and a mask bit count.</summary>
    public static SubnetResult Calculate(string ip, int bits)
    {
        if (!IpMath.TryParseOctets(ip, out var octets))
        {
            return new SubnetResult { Error = ErrorText, UsableCount = ErrorText, Bits = bits };
        }
        return Calculate(octets, bits);
    }

    /// <summary>Dotted-quad mask for a bit count, e.g. 24 -> "255.255.255.0".</summary>
    public static int[] MaskOctets(int bits)
    {
        var result = new int[4];
        for (var i = 0; i < 4; i++)
        {
            var remaining = bits - i * 8;
            result[i] = remaining >= 8 ? 255 : remaining <= 0 ? 0 : (0xFF << (8 - remaining)) & 0xFF;
        }
        return result;
    }

    /// <summary>Dotted-quad mask string for a bit count.</summary>
    public static string MaskString(int bits) => string.Join(".", MaskOctets(bits));

    /// <summary>Wildcard (inverse) mask, e.g. 24 -> "0.0.0.255".</summary>
    public static string WildcardString(int bits) =>
        string.Join(".", MaskOctets(bits).Select(o => 255 - o));

    /// <summary>
    /// Converts an "x.x.x.x" mask or a "/nn" CIDR to a bit count.
    /// Returns -1 when the mask is not contiguous.
    /// </summary>
    public static int BitsFromMask(string? maskText)
    {
        if (string.IsNullOrWhiteSpace(maskText)) return -1;
        var text = maskText.Trim().TrimStart('/');
        if (int.TryParse(text, out var direct)) return direct is >= 0 and <= 32 ? direct : -1;
        if (!IpMath.TryParseOctets(text, out var octets)) return -1;

        var bits = 0;
        var sawZero = false;
        foreach (var o in octets)
        {
            for (var b = 7; b >= 0; b--)
            {
                var set = (o & (1 << b)) != 0;
                if (set)
                {
                    if (sawZero) return -1; // non-contiguous
                    bits++;
                }
                else sawZero = true;
            }
        }
        return bits;
    }

    /// <summary>
    /// The host range a mask implies, used by the "掩码位" batch-scan mode.
    /// Unlike the original this does not silently drop .0/.255 hosts of a /23 or
    /// shorter prefix; it returns the true usable range for the prefix length.
    /// </summary>
    public static List<string> HostsForMask(string seedIp, int bits)
    {
        if (bits is < 0 or > 32 || !IpMath.TryParseOctets(seedIp, out var ip)) return [];

        var mask = MaskOctets(bits);
        var network = ip.Zip(mask, (a, m) => a & m).ToArray();
        var broadcast = ip.Zip(mask, (a, m) => a | (~m & 0xFF)).ToArray();

        var list = new List<string>();
        var first = (int[])network.Clone();
        var last = (int[])broadcast.Clone();

        if (bits is 32) return [string.Join(".", ip)];
        if (bits is 31) return [string.Join(".", network), string.Join(".", broadcast)];

        first[3] += 1;
        last[3] -= 1;

        var start = IpMath.ToUInt32(string.Join(".", first));
        var end = IpMath.ToUInt32(string.Join(".", last));
        const int maxCount = 65536;
        for (var v = start; v <= end && list.Count < maxCount; v++) list.Add(IpMath.FromUInt32(v));
        return list;
    }
}
