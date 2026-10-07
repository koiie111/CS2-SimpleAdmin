using System.Diagnostics;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Models;
using CS2_SimpleAdminApi;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin.Managers;

/// <summary>
/// Connect-time loading and periodic player checks.
/// <para>
/// Threading: everything that touches controllers, PlayersInfo, penalties or the engine runs on the game thread.
/// Background work (DB queue) receives a <see cref="PlayerSession"/> + config snapshot and returns an immutable
/// result; <see cref="ApplyLoadResult"/> runs through the dispatcher and first checks that the session is still the
/// current connection in that slot.
/// </para>
/// </summary>
internal class PlayerManager
{
    /// <summary>Max SteamIDs listed per admin in the "associated accounts" notice (5 per chat line).</summary>
    internal const int MaxAssociatedAccountsShown = 25;

    internal sealed record LoadResult(
        bool Banned,
        PlayerPenaltyStats Stats,
        List<ActiveMuteRow> ActiveMutes,
        List<(ulong SteamId, string PlayerName)> AccountsAssociated);

    /// <summary>
    /// Loads and initializes player data when a client connects (game thread).
    /// OnClientConnected and player_connect_full of the same connection share one session and one load.
    /// </summary>
    /// <param name="player">The connecting player.</param>
    /// <param name="fullConnect">Kept for API compatibility; both connect events lead to the same single load.</param>
    public void LoadPlayerData(CCSPlayerController player, bool fullConnect = false)
    {
        if (!player.UserId.HasValue)
        {
            Helper.KickPlayer(player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_INVALIDCONNECTION);
            return;
        }

        var steamId = player.SteamID;
        var playerName = !string.IsNullOrEmpty(player.PlayerName)
            ? player.PlayerName
            : CS2_SimpleAdmin._localizer?["sa_unknown"] ?? "Unknown";
        var ipAddress = player.IpAddress?.Split(":")[0];

        if (CS2_SimpleAdmin.RenamedPlayers.TryGetValue(steamId, out var renamedTo))
        {
            player.Rename(renamedTo);
        }

        var session = Runtime.Sessions.BeginOrGet(player.Slot, steamId, player.UserId.Value, playerName, ipAddress, out _);
        // connect + player_connect_full of one connection share one load; a load that failed is retried by its own
        // backoff timer, not by the next connect event
        if (session.LoadState != ConnectLoadState.Pending)
        {
            Interlocked.Increment(ref PluginMetrics.ConnectDeduplicated);
            return;
        }

        if (CS2_SimpleAdmin.DatabaseProvider == null || CS2_SimpleAdmin.Instance.CacheManager == null) return;

        // Before Ready the cache is not loaded; MarkReady → LoadPendingSessions picks this session up.
        if (Runtime.State != PluginState.Ready) return;

        QueueLoad(session, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Schedules <paramref name="callback"/> on the game thread after <paramref name="delay"/>. A seam: tests replace it
    /// with a manual clock; production uses a CounterStrikeSharp timer.
    /// </summary>
    internal static Action<TimeSpan, Action> RetryScheduler { get; set; } =
        (delay, callback) => CS2_SimpleAdmin.Instance.AddTimer((float)delay.TotalSeconds, callback);

    /// <summary>
    /// Game thread: starts loads for sessions that are waiting for one: connected while the plugin was starting
    /// (<see cref="ConnectLoadState.Pending"/>) or whose retry is due (<see cref="ConnectLoadState.RetryWait"/>).
    /// Also the safety net behind the per-session retry timers.
    /// </summary>
    public void LoadPendingSessions()
    {
        var sessions = new List<PlayerSession>();
        Runtime.Sessions.Snapshot(sessions);
        var now = Stopwatch.GetTimestamp();
        foreach (var session in sessions)
            if (session.LoadState is ConnectLoadState.Pending or ConnectLoadState.RetryWait)
                QueueLoad(session, now);
    }

    /// <summary>
    /// Game thread. Starts the next attempt if the session is current and one is due (<paramref name="now"/> is a
    /// Stopwatch timestamp; the retry timer passes <see cref="long.MaxValue"/>: its delay has elapsed by definition).
    /// A queue that refuses the work does not consume an attempt and is retried shortly.
    /// </summary>
    internal void QueueLoad(PlayerSession session, long now)
    {
        var plugin = CS2_SimpleAdmin.Instance;
        var cache = plugin.CacheManager;
        if (cache == null || !Runtime.Sessions.IsCurrent(session)) return; // never revive a disconnected session
        var attempt = session.TryBeginLoad(now);
        if (attempt == 0) return;

        var config = plugin.Config; // one config snapshot for the whole operation
        if (Runtime.TryQueueDb<bool>("connect-load", ct => LoadAsync(session, attempt, config, cache, ct)) != null) return;

        // Queue full or stopped: nothing was tried, so wait a moment and try again (never silently forgotten)
        if (session.ReleaseLoad(attempt, Stopwatch.GetTimestamp(), LoadRetryPolicy.QueueFullDelay))
            ScheduleRetry(session, LoadRetryPolicy.QueueFullDelay);
    }

    private void ScheduleRetry(PlayerSession session, TimeSpan delay)
    {
        var context = Runtime.Context;
        RetryScheduler(delay, () =>
        {
            // The timer belongs to the lifetime that scheduled it; after a restart the new lifetime's own
            // LoadPendingSessions covers every session
            if (context.IsCurrent) QueueLoad(session, long.MaxValue);
        });
    }

    /// <summary>Game thread. A load attempt failed: back off and retry, unless the connection is gone or already moved on.</summary>
    private void OnLoadFailed(PlayerSession session, int attempt, string reason)
    {
        if (!Runtime.Sessions.IsCurrent(session))
        {
            Interlocked.Increment(ref PluginMetrics.StaleSessionResults);
            return;
        }

        if (!session.FailLoad(attempt, Stopwatch.GetTimestamp(), out var delay)) return;
        Interlocked.Increment(ref PluginMetrics.ConnectLoadRetries);
        RateLimitedLog.Warning("connect.load-failed",
            $"Connect load of {session} failed (attempt {attempt}), retrying in {delay.TotalSeconds:F0}s: {reason}");
        ScheduleRetry(session, delay);
    }

    private async Task<bool> LoadAsync(PlayerSession session, int attempt, CS2_SimpleAdminConfig config, CacheManager cache,
        CancellationToken ct)
    {
        try
        {
            await LoadCoreAsync(session, attempt, config, cache, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw; // unload / restart: nothing to retry in a dead lifetime
        }
        catch (Exception ex)
        {
            // The database accepted the work but it failed: return to the game thread, where the session state lives.
            // The transition is bound to this attempt and session, so a late failure cannot touch a newer attempt,
            // a replaced slot or a new lifetime (OnGameThread is bound to the lifetime the job was accepted in).
            await Runtime.OnGameThread(() => OnLoadFailed(session, attempt, ex.Message)).ConfigureAwait(false);
            return false;
        }
    }

    private async Task LoadCoreAsync(PlayerSession session, int attempt, CS2_SimpleAdminConfig config, CacheManager cache,
        CancellationToken ct)
    {
        var start = LatencyHistogram.Now();
        var plugin = CS2_SimpleAdmin.Instance;
        var other = config.OtherSettings;
        var serverId = CS2_SimpleAdmin.ServerId;

        // Save ip address before ban check
        if (other.CheckMultiAccountsByIp && session.IpAddress != null)
            await SavePlayerIpAddress(session.SteamId, session.Name, session.IpAddress, ct).ConfigureAwait(false);

        var now = Time.ActualDateTime();
        var check = cache.CheckBan(config, session.SteamId, session.IpAddress, now);
        if (check.IsBanned)
        {
            CS2_SimpleAdmin._logger?.LogInformation("[BAN CHECK] Player {Name} ({SteamId}) IP: {Ip} is banned (ban #{BanId}, {Match}) - kicking",
                session.Name, session.SteamId, session.IpAddress, check.Ban?.Id, check.Match);
            QueuePlayerDataUpdate(config, serverId, session, check, cache);
            await Runtime.OnGameThread(() =>
            {
                if (!ControllerAvailable(session))
                {
                    if (Runtime.Sessions.IsCurrent(session))
                    {
                        // Same connection whose controller is not resolvable yet: retry (as the normal path does) so the
                        // kick is not lost; a disconnected session is never revived
                        OnLoadFailed(session, attempt, "controller not available when the ban result arrived");
                        return;
                    }

                    Interlocked.Increment(ref PluginMetrics.StaleSessionResults);
                    return;
                }

                // Finish this attempt first: only the attempt that completes it kicks (exactly one kick per connection)
                if (!session.CompleteLoad(attempt))
                {
                    Interlocked.Increment(ref PluginMetrics.StaleSessionResults);
                    return;
                }

                KickBanned(session);
            }).ConfigureAwait(false);
            PluginMetrics.ConnectLoad.RecordSince(start);
            return;
        }

        var accounts = new List<(ulong SteamId, string PlayerName)>();
        if (other.CheckMultiAccountsByIp && session.IpAddress != null)
            foreach (var account in cache.GetAccountsByIp(session.IpAddress, now, other.ExpireOldIpBans))
                accounts.Add((account.SteamId, account.PlayerName));

        var stats = await plugin.MuteManager.GetPlayerPenaltyStatsAsync(session.SteamId, config.MultiServerMode, serverId, ct)
            .ConfigureAwait(false);
        var mutes = await plugin.MuteManager.GetActiveMutesAsync(session.SteamId, config.MultiServerMode, other.TimeMode,
            serverId, now, ct).ConfigureAwait(false);

        var result = new LoadResult(false, stats, mutes, accounts);
        await Runtime.OnGameThread(() => ApplyLoadResult(session, attempt, result, config)).ConfigureAwait(false);
        PluginMetrics.ConnectLoad.RecordSince(start);
    }

    /// <summary>Game thread. Applies a connect load if the connection is still current.</summary>
    internal static void ApplyLoadResult(PlayerSession session, int attempt, LoadResult result, CS2_SimpleAdminConfig config)
    {
        if (!ControllerAvailable(session))
        {
            if (Runtime.Sessions.IsCurrent(session))
            {
                // Same connection, but its controller is not resolvable yet: try again instead of losing its penalties
                CS2_SimpleAdmin.PlayerManager.OnLoadFailed(session, attempt, "controller not available when the result arrived");
                return;
            }

            Interlocked.Increment(ref PluginMetrics.StaleSessionResults);
            return;
        }

        // Marked loaded before the first side effect: a failure while applying is a bug, not a database problem, and
        // a retry would apply the same penalties twice
        if (!session.CompleteLoad(attempt))
        {
            Interlocked.Increment(ref PluginMetrics.StaleSessionResults);
            return;
        }

        var info = new PlayerInfo(session.UserId, session.Slot, new SteamID(session.SteamId), session.Name, session.IpAddress,
            (int)result.Stats.TotalBans, (int)result.Stats.TotalMutes, (int)result.Stats.TotalGags,
            (int)result.Stats.TotalSilences, (int)result.Stats.TotalWarns)
        {
            AccountsAssociated = result.AccountsAssociated,
            IsLoaded = true
        };
        CS2_SimpleAdmin.PlayersInfo[session.SteamId] = info;

        var voiceMuted = false;
        foreach (var mute in result.ActiveMutes)
        {
            var ends = mute.Ends ?? DateTime.MinValue;
            switch (mute.Type)
            {
                case "GAG":
                    PlayerPenaltyManager.AddPenalty(session.Slot, PenaltyType.Gag, ends, mute.Duration);
                    break;
                case "MUTE":
                    PlayerPenaltyManager.AddPenalty(session.Slot, PenaltyType.Mute, ends, mute.Duration);
                    voiceMuted = true;
                    break;
                default:
                    PlayerPenaltyManager.AddPenalty(session.Slot, PenaltyType.Silence, ends, mute.Duration);
                    voiceMuted = true;
                    break;
            }
        }

        NativeEffects(session, info, voiceMuted, config.OtherSettings.NotifyPenaltiesToAdminOnConnect);
    }

    // ---- the only places where a load result touches the engine; seams so the state logic is testable without a server ----

    /// <summary>Game thread: is the controller of this session resolvable right now?</summary>
    internal static Func<PlayerSession, bool> ControllerAvailable { get; set; } = static session => ResolveController(session) != null;

    /// <summary>Game thread: voice flags and the admin notice for a freshly loaded player.</summary>
    internal static Action<PlayerSession, PlayerInfo, bool, bool> NativeEffects { get; set; } =
        static (session, info, voiceMuted, notifyAdmins) =>
        {
            var player = ResolveController(session);
            if (player == null) return;
            if (voiceMuted) player.VoiceFlags = VoiceFlags.Muted;
            if (notifyAdmins) NotifyAdmins(player, info);
        };

    /// <summary>Game thread: kick a connection that the ban cache rejected.</summary>
    internal static Action<PlayerSession> KickBanned { get; set; } = static session =>
    {
        if (ResolveController(session) is { } player)
            Helper.KickPlayer(player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED);
    };

    private static void NotifyAdmins(CCSPlayerController player, PlayerInfo info)
    {
        if (CS2_SimpleAdmin._localizer == null) return;
        foreach (var admin in Helper.GetValidPlayers())
        {
            if (admin == player || CS2_SimpleAdmin.AdminDisabledJoinComms.Contains(admin.SteamID)) continue;
            var adminId = new SteamID(admin.SteamID);
            if (!AdminManager.PlayerHasPermissions(adminId, "@css/kick") && !AdminManager.PlayerHasPermissions(adminId, "@css/ban"))
                continue;

            admin.SendLocalizedMessage(CS2_SimpleAdmin._localizer, "sa_admin_penalty_info", player.PlayerName,
                info.TotalBans, info.TotalGags, info.TotalMutes, info.TotalSilences, info.TotalWarns);

            if (info.AccountsAssociated.Count < 2) continue;
            // Bounded output: a shared IP (LAN/mirror) can carry hundreds of accounts
            var shown = info.AccountsAssociated.Count > MaxAssociatedAccountsShown
                ? info.AccountsAssociated.GetRange(0, MaxAssociatedAccountsShown)
                : info.AccountsAssociated;
            foreach (var chunk in shown.ChunkBy(5))
            {
                admin.SendLocalizedMessage(CS2_SimpleAdmin._localizer, "sa_admin_associated_accounts", player.PlayerName,
                    string.Join(", ", chunk.Select(a => $"{a.PlayerName} ({a.SteamId})")));
            }

            if (info.AccountsAssociated.Count > MaxAssociatedAccountsShown)
                admin.PrintToChat($" (+{info.AccountsAssociated.Count - MaxAssociatedAccountsShown} more accounts, see css_history / site)");
        }
    }

    /// <summary>
    /// Game thread: the controller of a session, or null if the slot now holds another connection,
    /// the player left, or the map changed.
    /// </summary>
    internal static CCSPlayerController? ResolveController(PlayerSession session)
    {
        if (!Runtime.Sessions.IsCurrent(session)) return null;
        var player = Utilities.GetPlayerFromSlot(session.Slot);
        if (player == null || !player.IsValid || player.SteamID != session.SteamId || player.UserId != session.UserId)
            return null;
        return player;
    }

    private static void QueuePlayerDataUpdate(CS2_SimpleAdminConfig config, int? serverId, PlayerSession session,
        BanCheckResult check, CacheManager cache)
    {
        var multi = config.OtherSettings.BanType != 0 && config.OtherSettings.CheckMultiAccountsByIp;
        if (!CacheManager.NeedsPlayerDataUpdate(check, multi, session.IpAddress)) return;
        // Best effort back-fill; dropping it under load only delays filling the ban row's name/IP
        Runtime.TryQueueDb("ban-backfill", ct =>
            cache.UpdatePlayerDataAsync(config, serverId, session.Name, session.SteamId, session.IpAddress, ct));
    }

    /// <summary>
    /// Returns every ban, gag/mute/silence and warn ever recorded for a SteamID, newest first (unbounded; kept for
    /// existing callers). Prefer <see cref="GetPenaltyHistoryPage"/>.
    /// </summary>
    public async Task<List<dynamic>> GetPenaltyHistory(ulong steamId, string? type = null)
    {
        if (CS2_SimpleAdmin.DatabaseProvider == null) return [];

        try
        {
            await using var connection = await CS2_SimpleAdmin.DatabaseProvider.CreateConnectionAsync();
            var sql = CS2_SimpleAdmin.DatabaseProvider.GetPenaltyHistoryQuery(CS2_SimpleAdmin.Instance.Config.MultiServerMode);
            var rows = (await connection.QueryAsync(sql, new { PlayerSteamID = steamId, serverid = CS2_SimpleAdmin.ServerId })).ToList();

            if (type != null)
                rows.RemoveAll(r => !type.Equals((string)r.type + "s", StringComparison.OrdinalIgnoreCase));

            return rows;
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogError("Unable to load penalty history: {exception}", ex.Message);
            return [];
        }
    }

    internal sealed record HistoryPage(int Total, int Page, int PageSize, List<PenaltyHistoryRow> Rows)
    {
        public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
    }

    /// <summary>
    /// One page of a player's history, filtered and paged in SQL with a stable order (created DESC, type, id DESC).
    /// Runs on a DB worker. <paramref name="page"/> is 1-based.
    /// </summary>
    internal static async Task<HistoryPage> GetPenaltyHistoryPage(ulong steamId, string? type, int page, int pageSize,
        bool multiServer, int? serverId, CancellationToken ct)
    {
        var provider = CS2_SimpleAdmin.DatabaseProvider ?? throw new InvalidOperationException("no database");
        var muteType = Database.SharedQueries.Parts(type).MuteType;
        await using var connection = await provider.CreateConnectionAsync(ct).ConfigureAwait(false);
        var total = Convert.ToInt32(await connection.ExecuteScalarAsync<object>(new CommandDefinition(
            provider.GetPenaltyHistoryCountQuery(multiServer, type),
            new { PlayerSteamID = steamId, serverid = serverId, muteType }, cancellationToken: ct)).ConfigureAwait(false));
        page = Math.Max(1, page);
        var rows = (await connection.QueryAsync<PenaltyHistoryRow>(new CommandDefinition(
            provider.GetPenaltyHistoryPageQuery(multiServer, type),
            new { PlayerSteamID = steamId, serverid = serverId, muteType, limit = pageSize, offset = (page - 1) * pageSize },
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
        return new HistoryPage(total, page, pageSize, rows);
    }

    /// <summary>
    /// Dapper gives DateTime on MySQL but can give a string on SQLite; accept both.
    /// </summary>
    internal static DateTime? ToDateTime(object? value) =>
        value switch
        {
            DateTime d => d,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) => parsed,
            _ => null
        };

    /// <summary>
    /// Loads permanent renames from the database (DB worker). The caller applies them on the game thread.
    /// </summary>
    public async Task<List<(ulong SteamId, string Name)>> LoadRenamedPlayersAsync(CancellationToken ct)
    {
        if (CS2_SimpleAdmin.DatabaseProvider == null) return [];
        await using var connection = await CS2_SimpleAdmin.DatabaseProvider.CreateConnectionAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(long steamId, string name)>(new CommandDefinition(
            CS2_SimpleAdmin.DatabaseProvider.GetRenamesQuery(), cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => ((ulong)r.steamId, r.name)).ToList();
    }

    /// <summary>
    /// Persists or removes a permanent rename (css_prename) for a player.
    /// </summary>
    /// <param name="steamId">SteamID64 of the player.</param>
    /// <param name="name">Forced name, or null/empty to remove the rename.</param>
    public async Task SaveRenamedPlayer(ulong steamId, string? name)
    {
        if (CS2_SimpleAdmin.DatabaseProvider == null) return;

        await using var connection = await CS2_SimpleAdmin.DatabaseProvider.CreateConnectionAsync();
        if (string.IsNullOrEmpty(name))
            await connection.ExecuteAsync(CS2_SimpleAdmin.DatabaseProvider.GetDeleteRenameQuery(), new { steamId });
        else
            await connection.ExecuteAsync(CS2_SimpleAdmin.DatabaseProvider.GetUpsertRenameQuery(), new { steamId, name });
    }

    /// <summary>
    /// Saves player's IP address to the database for multi-account detection.
    /// This is called before ban checks to ensure IP is recorded even if player is banned.
    /// </summary>
    private static async Task SavePlayerIpAddress(ulong steamId, string playerName, string ipAddress, CancellationToken ct)
    {
        if (CS2_SimpleAdmin.DatabaseProvider == null) return;

        try
        {
            await using var connection = await CS2_SimpleAdmin.DatabaseProvider.CreateConnectionAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(CS2_SimpleAdmin.DatabaseProvider.GetUpsertPlayerIpQuery(), new
            {
                SteamID = steamId,
                playerName,
                IPAddress = IpHelper.IpToUint(ipAddress)
            }, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RateLimitedLog.Error("connect.save-ip", ex, $"Unable to save ip address for {playerName}");
        }
    }

    /// <summary>
    /// Game thread: re-applies permanent renames to online players. O(online players), one dictionary lookup each.
    /// </summary>
    internal static void EnforceRenamesOnline()
    {
        if (CS2_SimpleAdmin.RenamedPlayers.Count == 0) return;
        var start = LatencyHistogram.Now();
        foreach (var player in CS2_SimpleAdmin.CachedPlayers)
        {
            if (!player.IsValid || player.Connected != PlayerConnectedState.Connected) continue;
            if (!CS2_SimpleAdmin.RenamedPlayers.TryGetValue(player.SteamID, out var name)) continue;
            if (player.PlayerName.Equals(name)) continue;
            player.Rename(name);
        }

        PluginMetrics.RenameTimer.RecordSince(start);
    }

    /// <summary>
    /// Registers the repeating timers: renames (5 s) and the maintenance pass (61 s).
    /// </summary>
    public void CheckPlayersTimer()
    {
        CS2_SimpleAdmin.Instance.AddTimer(5f, EnforceRenamesOnline, TimerFlags.REPEAT);
        CS2_SimpleAdmin.Instance.PlayersTimer = CS2_SimpleAdmin.Instance.AddTimer(61.0f, PeriodicMaintenance.OnTimer, TimerFlags.REPEAT);
    }
}
