using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CS2_SimpleAdmin.Database;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin;

/// <summary>
/// Startup sequence without waiting on the game thread:
/// <c>connect (bounded retries) → migrations → [game thread: server convars] → server row → bans cache → admins → Ready</c>.
/// Ready is raised at most once per plugin lifetime, only after the cache and admins are loaded.
/// </summary>
public partial class CS2_SimpleAdmin
{
    private static readonly TimeSpan[] ConnectRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)];

    private Task<bool> _databaseInit = Task.FromResult(false);
    private long _readyFiredGeneration;

    /// <summary>Completes with true when the database is connected and migrated for this lifetime.</summary>
    internal Task<bool> DatabaseInitTask => _databaseInit;

    private void StartDatabaseInitialization()
    {
        if (DatabaseProvider == null) return;
        var provider = DatabaseProvider;
        var token = Runtime.Token;
        Runtime.State = PluginState.Starting;
        Runtime.LastError = null;
        // Task.Run: SQLite's "async" open is synchronous and must not run on this (game) thread.
        _databaseInit = Task.Run(() => InitializeDatabaseAsync(provider, token), token);
    }

    internal static async Task<bool> InitializeDatabaseAsync(IDatabaseProvider provider, CancellationToken token,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        retryDelays ??= ConnectRetryDelays;
        string? error = null;
        for (var attempt = 0; ; attempt++)
        {
            var (success, exception) = await provider.CheckConnectionAsync().ConfigureAwait(false);
            if (success) break;
            error = exception;
            if (attempt >= retryDelays.Count)
            {
                Runtime.State = PluginState.Failed;
                Runtime.LastError = $"database connection failed: {error}";
                _logger?.LogError("Problem with database connection! \n{exception}", error);
                return false;
            }

            RateLimitedLog.Warning("init.connect", $"Database connection failed (attempt {attempt + 1}), retrying: {error}");
            await Task.Delay(retryDelays[attempt], token).ConfigureAwait(false);
        }

        try
        {
            await provider.DatabaseMigrationAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Runtime.State = PluginState.Failed;
            Runtime.LastError = ex.Message;
            _logger?.LogError("Database migrations failed, plugin is not ready: {error}", ex.Message);
            return false;
        }

        if (Runtime.State == PluginState.Starting)
            Runtime.State = PluginState.DatabaseReady;
        return true;
    }

    /// <summary>
    /// Game thread. Marks the plugin ready, raises OnSimpleAdminReady once per lifetime and queues the
    /// connect loads of players who joined while the plugin was starting.
    /// </summary>
    internal void MarkReady()
    {
        GameThread.AssertCurrent(nameof(MarkReady));
        ServerLoaded = true;
        Runtime.State = PluginState.Ready;
        Runtime.LastError = null;

        var generation = Runtime.Lifetime.Generation;
        if (Interlocked.Exchange(ref _readyFiredGeneration, generation) != generation)
        {
            try
            {
                SimpleAdminApi?.OnSimpleAdminReadyEvent();
            }
            catch (Exception ex)
            {
                _logger?.LogError("OnSimpleAdminReady subscriber failed: {error}", ex.Message);
            }
        }

        PlayerManager.LoadPendingSessions();
    }

    /// <summary>
    /// For commands that need the database: true when usable, otherwise replies with the current state.
    /// </summary>
    internal static bool EnsureDatabaseReady(CommandInfo? command)
    {
        if (DatabaseProvider != null && Runtime.State is PluginState.Ready or PluginState.DatabaseReady && Runtime.Db != null)
            return true;

        var message = Runtime.State switch
        {
            PluginState.Failed => $"[CS2-SimpleAdmin] Database unavailable: {Runtime.LastError ?? "unknown error"}",
            _ => "[CS2-SimpleAdmin] Plugin is still starting (database not ready yet), try again in a moment."
        };
        if (command != null)
            command.ReplyToCommand(message);
        else
            RateLimitedLog.Warning("not-ready", message);
        return false;
    }

    /// <summary>
    /// Queues a mandatory database write (ban, mute, warn, unban…). If the bounded queue is full, nothing is
    /// applied and the caller is told explicitly; the penalty is never silently dropped.
    /// </summary>
    internal static bool TryQueuePenaltyWork(CCSPlayerController? caller, CommandInfo? command, string operation,
        Func<CancellationToken, Task> work)
    {
        if (!EnsureDatabaseReady(command))
        {
            if (command == null && caller is { IsValid: true })
                caller.PrintToChat("[CS2-SimpleAdmin] Database not ready - the action was NOT saved. Try again in a moment.");
            return false;
        }

        if (Runtime.TryQueueDb(operation, work)) return true;

        const string message = "[CS2-SimpleAdmin] Database queue is full or unavailable - the action was NOT saved. Try again.";
        if (command != null)
            command.ReplyToCommand(message);
        else if (caller is { IsValid: true })
            caller.PrintToChat(message);
        else
            CounterStrikeSharp.API.Server.PrintToConsole(message);
        _logger?.LogError("{Operation} was rejected: database queue full or unavailable", operation);
        return false;
    }

    /// <summary>Tells the issuing admin (or the console) that a queued write failed in the database.</summary>
    internal static Task ReportWriteFailureAsync(CCSPlayerController? caller, string what)
    {
        var callerSlot = caller?.Slot;
        var callerSteamId = caller?.SteamID;
        return Runtime.OnGameThread(() =>
        {
            var message = $"[CS2-SimpleAdmin] {what} could NOT be saved to the database (see server log).";
            var target = callerSlot.HasValue ? CounterStrikeSharp.API.Utilities.GetPlayerFromSlot(callerSlot.Value) : null;
            if (target is { IsValid: true } && target.SteamID == callerSteamId)
                target.PrintToChat(message);
            else
                CounterStrikeSharp.API.Server.PrintToConsole(message);
        });
    }

    /// <summary>
    /// PlayersInfo entry of a connected player, or a fresh snapshot when the connect load has not finished yet
    /// (the old indexer threw KeyNotFoundException and aborted the command).
    /// </summary>
    internal static CS2_SimpleAdminApi.PlayerInfo GetPlayerInfo(CCSPlayerController player) =>
        PlayersInfo.TryGetValue(player.SteamID, out var info) ? info : CreatePlayerInfoSnapshot(player);

    internal static CS2_SimpleAdminApi.PlayerInfo CreatePlayerInfoSnapshot(CCSPlayerController player) =>
        new(player.UserId, player.Slot, new CounterStrikeSharp.API.Modules.Entities.SteamID(player.SteamID),
            string.IsNullOrEmpty(player.PlayerName) ? _localizer?["sa_unknown"] ?? "Unknown" : player.PlayerName,
            player.IpAddress?.Split(':')[0]);

    // ---------------------------------------------------------------- admins reload (coalesced)

    private static readonly object AdminReloadGate = new();
    private static bool _adminReloadBusy;
    private static TaskCompletionSource? _adminReloadPending;

    /// <summary>
    /// Reloads SQL admins/groups. Concurrent requests coalesce: while one reload runs, every new request shares
    /// a single follow-up reload, so N requests cause at most 2 reloads and never queue N file writes/timers.
    /// </summary>
    internal Task ReloadAdminsAsync()
    {
        lock (AdminReloadGate)
        {
            if (_adminReloadBusy)
            {
                Interlocked.Increment(ref PluginMetrics.ReloadAdminsCoalesced);
                _adminReloadPending ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _adminReloadPending.Task;
            }

            _adminReloadBusy = true;
        }

        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => AdminReloadLoopAsync(first));
        return first.Task;
    }

    private async Task AdminReloadLoopAsync(TaskCompletionSource? current)
    {
        while (current != null)
        {
            try
            {
                await ReloadAdminsOnceAsync(Runtime.Token).ConfigureAwait(false);
                current.TrySetResult();
            }
            catch (Exception ex)
            {
                // A failed DB read aborts here, leaving current permissions untouched
                if (ex is not OperationCanceledException)
                    _logger?.LogError("Unable to reload admins: {exception}", ex.Message);
                current.TrySetResult();
            }

            lock (AdminReloadGate)
            {
                current = _adminReloadPending;
                _adminReloadPending = null;
                if (current == null) _adminReloadBusy = false;
            }
        }
    }

    private async Task ReloadAdminsOnceAsync(CancellationToken token)
    {
        var permissionManager = PermissionManager;
        var job = Runtime.TryQueueDb("admins-reload", async ct =>
        {
            var groupsWritten = await permissionManager.CreateGroupsJsonFile().ConfigureAwait(false);
            var (admins, adminsWritten) = await permissionManager.CreateAdminsJsonFileWithStatus().ConfigureAwait(false);
            return (groupsWritten, admins, adminsWritten);
        }) ?? throw new InvalidOperationException("database queue full");

        var (groupsWritten, admins, adminsWritten) = await job.ConfigureAwait(false);
        var adminsPath = ModuleDirectory + "/data/admins.json";
        var groupsPath = ModuleDirectory + "/data/groups.json";

        // Three separate game-thread items: the dispatcher's time budget can split them over world updates.
        // Order kept from the original: strip old + load admins, load groups, load admins again (CSS merges group
        // flags/immunity into admins that already exist, and only into domains that exist).
        await Runtime.OnGameThread(() =>
        {
            if (!adminsWritten) return;
            PermissionManager.ApplyAdminCache(admins);
            AdminManager.LoadAdminData(adminsPath);
        }).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await Runtime.OnGameThread(() =>
        {
            if (groupsWritten) AdminManager.LoadAdminGroups(groupsPath);
        }).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await Runtime.OnGameThread(() =>
        {
            if (adminsWritten) AdminManager.LoadAdminData(adminsPath);
            _logger?.LogInformation("Loaded admins!");
        }).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- diagnostics

    [RequiresPermissions("@css/root")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER, usage: "[reset]")]
    public void OnPerfCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.GetArg(1).Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            PluginMetrics.ResetHistograms();
            command.ReplyToCommand("[CS2-SimpleAdmin] perf histograms reset");
            return;
        }

        var text = Runtime.Describe() + PluginMetrics.Describe(() => CacheManager?.Describe() ?? "cache: none\n");
        foreach (var line in text.Split('\n'))
            if (line.Length > 0) command.ReplyToCommand(line);
    }
}
