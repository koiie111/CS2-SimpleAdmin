using CounterStrikeSharp.API;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>Database/plugin readiness, as reported to commands and css_sa_status.</summary>
internal enum PluginState
{
    Starting,
    DatabaseReady,
    Ready,
    Failed
}

/// <summary>
/// Owner of the per-lifetime background machinery:
/// <list type="bullet">
/// <item><see cref="Db"/>: bounded queue for all SQL (commands, connect loads, periodic pass, reloads).
/// 4 workers for MySQL (async I/O), 1 worker for SQLite (synchronous provider ⇒ serialised, off the game thread).</item>
/// <item><see cref="Http"/>: bounded queue for Discord webhooks (notifications may be dropped when full).</item>
/// <item><see cref="Dispatcher"/>: bounded, time-budgeted application of results on the game thread.</item>
/// <item><see cref="Sessions"/>: per-slot connection identity used to reject stale results.</item>
/// </list>
/// Everything is created on Load/OnConfigParsed and stopped on Unload without waiting for running work.
/// </summary>
internal static class Runtime
{
    public const int DbQueueCapacity = 512;
    public const int HttpQueueCapacity = 64;

    private static readonly object StartLock = new();

    public static PluginLifetime Lifetime { get; private set; } = new();
    public static GameDispatcher Dispatcher { get; private set; } = new(Server.NextWorldUpdate);
    public static BoundedWorkQueue? Db { get; private set; }
    public static BoundedWorkQueue? Http { get; private set; }
    public static readonly PlayerSessions Sessions = new();

    private static volatile PluginState _state = PluginState.Starting;
    public static PluginState State
    {
        get => _state;
        set => _state = value;
    }

    public static string? LastError { get; set; }

    /// <summary>Creates the queues for this lifetime. Safe to call more than once.</summary>
    public static void Start(bool sqlite)
    {
        lock (StartLock)
        {
            if (Db != null) return;
            Db = new BoundedWorkQueue("db", DbQueueCapacity, sqlite ? 1 : 4, Lifetime.Token, PluginMetrics.DbQueueWait);
            Http = new BoundedWorkQueue("http", HttpQueueCapacity, 2, Lifetime.Token);
        }
    }

    /// <summary>Unload: cancel background work and drop queued game-thread work. Does not block.</summary>
    public static void Stop()
    {
        lock (StartLock)
        {
            Lifetime.Dispose();
            Dispatcher.Stop();
            Db?.Complete();
            Http?.Complete();
            Db = null;
            Http = null;
            Sessions.Clear();
        }
    }

    /// <summary>Starts a fresh lifetime after a Stop (same assembly re-used).</summary>
    public static void Restart()
    {
        lock (StartLock)
        {
            if (Lifetime.IsAlive && !Dispatcher.IsStopped) return;
            Lifetime = new PluginLifetime();
            Dispatcher = new GameDispatcher(Server.NextWorldUpdate);
            State = PluginState.Starting;
        }
    }

    public static CancellationToken Token => Lifetime.Token;

    /// <summary>Runs <paramref name="action"/> on the game thread (bounded, budgeted). Use from background code.</summary>
    public static Task OnGameThread(Action action) => Dispatcher.PostAsync(action, Lifetime.Token);

    public static Task<T> OnGameThread<T>(Func<T> func) => Dispatcher.PostAsync(func, Lifetime.Token);

    /// <summary>Queues database work. Returns false (nothing runs) when the queue is full or not started.</summary>
    public static bool TryQueueDb(string operation, Func<CancellationToken, Task> work) =>
        Db?.TryEnqueue(operation, work) ?? false;

    public static Task<T>? TryQueueDb<T>(string operation, Func<CancellationToken, Task<T>> work) =>
        Db?.TryEnqueue(operation, work);

    public static string Describe() =>
        $"state={State}{(LastError != null ? $" lastError=\"{LastError}\"" : "")}\n" +
        $"{Db?.Describe() ?? "db: not started"}\n{Http?.Describe() ?? "http: not started"}\n" +
        $"dispatcher: queued={Dispatcher.Count}/{Dispatcher.Capacity} maxItems/update={Dispatcher.MaxItemsPerUpdate}\n";
}
