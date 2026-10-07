using CounterStrikeSharp.API;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>Database/plugin readiness, as reported to commands and css_sa_status.</summary>
internal enum PluginState
{
    Starting,

    /// <summary>
    /// Connected and migrated, but the server row / ban cache / admins are not loaded yet. Reads that do not depend
    /// on the server identity work; <b>server-scoped writes are refused</b> (see <see cref="Runtime.IsOperationReady"/>).
    /// </summary>
    DatabaseReady,

    Ready,
    Failed
}

/// <summary>
/// Everything that identifies one plugin lifetime for the code that was started in it: the lifetime (generation +
/// token) and the dispatcher that belongs to it. Background work captures this when it is <i>accepted</i>, not when
/// it later wants to post a result, so a worker of an old lifetime (for example one still waiting on a SQL call that
/// ignores cancellation) can never land its result in a newer lifetime's dispatcher after
/// <see cref="Runtime.Restart"/>.
/// </summary>
internal sealed class RuntimeContext(PluginLifetime lifetime, GameDispatcher dispatcher)
{
    public PluginLifetime Lifetime { get; } = lifetime;
    public GameDispatcher Dispatcher { get; } = dispatcher;
    public long Generation => Lifetime.Generation;
    public CancellationToken Token => Lifetime.Token;

    /// <summary>True while this is still the runtime's current context and neither it nor its dispatcher was stopped.</summary>
    public bool IsCurrent => ReferenceEquals(Runtime.Context, this) && Lifetime.IsAlive && !Dispatcher.IsStopped;

    /// <summary>
    /// Posts to this context's dispatcher. Fails with <see cref="OperationCanceledException"/> when the context is
    /// stale, both before posting and again on the game thread right before the action would run.
    /// </summary>
    public Task PostAsync(Action action)
    {
        if (!IsCurrent) return Task.FromCanceled(new CancellationToken(true));
        return Dispatcher.PostAsync(() =>
        {
            if (!IsCurrent) throw new OperationCanceledException(Token);
            action();
        }, Token);
    }

    public async Task<T> PostAsync<T>(Func<T> func)
    {
        T result = default!;
        await PostAsync(() => { result = func(); }).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Queues database work for this context only: refused (null) when this context is no longer current, so startup
    /// code of an old lifetime can never put jobs into a newer lifetime's queue.
    /// </summary>
    public Task<T>? TryQueueDb<T>(string operation, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default) =>
        Runtime.TryQueueDbFor(this, operation, work, cancellationToken);

    /// <summary>Non-blocking post for game-thread callers (leaves silently if the context is stale or the queue full).</summary>
    public bool TryPost(Action action) =>
        IsCurrent && Dispatcher.TryPost(() =>
        {
            if (IsCurrent) action();
        });
}

/// <summary>
/// What a queued job was accepted for: the <see cref="RuntimeContext"/> (lifetime/dispatcher) and a snapshot of the
/// server id taken when the work was queued. Exposed to the job as <see cref="Current"/> (an AsyncLocal that the
/// queue worker sets for the duration of the job), which is what <see cref="Runtime.OnGameThread(Action)"/> and
/// <c>CS2_SimpleAdmin.ServerId</c> consult. Code that is <i>not</i> run by a queue worker (timers, startup tasks)
/// captures <see cref="Runtime.Context"/> explicitly instead.
/// </summary>
internal sealed class WorkContext(RuntimeContext runtime, int? serverId, CallerRef? caller = null)
{
    private static readonly AsyncLocal<WorkContext?> Ambient = new();

    public RuntimeContext Runtime { get; } = runtime;

    /// <summary>sa_servers.id at the time the work was accepted; null if none was known (global work).</summary>
    public int? ServerId { get; } = serverId;

    /// <summary>Who issued the command that queued this work, snapshotted on the game thread (null for non-command work).</summary>
    public CallerRef? Caller { get; } = caller;

    public static WorkContext? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }
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
/// <para>
/// Lifetimes: normal CounterStrikeSharp hot reload loads the plugin into a new AssemblyLoadContext, which has its
/// own copy of these statics (a fresh <see cref="Context"/>); old and new never share a dispatcher. A same-assembly
/// <see cref="Restart"/> (Unload followed by Load in the same loaded assembly) reuses the statics, so work of the
/// previous lifetime is fenced by the <see cref="RuntimeContext"/> it captured.
/// </para>
/// </summary>
internal static class Runtime
{
    public const int DbQueueCapacity = 512;
    public const int HttpQueueCapacity = 64;

    private static readonly object StartLock = new();

    private static volatile RuntimeContext _context = new(new PluginLifetime(), new GameDispatcher(Server.NextWorldUpdate));

