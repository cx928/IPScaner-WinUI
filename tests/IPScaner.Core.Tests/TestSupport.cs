using System.Runtime.CompilerServices;

namespace IPScaner.Core.Tests;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> so cache TTL tests never sleep.
/// </summary>
internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}

/// <summary>
/// Scratch directory for tests that need real files, deleted on dispose.
/// </summary>
/// <remarks>
/// The directory is created under the test output folder first, because that
/// location is always writable for the test host (this sandbox denies directory
/// creation under the OS temp folder for child processes). The OS temp folder is
/// used as a fallback so the helpers still behave conventionally elsewhere.
/// </remarks>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace([CallerMemberName] string? label = null)
    {
        var name = $"{Sanitize(label)}-{Guid.NewGuid():N}";
        Root = CreateScratchDirectory(
            Path.Combine(AppContext.BaseDirectory, "scratch", name),
            Path.Combine(Path.GetTempPath(), "ipscaner-core-tests", name));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathFor(string fileName) => Path.Combine(Root, fileName);

    /// <summary>
    /// Folder inside the test output directory where exported samples are kept so
    /// an out-of-process verifier (openpyxl) can open the very file the test wrote.
    /// </summary>
    public static string ArtifactsDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Scratch cleanup is best-effort only.
        }
    }

    private static string CreateScratchDirectory(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Try the next candidate location.
            }
        }

        throw new InvalidOperationException(
            $"Unable to create a scratch directory. Tried: {string.Join(", ", candidates)}");
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "scratch";
        var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length == 0 ? "scratch" : cleaned;
    }
}
