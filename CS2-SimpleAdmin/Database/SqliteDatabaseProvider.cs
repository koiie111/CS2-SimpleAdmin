using System.Data.Common;
using System.Data.SQLite;

namespace CS2_SimpleAdmin.Database;

public class SqliteDatabaseProvider(string filePath) : IDatabaseProvider
{
    // Default Timeout (seconds) is also System.Data.SQLite's busy-wait budget for a locked database file.
    // All plugin access is serialised by the single-worker DB queue, so contention only comes from outside
    // processes (backups, manual edits); WAL is not enabled because the file may live on shared/network storage.
    private readonly string _connectionString = $"Data Source={filePath};Default Timeout=15";

    /// <summary>
    /// System.Data.SQLite 1.0.119 does not override the ADO.NET async methods, so OpenAsync/ExecuteReaderAsync run
    /// synchronously on the calling thread. Callers must therefore only use this provider from the DB queue workers,
    /// never from a game-thread callback.
    /// </summary>
    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        var conn = new SQLiteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async Task<(bool Success, string? Exception)> CheckConnectionAsync()
    {
        try
        {
            await using var conn = await CreateConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public Task DatabaseMigrationAsync()
    {
        var migration = new Migration(CS2_SimpleAdmin.Instance.ModuleDirectory + "/Database/Migrations/Sqlite");
        return migration.ExecuteMigrationsAsync();
    }

    public string GetIpHistoryQuery() =>
        "SELECT steamid, name, address, used_at FROM sa_players_ips ORDER BY used_at DESC";

    public string GetUpsertPlayerIpQuery()
    {
        return """
            INSERT INTO sa_players_ips (steamid, name, address, used_at)
            VALUES (@SteamID, @playerName, @IPAddress, CURRENT_TIMESTAMP)
            ON CONFLICT(steamid, address) DO UPDATE SET
                used_at = CURRENT_TIMESTAMP,
                name = @playerName;
            """;
    }

    public string GetAddBanQuery() =>
        """
        INSERT INTO sa_bans
            (player_steamid, player_name, player_ip, admin_steamid, admin_name, reason, duration, ends, created, server_id)
        VALUES
            (@playerSteamid, @playerName, @playerIp, @adminSteamid, @adminName, @banReason, @duration, @ends, @created, @serverid);
        SELECT last_insert_rowid();
        """;

    public string GetAddBanBySteamIdQuery() =>
        """
        INSERT INTO sa_bans
            (player_steamid, admin_steamid, admin_name, reason, duration, ends, created, server_id)
        VALUES
            (@playerSteamid, @adminSteamid, @adminName, @banReason, @duration, @ends, @created, @serverid);
        SELECT last_insert_rowid();
        """;

    public string GetAddBanByIpQuery() =>
        """
        INSERT INTO sa_bans
            (player_ip, admin_steamid, admin_name, reason, duration, ends, created, server_id)
        VALUES
            (@playerIp, @adminSteamid, @adminName, @banReason, @duration, @ends, @created, @serverid);
        SELECT last_insert_rowid();
        """;

    public string GetUnbanRetrieveBansQuery() =>
        "SELECT id, player_steamid FROM sa_bans WHERE (player_steamid = @pattern OR player_name = @pattern OR player_ip = @pattern) AND status = 'ACTIVE'";

    public string GetUnbanAdminIdQuery() =>
        "SELECT id FROM sa_admins WHERE player_steamid = @adminSteamId";

    public string GetInsertUnbanQuery(bool includeReason) =>
        includeReason
            ? "INSERT INTO sa_unbans (ban_id, admin_id, reason) VALUES (@banId, @adminId, @reason); SELECT last_insert_rowid();"
            : "INSERT INTO sa_unbans (ban_id, admin_id) VALUES (@banId, @adminId); SELECT last_insert_rowid();";

    // SQLite has no ON UPDATE CURRENT_TIMESTAMP, so updated_at must be set explicitly or the cache refresh never sees the change
    public string GetUpdateBanStatusQuery() =>
        "UPDATE sa_bans SET status = 'UNBANNED', unban_id = @unbanId, updated_at = CURRENT_TIMESTAMP WHERE id = @banId";

    public string GetExpireBansQuery() =>
        "UPDATE sa_bans SET status = 'EXPIRED', updated_at = CURRENT_TIMESTAMP WHERE status = 'ACTIVE' AND duration > 0 AND ends <= @currentTime";

    public string GetExpireIpBansQuery() =>
        "UPDATE sa_bans SET player_ip = NULL WHERE status = 'ACTIVE' AND ends <= @ipBansTime";

    public string GetExpireOldPlayerIpsQuery() =>
        "DELETE FROM sa_players_ips WHERE used_at <= @ipBansTime";

    public string GetRenamesQuery() =>
        "SELECT player_steamid, name FROM sa_renames";

    public string GetUpsertRenameQuery() =>
        "INSERT INTO sa_renames (player_steamid, name) VALUES (@steamId, @name) ON CONFLICT(player_steamid) DO UPDATE SET name = excluded.name";

    public string GetDeleteRenameQuery() =>
        "DELETE FROM sa_renames WHERE player_steamid = @steamId";

    public string GetAdminsQuery() =>
        """
        SELECT sa_admins.player_steamid, sa_admins.player_name, sa_admins_flags.flag, sa_admins.immunity, sa_admins.ends
        FROM sa_admins_flags
        JOIN sa_admins ON sa_admins_flags.admin_id = sa_admins.id
        WHERE (sa_admins.ends IS NULL OR sa_admins.ends > @CurrentTime)
          AND (sa_admins.server_id IS NULL OR sa_admins.server_id = @serverid)
        ORDER BY sa_admins.player_steamid
        """;

    public string GetDeleteAdminQuery(bool globalDelete) =>
        globalDelete
            ? "DELETE FROM sa_admins WHERE player_steamid = @PlayerSteamID"
            : "DELETE FROM sa_admins WHERE player_steamid = @PlayerSteamID AND server_id = @ServerId";

    public string GetAddAdminQuery() =>
        """
        INSERT INTO sa_admins (player_steamid, player_name, immunity, ends, created, server_id)
        VALUES (@playerSteamId, @playerName, @immunity, @ends, @created, @serverid);
        SELECT last_insert_rowid();
        """;

    public string GetGroupsQuery() =>
        """
        SELECT g.group_id, sg.name AS group_name, sg.immunity, f.flag
        FROM sa_groups_flags f
        JOIN sa_groups_servers g ON f.group_id = g.group_id
        JOIN sa_groups sg ON sg.id = g.group_id
        WHERE (g.server_id = @serverid OR server_id IS NULL)
        """;

    public string GetAddAdminFlagsQuery() =>
        "INSERT INTO sa_admins_flags (admin_id, flag) VALUES (@adminId, @flag);";

    public string GetUpdateAdminGroupQuery() =>
        "UPDATE sa_admins SET group_id = @groupId WHERE id = @adminId;";

    public string GetAddGroupQuery() =>
        """
        INSERT INTO sa_groups (name, immunity) VALUES (@groupName, @immunity);
        SELECT last_insert_rowid();
        """;

    public string GetGroupIdByNameQuery() =>
        """
        SELECT sgs.group_id
        FROM sa_groups_servers sgs
        JOIN sa_groups sg ON sgs.group_id = sg.id
        WHERE sg.name = @groupName
        ORDER BY (sgs.server_id = @serverId) DESC, sgs.server_id ASC
        LIMIT 1;
        """;

    public string GetAddGroupFlagsQuery() =>
        "INSERT INTO sa_groups_flags (group_id, flag) VALUES (@groupId, @flag);";

    public string GetAddGroupServerQuery() =>
        "INSERT INTO sa_groups_servers (group_id, server_id) VALUES (@groupId, @server_id);";

    public string GetDeleteGroupQuery() =>
        "DELETE FROM sa_groups WHERE name = @groupName;";

    public string GetDeleteOldAdminsQuery() =>
        "DELETE FROM sa_admins WHERE ends IS NOT NULL AND ends <= @CurrentTime;";
    
    public string GetAddMuteQuery(bool includePlayerName) =>
        includePlayerName
            ? """
              INSERT INTO sa_mutes
              (player_steamid, player_name, admin_steamid, admin_name, reason, duration, ends, created, type, server_id)
              VALUES (@playerSteamid, @playerName, @adminSteamid, @adminName, @muteReason, @duration, @ends, @created, @type, @serverid);
              SELECT last_insert_rowid();
              """
            : """
              INSERT INTO sa_mutes
              (player_steamid, admin_steamid, admin_name, reason, duration, ends, created, type, server_id)
              VALUES (@playerSteamid, @adminSteamid, @adminName, @muteReason, @duration, @ends, @created, @type, @serverid);
              SELECT last_insert_rowid();
              """;

    public string GetIsMutedQuery(int timeMode) =>
        timeMode == 1
            ? "SELECT * FROM sa_mutes WHERE player_steamid = @PlayerSteamID AND status = 'ACTIVE' AND (duration = 0 OR ends > @CurrentTime)"
            : "SELECT * FROM sa_mutes WHERE player_steamid = @PlayerSteamID AND status = 'ACTIVE' AND (duration = 0 OR duration > COALESCE(passed, 0))";

    public string GetActiveMutesBatchQuery(int timeMode) => SharedQueries.ActiveMutesBatch(timeMode);

    public string GetActiveSteamBansQuery() => SharedQueries.ActiveSteamBans;

    public string GetActiveBansByIdsQuery() => SharedQueries.ActiveBansByIds;

    public string GetMuteStatsQuery() =>
        """
        SELECT
            COUNT(CASE WHEN type = 'MUTE' THEN 1 END) AS TotalMutes,
            COUNT(CASE WHEN type = 'GAG' THEN 1 END) AS TotalGags,
            COUNT(CASE WHEN type = 'SILENCE' THEN 1 END) AS TotalSilences
        FROM sa_mutes
        WHERE player_steamid = @PlayerSteamID;
        """;

    public string GetRetrieveMutesQuery() =>
        "SELECT id FROM sa_mutes WHERE (player_steamid = @pattern OR player_name = @pattern) AND type = @muteType AND status = 'ACTIVE'";

    public string GetUnmuteAdminIdQuery() =>
        "SELECT id FROM sa_admins WHERE player_steamid = @adminSteamId";

    public string GetInsertUnmuteQuery(bool includeReason) =>
        includeReason
            ? "INSERT INTO sa_unmutes (mute_id, admin_id, reason) VALUES (@muteId, @adminId, @reason); SELECT last_insert_rowid();"
            : "INSERT INTO sa_unmutes (mute_id, admin_id) VALUES (@muteId, @adminId); SELECT last_insert_rowid();";

    public string GetUpdateMuteStatusQuery() =>
        "UPDATE sa_mutes SET status = 'UNMUTED', unmute_id = @unmuteId WHERE id = @muteId";

    public string GetExpireMutesQuery(int timeMode) =>
        timeMode == 1
            ? "UPDATE sa_mutes SET status = 'EXPIRED' WHERE status = 'ACTIVE' AND duration > 0 AND ends <= @CurrentTime"
            : "UPDATE sa_mutes SET status = 'EXPIRED' WHERE status = 'ACTIVE' AND duration > 0 AND passed >= duration";

    public string GetAddWarnQuery(bool includePlayerName) =>
        includePlayerName
            ? """
              INSERT INTO sa_warns
              (player_steamid, player_name, admin_steamid, admin_name, reason, duration, ends, created, server_id)
              VALUES
              (@playerSteamid, @playerName, @adminSteamid, @adminName, @warnReason, @duration, @ends, @created, @serverid);
              SELECT last_insert_rowid();
              """
            : """
              INSERT INTO sa_warns
              (player_steamid, admin_steamid, admin_name, reason, duration, ends, created, server_id)
              VALUES
              (@playerSteamid, @adminSteamid, @adminName, @warnReason, @duration, @ends, @created, @serverid);
              SELECT last_insert_rowid();
              """;

    public string GetPlayerWarnsQuery(bool active) =>
        active
            ? "SELECT * FROM sa_warns WHERE player_steamid = @PlayerSteamID AND status = 'ACTIVE' ORDER BY id DESC"
            : "SELECT * FROM sa_warns WHERE player_steamid = @PlayerSteamID ORDER BY id DESC";

    public string GetPlayerWarnsCountQuery(bool active) =>
        active
            ? "SELECT COUNT(*) FROM sa_warns WHERE player_steamid = @PlayerSteamID AND status = 'ACTIVE'"
            : "SELECT COUNT(*) FROM sa_warns WHERE player_steamid = @PlayerSteamID";

    public string GetUnwarnByIdQuery() =>
        "UPDATE sa_warns SET status = 'EXPIRED' WHERE status = 'ACTIVE' AND player_steamid = @steamid AND id = @warnId";

    public string GetUnwarnLastQuery() =>
        """
        UPDATE sa_warns
        SET status = 'EXPIRED'
        WHERE status = 'ACTIVE'
        AND player_steamid = @steamid
        ORDER BY id DESC
        LIMIT 1
        """;

    public string GetExpireWarnsQuery() =>
        "UPDATE sa_warns SET status = 'EXPIRED' WHERE status = 'ACTIVE' AND duration > 0 AND ends <= @CurrentTime";

    public string GetPlayerPenaltyStatsQuery() => SharedQueries.PlayerPenaltyStats();

    public string GetWarnsMenuPageQuery() => SharedQueries.WarnsMenuPage();

    public string GetWarnsMenuCountQuery() => SharedQueries.WarnsMenuCount();

    public string GetOnlineCreditPlanQuery() => SharedQueries.OnlineCreditPlan();

    public string GetApplyOnlineCreditQuery(IReadOnlyList<Managers.OnlineCreditStep> steps) => SharedQueries.ApplyOnlineCredit(steps);
    public string GetExpiredOnlineMutesBatchQuery() => SharedQueries.ExpiredOnlineMutesBatch();

    public string GetPenaltyHistoryPageQuery(string? type) => SharedQueries.PenaltyHistoryPage(type);

    public string GetPenaltyHistoryCountQuery(string? type) => SharedQueries.PenaltyHistoryCount(type);

    public string GetPenaltyHistoryQuery() =>
        """
        SELECT b.id, 'BAN' AS type, b.player_name, b.admin_name, b.reason, b.duration, b.created, b.ends, b.status,
               ub.reason AS lift_reason, ub.date AS lift_date, ua.player_name AS lift_admin
        FROM sa_bans b
        LEFT JOIN sa_unbans ub ON ub.id = b.unban_id
        LEFT JOIN sa_admins ua ON ua.id = ub.admin_id
        WHERE b.player_steamid = @PlayerSteamID
        UNION ALL
        SELECT m.id, m.type, m.player_name, m.admin_name, m.reason, m.duration, m.created, m.ends, m.status,
               um.reason AS lift_reason, um.date AS lift_date, ua.player_name AS lift_admin
        FROM sa_mutes m
        LEFT JOIN sa_unmutes um ON um.id = m.unmute_id
        LEFT JOIN sa_admins ua ON ua.id = um.admin_id
        WHERE m.player_steamid = @PlayerSteamID
        UNION ALL
        SELECT w.id, 'WARN' AS type, w.player_name, w.admin_name, w.reason, w.duration, w.created, w.ends, w.status,
               NULL AS lift_reason, NULL AS lift_date, NULL AS lift_admin
        FROM sa_warns w
        WHERE w.player_steamid = @PlayerSteamID
        ORDER BY created DESC
        """;

}
