using System.Collections.Concurrent;
using System.Text;

namespace IPScaner.Core.Logging;

/// <summary>
/// Debug log writer, preserving the original tool's on-disk contract:
/// <c>Logs\yyyy-MM-dd.log</c> under the application directory, one line per entry
/// formatted <c>yyyy-MM-dd HH:mm:ss - Caller.Message  text</c>, with files older
/// than 30 days pruned.
/// </summary>
/// <remarks>
/// Writes are queued to a background flush loop exactly like the original, but
/// the queue is bounded so a hung disk cannot exhaust memory during a /16 sweep.
/// </remarks>
public sealed class AppLog
{
    private const int MaxQueuedEntries = 8192;
    private const int RetentionDays = 30;

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), MaxQueuedEntries);
    private readonly string _directory;
    private readonly Task _writer;
    private readonly TimeProvider _clock;

    public static AppLog Instance { get; } = new();

    /// <summary>Set from configuration; when false, queued entries are discarded.</summary>
    public bool Enabled { get; set; }

    public AppLog(string? directory = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _directory = directory ?? Path.Combine(Storage.AppPaths.DataDirectory, "Logs");
        _writer = Task.Factory.StartNew(FlushLoop, TaskCreationOptions.LongRunning);
    }

    /// <summary>Queues a line attributed to the calling method.</summary>
    public void Log(string message) => Write(CallerName(), message);

    public void Log(string category, string message) => Write(category, message);

    private void Write(string category, string message)
    {
        if (!Enabled) return;
        var line = $"{_clock.GetLocalNow():yyyy-MM-dd HH:mm:ss} - {category}  {message}";
        _queue.TryAdd(line);
    }

    private static string CallerName()
    {
        try
        {
            var frame = new System.Diagnostics.StackTrace(2, false).GetFrame(0);
            var method = frame?.GetMethod();
            return method is null ? "AppLog" : $"{method.DeclaringType?.Name}.{method.Name}";
        }
        catch
        {
            return "AppLog";
        }
    }

    private void FlushLoop()
    {
        var lastPrune = DateTime.MinValue.Date;
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (!Enabled) continue; // re-check at flush time
                Directory.CreateDirectory(_directory);
                var today = _clock.GetLocalNow().Date;
                var path = Path.Combine(_directory, $"{today:yyyy-MM-dd}.log");
                using var writer = new StreamWriter(path, append: true, Encoding.UTF8);
                writer.WriteLine(line);

                if (today != lastPrune)
                {
                    lastPrune = today;
                    PruneOldFiles(today);
                }
            }
            catch
            {
                // Logging must never take the application down.
            }
        }
    }

    private void PruneOldFiles(DateTime today)
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            foreach (var file in new DirectoryInfo(_directory).GetFiles("*.log"))
            {
                if (today.Subtract(file.CreationTime).TotalDays >= RetentionDays)
                {
                    try { file.Delete(); } catch { /* in use */ }
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Flushes and stops the background writer (called on app exit).</summary>
    public void Shutdown()
    {
        try
        {
            _queue.CompleteAdding();
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // shutting down anyway
        }
    }
}
