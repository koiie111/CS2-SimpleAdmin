using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Logs a given error key at most once per window and reports how many repeats were suppressed,
/// so a DB/HTTP outage produces a bounded number of log lines instead of one per player/tick.
/// </summary>
internal static class RateLimitedLog
{
    private sealed class Entry
    {
        public long LastTimestamp;
        public long Suppressed;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly long Window = Stopwatch.Frequency * 30;

    public static Func<ILogger?> LoggerProvider { get; set; } = () => CS2_SimpleAdmin._logger;

    public static void Error(string key, Exception? ex, string message) => Write(LogLevel.Error, key, ex, message);

    public static void Warning(string key, string message) => Write(LogLevel.Warning, key, null, message);

    private static void Write(LogLevel level, string key, Exception? ex, string message)
    {
        var entry = Entries.GetOrAdd(key, static _ => new Entry { LastTimestamp = long.MinValue / 2 });
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref entry.LastTimestamp);
        if (now - last < Window || Interlocked.CompareExchange(ref entry.LastTimestamp, now, last) != last)
        {
            Interlocked.Increment(ref entry.Suppressed);
            return;
        }

        var suppressed = Interlocked.Exchange(ref entry.Suppressed, 0);
        var text = suppressed > 0 ? $"{message} (+{suppressed} similar suppressed)" : message;
        var logger = LoggerProvider();
        if (logger == null) return;
        if (ex != null)
            logger.Log(level, "[{Key}] {Message}: {Error}", key, text, ex.Message);
        else
            logger.Log(level, "[{Key}] {Message}", key, text);
    }

    internal static void ResetForTests() => Entries.Clear();
}