    /// <summary>The current lifetime + dispatcher. Read once per operation and keep the reference.</summary>
    public static RuntimeContext Context => _context;

    public static PluginLifetime Lifetime => _context.Lifetime;
    public static GameDispatcher Dispatcher => _context.Dispatcher;
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

    /// <summary>
    /// Sets state/error only if <paramref name="context"/> is still current, so startup code of an old lifetime
    /// cannot flip the state of the new one. Returns false when it was ignored.
    /// </summary>
    public static bool TrySetState(RuntimeContext context, PluginState state, string? error = null)
    {
        lock (StartLock)
        {
            if (!context.IsCurrent) return false;
            _state = state;
            LastError = error;
            return true;
        }
    }

    /// <summary>
    /// Publishes the server row id for <paramref name="context"/>'s lifetime; ignored (false) if that lifetime is no
    /// longer current, so a late startup of an old lifetime cannot set the id of the new one.
    /// </summary>
    public static bool TrySetServerId(RuntimeContext context, int serverId)
    {
        lock (StartLock)
        {
            if (!context.IsCurrent) return false;
            CS2_SimpleAdmin.GlobalServerId = serverId;
            return true;
        }
    }

    /// <summary>Starting → DatabaseReady, only for the current context and only from Starting (never downgrades Ready/Failed).</summary>
    public static bool TryMarkDatabaseReady(RuntimeContext context)
    {
        lock (StartLock)
        {
            if (!context.IsCurrent || _state != PluginState.Starting) return false;
            _state = PluginState.DatabaseReady;
            return true;
        }
    }

    /// <summary>
    /// Database connected and migrated (<see cref="PluginState.DatabaseReady"/> or <see cref="PluginState.Ready"/>):
    /// enough for reads and for explicitly global operations.
    /// </summary>
    public static bool IsDatabaseConnected =>
        CS2_SimpleAdmin.DatabaseProvider != null && State is PluginState.Ready or PluginState.DatabaseReady && Db != null;

    /// <summary>
    /// Fully ready for server-scoped operations: <see cref="PluginState.Ready"/> (server row, cache and admins loaded)
    /// <b>and</b> a resolved server id. A write accepted in this state never lands with a NULL server_id.
    /// </summary>
    public static bool IsOperationReady =>
        CS2_SimpleAdmin.DatabaseProvider != null && State == PluginState.Ready && Db != null &&
        CS2_SimpleAdmin.GlobalServerId.HasValue;

    /// <summary>Creates the queues for this lifetime. Safe to call more than once.</summary>
    public static void Start(bool sqlite, int dbCapacity = DbQueueCapacity)
    {
        lock (StartLock)
        {
            if (Db != null) return;
            var token = _context.Token;
            Db = new BoundedWorkQueue("db", dbCapacity, sqlite ? 1 : 4, token, PluginMetrics.DbQueueWait, CaptureWork);
            Http = new BoundedWorkQueue("http", HttpQueueCapacity, 2, token, captureContext: CaptureWork);
        }
    }

    /// <summary>
    /// The context a job accepted now must run under. Work queued from inside a running job <b>inherits that job's
    /// context</b> (lifetime, server id, caller): a producer of an old lifetime must never become a new job of the
    /// current one. Only code that is not run by a queue worker (command, timer) gets the current runtime.
    /// </summary>
    internal static WorkContext CaptureWork() => WorkContext.Current ?? new WorkContext(_context, CS2_SimpleAdmin.GlobalServerId);

    /// <summary>Unload: cancel background work and drop queued game-thread work. Does not block.</summary>
    public static void Stop()
    {
        lock (StartLock)
        {
            var context = _context;
            context.Lifetime.Dispose();
            context.Dispatcher.Stop();
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
            var context = _context;
            if (context.Lifetime.IsAlive && !context.Dispatcher.IsStopped) return;
            _context = new RuntimeContext(new PluginLifetime(), new GameDispatcher(Server.NextWorldUpdate));
            _state = PluginState.Starting;
            LastError = null;
            CS2_SimpleAdmin.GlobalServerId = null; // the new lifetime resolves its own server row
        }
    }

    public static CancellationToken Token => _context.Token;

    /// <summary>Tests/tools: run with a dispatcher that is pumped manually instead of by the engine.</summary>
    internal static void UseDispatcher(GameDispatcher dispatcher)
    {
        lock (StartLock) _context = new RuntimeContext(_context.Lifetime, dispatcher);
    }

