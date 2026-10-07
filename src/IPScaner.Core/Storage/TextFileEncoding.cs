using System.Text;

namespace IPScaner.Core.Storage;

/// <summary>
/// Encoding sniffing for the tool's legacy text files.
/// </summary>
/// <remarks>
/// The original memo/command files may be UTF-8 (with or without BOM),
/// UTF-16 LE/BE, or GBK — the latter being common on Chinese Windows where
/// Notepad saved "ANSI". This mirrors the original detection order so existing
/// files keep loading correctly.
/// </remarks>
public static class TextFileEncoding
{
    static TextFileEncoding()
    {
        // GBK is not in the default .NET Core encoding set.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>UTF-8 without BOM — the encoding new files are written with.</summary>
    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>UTF-8 with BOM — what the original memo writer produced.</summary>
    public static Encoding Utf8Bom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>GBK / code page 936.</summary>
    public static Encoding Gbk
    {
        get
        {
            try { return Encoding.GetEncoding(936); }
            catch { return Encoding.UTF8; }
        }
    }

    /// <summary>Detects the encoding of a file from its byte-order mark and contents.</summary>
    public static Encoding Detect(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            return Detect(bytes);
        }
        catch
        {
            return Encoding.Default;
        }
    }

    /// <summary>Detects the encoding of a byte buffer (BOM first, then GBK heuristic).</summary>
    public static Encoding Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Utf8Bom;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;

        // No BOM: look for bytes that are only plausible as a GBK lead byte.
        // (The original used the same ">= 161" heuristic.)
        foreach (var b in bytes)
        {
            if (b is >= 161 and <= 247) return Gbk;
        }

        return Utf8NoBom;
    }

    /// <summary>Reads all lines using the detected encoding.</summary>
    public static string[] ReadAllLines(string filePath) =>
        File.ReadAllLines(filePath, Detect(filePath));
}
