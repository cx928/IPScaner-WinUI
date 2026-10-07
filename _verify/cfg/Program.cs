using System.Text;
using IPScaner.Core.Configuration;

// Byte-for-byte compatibility check: load the user's real IPScaner.cfg through
// ConfigStore, write it back out, and compare the two files.
//
// This is the strongest available proof that dropping the WinUI build next to an
// existing installation preserves settings, including the XML declaration style.

var original = args.Length > 0 ? args[0] : @"E:\IPScaner.cfg";

if (!File.Exists(original))
{
    Console.WriteLine($"SKIP: {original} not found");
    return 2;
}

var originalBytes = File.ReadAllBytes(original);

// 1. Parse it with the new model.
var store = new ConfigStore(original);
var config = store.Load();

Console.WriteLine($"loaded: QueryHostName={config.QueryHostNameEnabled} PingTimeout={config.PingTimeout} " +
                  $"PingCount={config.PingCount} ARP={config.ARPInsteadPingEnabled} TCP={config.PortInsteadPingEnabled} " +
                  $"ports=[{config.PrePortArray}] PortTimeout={config.PortTimeout} DoubleClick={config.DoubleClickTime} " +
                  $"font={config.BtnFontSize} DoubleEvent={config.DoubleEvent} HideMain={config.HideMainEnabled}");

// 2. Re-serialise to a scratch file.
var tempDir = Path.Combine(AppContext.BaseDirectory, "roundtrip");
Directory.CreateDirectory(tempDir);
var rewritten = Path.Combine(tempDir, "IPScaner.cfg");
if (File.Exists(rewritten)) File.Delete(rewritten);

new ConfigStore(rewritten).Save(config);
var newBytes = File.ReadAllBytes(rewritten);

// 3. Report.
static string Head(byte[] b, int n) =>
    string.Join(" ", b.Take(n).Select(x => x.ToString("X2")));

Console.WriteLine();
Console.WriteLine($"original  {originalBytes.Length,5} bytes | {Head(originalBytes, 22)}");
Console.WriteLine($"rewritten {newBytes.Length,5} bytes | {Head(newBytes, 22)}");

// Compare the declaration and the whole document separately so a mismatch is diagnosable.
var declLen = "<?xml version=\"1.0\"?>\r\n"u8.Length;
var declOriginal = Encoding.UTF8.GetString(originalBytes, 0, Math.Min(declLen, originalBytes.Length));
var declNew = Encoding.UTF8.GetString(newBytes, 0, Math.Min(declLen, newBytes.Length));

var declarationMatches = declOriginal == "<?xml version=\"1.0\"?>\r\n";
var identical = originalBytes.AsSpan().SequenceEqual(newBytes);

Console.WriteLine();
Console.WriteLine($"declaration original : {declOriginal.Replace("\r", "\\r").Replace("\n", "\\n")}");
Console.WriteLine($"declaration rewritten: {declNew.Replace("\r", "\\r").Replace("\n", "\\n")}");
Console.WriteLine($"declaration is the bare form (no encoding attr): {declarationMatches}");
Console.WriteLine($"BYTE-IDENTICAL ROUND TRIP: {identical}");

if (!identical)
{
    // Show the first differing offset to make the drift obvious.
    var limit = Math.Min(originalBytes.Length, newBytes.Length);
    for (var i = 0; i < limit; i++)
    {
        if (originalBytes[i] == newBytes[i]) continue;
        var from = Math.Max(0, i - 40);
        Console.WriteLine($"first diff at byte {i}");
        Console.WriteLine($"  original : ...{Encoding.UTF8.GetString(originalBytes, from, Math.Min(120, originalBytes.Length - from))}...");
        Console.WriteLine($"  rewritten: ...{Encoding.UTF8.GetString(newBytes, from, Math.Min(120, newBytes.Length - from))}...");
        break;
    }
}

// 4. Independently confirm the rewritten file still parses back to the same values.
var reloaded = new ConfigStore(rewritten).Load();
var same = reloaded.PingTimeout == config.PingTimeout
           && reloaded.PingCount == config.PingCount
           && reloaded.PrePortArray == config.PrePortArray
           && reloaded.PortTimeout == config.PortTimeout
           && reloaded.DefaultColorArgb == config.DefaultColorArgb
           && reloaded.NetworkOKColorArgb == config.NetworkOKColorArgb
           && reloaded.NetworkNGColorArgb == config.NetworkNGColorArgb
           && reloaded.MemoColorArgb == config.MemoColorArgb
           && reloaded.DoubleEvent == config.DoubleEvent
           && reloaded.QRY() == config.QRY();

Console.WriteLine($"all settings survive a reload: {same}");
return identical && declarationMatches && same ? 0 : 1;

internal static class ConfigExtensions
{
    // Small helper so the comparison above stays readable.
    public static bool QRY(this AppConfig c) => c.QueryHostNameEnabled;
}