    /// <summary>Tests: a fresh lifetime and queues.</summary>
    internal static void ResetForTests(bool sqlite, GameDispatcher dispatcher, int dbCapacity = DbQueueCapacity)
    {
        Stop();
        lock (StartLock)
        {
            _context = new RuntimeContext(new PluginLifetime(), dispatcher);
            _state = PluginState.Ready;
            LastError = null;
            CS2_SimpleAdmin.GlobalServerId = null;
        }

        Start(sqlite, dbCapacity);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the game thread (bounded, budgeted). Use from background code.
    /// Bound to the lifetime the calling job was accepted in (see <see cref="WorkContext"/>); if that lifetime is no
    /// longer current the returned task is cancelled and the action never runs.
    /// </summary>
    public static Task OnGameThread(Action action) => (WorkContext.Current?.Runtime ?? _context).PostAsync(action);

    public static Task<T> OnGameThread<T>(Func<T> func) => (WorkContext.Current?.Runtime ?? _context).PostAsync(func);

    // Queue selection and the staleness check of the producing context happen under StartLock, the lock that Stop and
    // Restart hold: a producer of an old lifetime is refused, and can never pick up a queue created by a newer Start.
    // TryWrite never blocks, so holding the lock is cheap.

    /// <summary>
    /// Queues database work. Returns false (nothing runs) when the queue is full or not started, or when the producing
    /// context (the ambient job context, else the current runtime) is stale.
    /// </summary>
    public static bool TryQueueDb(string operation, Func<CancellationToken, Task> work)
    {
        lock (StartLock)
        {
            var context = CaptureWork();
            return context.Runtime.IsCurrent && (Db?.TryEnqueue(operation, work, context) ?? false);
        }
    }

    /// <summary>
    /// Queues database work under an explicit <see cref="WorkContext"/> (a server id and caller captured by the
    /// code that validated the operation, rather than whatever is current when the queue accepts it). Refused when
    /// that context's lifetime is no longer current.
    /// </summary>
    public static bool TryQueueDb(string operation, Func<CancellationToken, Task> work, WorkContext context)
    {
        lock (StartLock)
            return context.Runtime.IsCurrent && (Db?.TryEnqueue(operation, work, context) ?? false);
    }

    /// <summary>
    /// Like <see cref="TryQueueDb(string, Func{CancellationToken, Task}, WorkContext)"/>, but the job runs only after
    /// every job accepted <b>earlier</b> for the same <paramref name="orderKey"/> (a SteamID64) has ended, and before
    /// every job accepted later. The place in that order is taken now, on the caller's (game) thread, which never
    /// waits; the order itself is kept by the queue's per-key lanes (<see cref="BoundedWorkQueue"/>), so a job that is
    /// waiting for its predecessor holds no worker. A refused job changed nothing: capacity is reserved before the
    /// lane is touched, so a refusal can never detach later jobs from earlier accepted ones.
    /// </summary>
    public static bool TryQueueDbOrdered(string operation, Func<CancellationToken, Task> work, WorkContext context, ulong orderKey)
    {
        lock (StartLock)
            return context.Runtime.IsCurrent && (Db?.TryEnqueue(operation, work, context, orderKey) ?? false);
    }

    /// <summary>Typed variant of <see cref="TryQueueDbOrdered(string, Func{CancellationToken, Task}, WorkContext, ulong)"/>; null when refused.</summary>
    public static Task<T>? TryQueueDbOrdered<T>(string operation, Func<CancellationToken, Task<T>> work, ulong orderKey)
    {
        lock (StartLock)
        {
            var context = CaptureWork();
            return context.Runtime.IsCurrent ? Db?.TryEnqueue(operation, work, default, context, orderKey) : null;
        }
    }

    public static Task<T>? TryQueueDb<T>(string operation, Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        lock (StartLock)
        {
            var context = CaptureWork();
            return context.Runtime.IsCurrent ? Db?.TryEnqueue(operation, work, cancellationToken, context) : null;
        }
    }

    /// <summary>Queues typed work for an explicit runtime context (startup code); refused if that context is stale.</summary>
    internal static Task<T>? TryQueueDbFor<T>(RuntimeContext runtime, string operation, Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        lock (StartLock)
        {
            if (!runtime.IsCurrent) return null;
            var ambient = WorkContext.Current;
            var context = ambient != null && ReferenceEquals(ambient.Runtime, runtime)
                ? ambient
                : new WorkContext(runtime, CS2_SimpleAdmin.GlobalServerId);
            return Db?.TryEnqueue(operation, work, cancellationToken, context);
        }
    }

    public static string Describe() =>
        $"state={State}{(LastError != null ? $" lastError=\"{LastError}\"" : "")} serverId={CS2_SimpleAdmin.GlobalServerId?.ToString() ?? "none"} generation={_context.Generation}\n" +
        $"{Db?.Describe() ?? "db: not started"}\n{Http?.Describe() ?? "http: not started"}\n" +
        $"dispatcher: queued={Dispatcher.Count}/{Dispatcher.Capacity} maxItems/update={Dispatcher.MaxItemsPerUpdate}\n";
}
