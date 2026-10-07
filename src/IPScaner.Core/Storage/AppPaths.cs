namespace IPScaner.Core.Storage;

/// <summary>
/// Decides where the application keeps its data files.
/// </summary>
/// <remarks>
/// The original tool wrote <c>IPScaner.cfg</c>, <c>IPScanerMemo.dat</c>,
/// <c>command.txt</c>, <c>ipScaner_his.xml</c> and <c>Logs\</c> next to the
/// executable. That is correct for the green/portable build — it is exactly why
/// settings survive when you move the folder around, and it is the layout the
/// tool has always used.
/// <para>
/// It breaks the moment the app is installed under <c>Program Files</c>: a
/// standard user cannot create files there, .NET does not silently virtualise
/// those writes the way some 32-bit apps do, and the result is an application
/// that appears to forget every setting on each launch.
/// </para>
/// <para>
/// So: use the application directory when it is writable (portable mode, fully
/// backward compatible), otherwise fall back to <c>%APPDATA%\IPScaner</c>
/// (installed mode). The same build therefore behaves correctly whether it was
/// unzipped or installed.
/// </para>
/// </remarks>
public static class AppPaths
{
    private const string ProductFolderName = "IPScaner";

    private static readonly Lazy<string> ResolvedDataDirectory = new(Resolve, isThreadSafe: true);
    private static readonly Lazy<bool> ResolvedIsPortable = new(() => Resolve() == AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), isThreadSafe: true);

    /// <summary>Directory that holds the config, memo, history, command and log files.</summary>
    public static string DataDirectory => ResolvedDataDirectory.Value;

    /// <summary>
    /// True when data lives beside the executable (the green/portable layout),
    /// false when it was redirected to the user profile (installed layout).
    /// </summary>
    public static bool IsPortable => ResolvedIsPortable.Value;

    private static string Resolve()
    {
        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (IsWritable(appDirectory)) return appDirectory;

        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProductFolderName);

        try
        {
            Directory.CreateDirectory(roaming);
            return roaming;
        }
        catch
        {
            // Last resort: LocalApplicationData, then the temp directory. Anything
            // is better than throwing during startup.
            try
            {
                var local = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ProductFolderName);
                Directory.CreateDirectory(local);
                return local;
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), ProductFolderName);
            }
        }
    }

    /// <summary>True when a file can actually be created in the directory.</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N")[..8]);
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
