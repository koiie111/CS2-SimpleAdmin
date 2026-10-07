using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CS2_SimpleAdmin.Database;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin;

/// <summary>
/// What a database operation needs to be accepted. Connectivity (<see cref="Global"/>) is weaker than readiness for
/// the server (<see cref="Server"/>): see <see cref="Runtime.IsDatabaseConnected"/> / <see cref="Runtime.IsOperationReady"/>.
/// </summary>
internal enum OperationScope
{
    /// <summary>The operation reads or writes rows scoped by server_id: needs <c>Ready</c> and a resolved server id.</summary>
    Server,

    /// <summary>
    /// Deliberately not tied to this server's row (the rename table, an explicitly global admin/group, a reload of
    /// admins): needs only a connected, migrated database. Using this is a statement that NULL/absent server_id is intended.
    /// </summary>
    Global
}

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
        var context = Runtime.Context; // the lifetime this startup belongs to
        Runtime.TrySetState(context, PluginState.Starting);
        // Task.Run: SQLite's "async" open is synchronous and must not run on this (game) thread.
        _databaseInit = Task.Run(() => InitializeDatabaseAsync(provider, context.Token, context: context), context.Token);
    }

    /// <param name="context">
    /// The lifetime that started the initialization (default: the current one). State changes are applied only while
    /// it is still current, so a slow startup of a previous lifetime cannot overwrite the state of the new one.
    /// </param>
    internal static async Task<bool> InitializeDatabaseAsync(IDatabaseProvider provider, CancellationToken token,
        IReadOnlyList<TimeSpan>? retryDelays = null, RuntimeContext? context = null)
    {
        context ??= Runtime.Context;
        retryDelays ??= ConnectRetryDelays;
        string? error = null;
        for (var attempt = 0; ; attempt++)
        {
            var (success, exception) = await provider.CheckConnectionAsync().ConfigureAwait(false);
            if (success) break;
            error = exception;
            if (attempt >= retryDelays.Count)
            {
                if (Runtime.TrySetState(context, PluginState.Failed, $"database connection failed: {error}"))
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
            if (Runtime.TrySetState(context, PluginState.Failed, ex.Message))
                _logger?.LogError("Database migrations failed, plugin is not ready: {error}", ex.Message);
            return false;
        }

        // Only moves Starting → DatabaseReady, and only in the lifetime that started this: never over Ready/Failed
        // or into a newer lifetime. DatabaseReady alone does not allow server-scoped writes (see EnsureDatabaseReady).
        Runtime.TryMarkDatabaseReady(context);
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
    /// <para>
    /// "Connected" and "ready for operations" are different: <see cref="PluginState.DatabaseReady"/> (connected and
    /// migrated, server row not resolved yet) is enough only for <see cref="OperationScope.Global"/> work. A
    /// <see cref="OperationScope.Server"/> operation (every ban/mute/warn write, history, stats…) needs
    /// <see cref="PluginState.Ready"/> and a server id, otherwise it would be saved with a NULL server_id and vanish
    /// from single-server queries once the server id is known.
    /// </para>
    /// </summary>
    internal static bool EnsureDatabaseReady(CommandInfo? command, OperationScope scope = OperationScope.Server)
    {
        if (scope == OperationScope.Global ? Runtime.IsDatabaseConnected : Runtime.IsOperationReady)
            return true;

        var message = Runtime.State switch
        {
            PluginState.Failed => $"[CS2-SimpleAdmin] Database unavailable: {Runtime.LastError ?? "unknown error"}",
            PluginState.DatabaseReady when scope == OperationScope.Server =>
                "[CS2-SimpleAdmin] Plugin is still starting (this server is not registered in the database yet), try again in a moment.",
            _ => "[CS2-SimpleAdmin] Plugin is still starting (database not ready yet), try again in a moment."
        };
        if (command != null)
            command.ReplyToCommand(message);
        else
            RateLimitedLog.Warning("not-ready", message);
        return false;
    }

    /// <summary>
    /// Queues a mandatory database write (ban, mute, warn, unban…). If the bounded queue is full or the plugin is not
    /// ready for the operation, nothing is applied and the caller is told explicitly; the penalty is never silently
    /// dropped. The server id and the caller's identity are captured <b>now</b>, on the game thread, and travel with
    /// the job: the job never reads the mutable global server id or a controller later.
    /// </summary>
    internal static bool TryQueuePenaltyWork(CCSPlayerController? caller, CommandInfo? command, string operation,
        Func<CancellationToken, Task> work, OperationScope scope = OperationScope.Server) =>
        TryQueuePenaltyWork(CallerRef.Capture(caller), command, operation, work, scope);

    internal static bool TryQueuePenaltyWork(CallerRef caller, CommandInfo? command, string operation,
        Func<CancellationToken, Task> work, OperationScope scope = OperationScope.Server)
    {
        if (!EnsureDatabaseReady(command, scope))
        {
            if (command == null)
                caller.Notify("[CS2-SimpleAdmin] Database not ready - the action was NOT saved. Try again in a moment.");
            return false;
        }

        // One read of the server id: the same value is what the readiness check required and what the job will use
        var serverId = GlobalServerId;
        if (scope == OperationScope.Server && serverId == null)
        {
            if (command != null) command.ReplyToCommand("[CS2-SimpleAdmin] Plugin is still starting (server id not resolved), try again in a moment.");
            else caller.Notify("[CS2-SimpleAdmin] Server not registered yet - the action was NOT saved. Try again in a moment.");
            return false;
        }

        if (Runtime.TryQueueDb(operation, work, new WorkContext(Runtime.Context, serverId, caller))) return true;

        const string message = "[CS2-SimpleAdmin] Database queue is full or unavailable - the action was NOT saved. Try again.";
        if (command != null)
            command.ReplyToCommand(message);
        else
            caller.Notify(message);
        _logger?.LogError("{Operation} was rejected: database queue full or unavailable", operation);
        return false;
    }

    /// <summary>
    /// Tells the issuing admin (or the console) that the queued write failed in the database. Must be called from
    /// inside the queued job: the caller identity is the snapshot taken on the game thread when the command was
    /// accepted (<see cref="WorkContext.Caller"/>); this method touches no controller and no native state on the
    /// worker. The message is delivered by a game-thread callback that re-checks that the same connection is still there.
    /// </summary>
    internal static Task ReportWriteFailureAsync(string what)
    {
        var caller = WorkContext.Current?.Caller ?? CallerRef.Console;
        return Runtime.OnGameThread(() =>
            caller.Notify($"[CS2-SimpleAdmin] {what} could NOT be saved to the database (see server log)."));
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

    private static readonly AdminReloadCoordinator AdminReloads = new(
        context => Instance.ReloadAdminsOnceAsync(context),
        ex => _logger?.LogError("Unable to reload admins (current permissions are kept): {exception}", ex.Message));

    /// <summary>
    /// Reloads SQL admins/groups. Concurrent requests coalesce (see <see cref="AdminReloadCoordinator"/>). The result
    /// says what happened: only <see cref="AdminReloadResult.Success"/> means the permissions were replaced; on
    /// <see cref="AdminReloadResult.Failed"/> the previous permissions stay in force.
    /// </summary>
    internal Task<AdminReloadResult> ReloadAdminsAsync() => ReloadAdminsAsync(WorkContext.Current?.Runtime ?? Runtime.Context);

    /// <summary>Reload for an explicit runtime context (startup code passes the context it belongs to).</summary>
    internal Task<AdminReloadResult> ReloadAdminsAsync(RuntimeContext context) => AdminReloads.RequestAsync(context);

    private async Task ReloadAdminsOnceAsync(RuntimeContext context)
    {
        var permissionManager = PermissionManager;
        var dataDirectory = Path.Combine(ModuleDirectory, "data");
        var job = context.TryQueueDb<PermissionManager.PreparedAdminReload>("admins-reload", async ct =>
        {
            // Everything is read first; the files are replaced only if all reads succeeded, so a failure at any
            // point leaves both the files and the applied permissions as they were.
            var prepared = await permissionManager.PrepareAdminReloadAsync().ConfigureAwait(false);
            await permissionManager.CommitAdminFilesAsync(prepared, dataDirectory, ct).ConfigureAwait(false);
            return prepared;
        });
        if (job == null)
        {
            if (!context.IsCurrent) throw new OperationCanceledException(context.Token);
            throw new InvalidOperationException("database queue full");
        }

        var prepared = await job.ConfigureAwait(false);
        var adminsPath = ModuleDirectory + "/data/admins.json";
        var groupsPath = ModuleDirectory + "/data/groups.json";

        // Three separate game-thread items: the dispatcher's time budget can split them over world updates.
        // Order kept from the original: strip old + load admins, load groups, load admins again (CSS merges group
        // flags/immunity into admins that already exist, and only into domains that exist).
        // NOTE: each item is one synchronous CounterStrikeSharp call (read + parse + apply of a whole JSON file) that
        // the dispatcher budget cannot interrupt; see docs/OPTIMIZATION_REPORT "Remaining limits".
        await context.PostAsync(() =>
        {
            if (!prepared.AdminsWritten) return;
            PermissionManager.ApplyAdminCache(prepared.Admins);
            AdminManager.LoadAdminData(adminsPath);
        }).ConfigureAwait(false);
        await context.PostAsync(() =>
        {
            if (prepared.GroupsWritten) AdminManager.LoadAdminGroups(groupsPath);
        }).ConfigureAwait(false);
        await context.PostAsync(() =>
        {
            if (prepared.AdminsWritten) AdminManager.LoadAdminData(adminsPath);
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
