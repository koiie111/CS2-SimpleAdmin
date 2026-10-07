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
        if (session.LoadQueued)
        {
            Interlocked.Increment(ref PluginMetrics.ConnectDeduplicated);
            return;
        }

        if (CS2_SimpleAdmin.DatabaseProvider == null || CS2_SimpleAdmin.Instance.CacheManager == null) return;

        // Before Ready the cache is not loaded; MarkReady → LoadPendingSessions picks this session up.
        if (Runtime.State != PluginState.Ready) return;

        QueueLoad(session);
    }

    /// <summary>Game thread: queues loads for sessions that connected while the plugin was starting.</summary>
    public void LoadPendingSessions()
    {
        var sessions = new List<PlayerSession>();
        Runtime.Sessions.Snapshot(sessions);
        foreach (var session in sessions)
            if (!session.LoadQueued)
                QueueLoad(session);
    }

    private void QueueLoad(PlayerSession session)
    {
        var plugin = CS2_SimpleAdmin.Instance;
        var cache = plugin.CacheManager;
        if (cache == null) return;
        var config = plugin.Config; // one config snapshot for the whole operation
        session.LoadQueued = true;
        if (!Runtime.TryQueueDb("connect-load", ct => LoadAsync(session, config, cache, ct)))
        {
            // Queue full: retried by the next periodic pass (LoadPendingSessions), never silently forgotten
            session.LoadQueued = false;
        }
    }

    private static async Task LoadAsync(PlayerSession session, CS2_SimpleAdminConfig config, CacheManager cache, CancellationToken ct)
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
                var player = ResolveController(session);
                if (player == null)
                {
                    Interlocked.Increment(ref PluginMetrics.StaleSessionResults);
                    return;
                }

                Helper.KickPlayer(player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED);
            }).ConfigureAwait(false);
            PluginMetrics.ConnectLoad.RecordSince(start);
            return;
        }

        var accounts = new List<(ulong SteamId, string PlayerName)>();
        if (other.CheckMultiAccountsByIp && session.IpAddress != null)
            foreach (var account in cache.GetAccountsByIp(session.IpAddress))
                accounts.Add((account.SteamId, account.PlayerName));

        var stats = await plugin.MuteManager.GetPlayerPenaltyStatsAsync(session.SteamId, config.MultiServerMode, serverId, ct)
            .ConfigureAwait(false);
        var mutes = await plugin.MuteManager.GetActiveMutesAsync(session.SteamId, config.MultiServerMode, other.TimeMode,
            serverId, now, ct).ConfigureAwait(false);

        var result = new LoadResult(false, stats, mutes, accounts);
        await Runtime.OnGameThread(() => ApplyLoadResult(session, result, config)).ConfigureAwait(false);
        PluginMetrics.ConnectLoad.RecordSince(start);
    }

    /// <summary>Game thread. Applies a connect load if the connection is still current.</summary>
    internal static void ApplyLoadResult(PlayerSession session, LoadResult result, CS2_SimpleAdminConfig config)
    {
        var player = ResolveController(session);
        if (player == null)
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

        if (voiceMuted)
            player.VoiceFlags = VoiceFlags.Muted;

        if (config.OtherSettings.NotifyPenaltiesToAdminOnConnect)
            NotifyAdmins(player, info);
    }

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
