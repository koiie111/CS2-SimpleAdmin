using System.Diagnostics;
using System.Text;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Allocation-free latency histogram. Buckets are powers of two in microseconds (1µs .. ~33s).
/// Record is cheap and lock-free so it can wrap hot game-thread callbacks; percentiles are approximate
/// (upper bound of the bucket) and computed only when printed.
/// </summary>
internal sealed class LatencyHistogram(string name)
{
    private const int BucketCount = 26;
    private readonly long[] _buckets = new long[BucketCount];
    private long _count;
    private long _maxTicks;
    private long _totalTicks;

    public string Name { get; } = name;

    public long Count => Interlocked.Read(ref _count);

    public static long Now() => Stopwatch.GetTimestamp();

    public void RecordSince(long startTimestamp) => Record(Stopwatch.GetTimestamp() - startTimestamp);

    public void Record(long elapsedTicks)
    {
        if (elapsedTicks < 0) elapsedTicks = 0;
        var us = elapsedTicks * 1_000_000 / Stopwatch.Frequency;
        var bucket = us <= 1 ? 0 : Math.Min(BucketCount - 1, 64 - (int)ulong.LeadingZeroCount((ulong)us - 1));
        Interlocked.Increment(ref _buckets[bucket]);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _totalTicks, elapsedTicks);

        long max;
        while (elapsedTicks > (max = Interlocked.Read(ref _maxTicks)) &&
               Interlocked.CompareExchange(ref _maxTicks, elapsedTicks, max) != max)
        {
        }
    }

    /// <summary>Upper bucket bound (µs) under which the given fraction of samples fall.</summary>
    public double PercentileMicros(double fraction)
    {
        var total = Count;
        if (total == 0) return 0;
        var target = (long)Math.Ceiling(total * fraction);
        long seen = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            seen += Interlocked.Read(ref _buckets[i]);
            if (seen >= target) return 1L << i;
        }

        return 1L << (BucketCount - 1);
    }

    public double MaxMicros => Interlocked.Read(ref _maxTicks) * 1_000_000.0 / Stopwatch.Frequency;

    public double MeanMicros
    {
        get
        {
            var c = Count;
            return c == 0 ? 0 : Interlocked.Read(ref _totalTicks) * 1_000_000.0 / Stopwatch.Frequency / c;
        }
    }

    public void Reset()
    {
        for (var i = 0; i < BucketCount; i++) Interlocked.Exchange(ref _buckets[i], 0);
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _maxTicks, 0);
        Interlocked.Exchange(ref _totalTicks, 0);
    }

    public string Describe() =>
        $"{Name,-26} n={Count,-8} mean={MeanMicros,8:F1}us p50<={PercentileMicros(0.50),7}us p95<={PercentileMicros(0.95),7}us p99<={PercentileMicros(0.99),7}us max={MaxMicros,9:F1}us";
}

/// <summary>
/// Process-wide counters and histograms for the plugin. Printed by css_sa_perf.
/// </summary>
internal static class PluginMetrics
{
    // Game-thread handlers
    public static readonly LatencyHistogram CommandListener = new("game.command_listener");
    public static readonly LatencyHistogram ChatPenaltyCheck = new("game.chat_penalty_check");
    public static readonly LatencyHistogram ConnectHandler = new("game.connect_handler");
    public static readonly LatencyHistogram PeriodicTimer = new("game.periodic_timer");
    public static readonly LatencyHistogram RenameTimer = new("game.rename_timer");
    public static readonly LatencyHistogram DispatcherPump = new("game.dispatcher_pump");
    public static readonly LatencyHistogram DispatcherItem = new("game.dispatcher_item");

    // Background
    public static readonly LatencyHistogram ConnectLoad = new("bg.connect_load");
    public static readonly LatencyHistogram PeriodicPass = new("bg.periodic_pass");
    public static readonly LatencyHistogram CacheRefresh = new("bg.cache_refresh");
    public static readonly LatencyHistogram CacheFullBuild = new("bg.cache_full_build");
    public static readonly LatencyHistogram DbQueueWait = new("bg.db_queue_wait");
    public static readonly LatencyHistogram ApplyLatency = new("bg->game.apply_latency");

    public static long DbJobsRejected;
    public static long DbJobsFailed;
    public static long DbJobsCompleted;
    public static long HttpRejected;
    public static long HttpFailed;
    public static long HttpSent;
    public static long HttpRateLimited;
    public static long DispatcherRejected;
    public static long DispatcherDroppedStale;
    public static long DispatcherDeferredUpdates;
    public static long StaleSessionResults;
    public static long PeriodicSkippedOverlap;
    public static long ReloadAdminsCoalesced;
    public static long CacheReconciles;
    public static long ConnectDeduplicated;

    private static readonly LatencyHistogram[] All =
    [
        CommandListener, ChatPenaltyCheck, ConnectHandler, PeriodicTimer, RenameTimer, DispatcherPump, DispatcherItem,
        ConnectLoad, PeriodicPass, CacheRefresh, CacheFullBuild, DbQueueWait, ApplyLatency
    ];

    public static string Describe(Func<string>? extra = null)
    {
        var sb = new StringBuilder();
        foreach (var h in All) sb.AppendLine(h.Describe());
        sb.AppendLine(
            $"db: completed={Interlocked.Read(ref DbJobsCompleted)} failed={Interlocked.Read(ref DbJobsFailed)} rejected={Interlocked.Read(ref DbJobsRejected)}");
        sb.AppendLine(
            $"http: sent={Interlocked.Read(ref HttpSent)} failed={Interlocked.Read(ref HttpFailed)} rejected={Interlocked.Read(ref HttpRejected)} 429={Interlocked.Read(ref HttpRateLimited)}");
        sb.AppendLine(
            $"dispatcher: rejected={Interlocked.Read(ref DispatcherRejected)} stale={Interlocked.Read(ref DispatcherDroppedStale)} deferredUpdates={Interlocked.Read(ref DispatcherDeferredUpdates)}");
        sb.AppendLine(
            $"misc: staleSession={Interlocked.Read(ref StaleSessionResults)} periodicSkipped={Interlocked.Read(ref PeriodicSkippedOverlap)} reloadCoalesced={Interlocked.Read(ref ReloadAdminsCoalesced)} reconciles={Interlocked.Read(ref CacheReconciles)} connectDedup={Interlocked.Read(ref ConnectDeduplicated)}");
        sb.AppendLine(
            $"gc: gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} heap={GC.GetTotalMemory(false) / 1024}KB pause={GC.GetTotalPauseDuration().TotalMilliseconds:F1}ms");
        if (extra != null) sb.Append(extra());
        return sb.ToString();
    }

    public static void ResetHistograms()
    {
        foreach (var h in All) h.Reset();
    }
}
