namespace CS2_SimpleAdmin.Database;

/// <summary>
/// Queries whose text is identical for MySQL/MariaDB and SQLite.
/// Parameters: @PlayerSteamID, @serverid, @ids, @minutes, @limit, @offset, @muteType.
/// </summary>
internal static class SharedQueries
{
    private static string Server(bool multiServer, string alias = "") =>
        multiServer ? "" : $" AND {alias}server_id = @serverid";

    /// <summary>Historic totals for one player (all rows, any status) in one round trip.</summary>
    public static string PlayerPenaltyStats(bool multiServer) =>
        $"""
         SELECT
             (SELECT COUNT(*) FROM sa_bans  WHERE player_steamid = @PlayerSteamID{Server(multiServer)}) AS TotalBans,
             (SELECT COUNT(*) FROM sa_mutes WHERE player_steamid = @PlayerSteamID AND type = 'MUTE'{Server(multiServer)}) AS TotalMutes,
             (SELECT COUNT(*) FROM sa_mutes WHERE player_steamid = @PlayerSteamID AND type = 'GAG'{Server(multiServer)}) AS TotalGags,
             (SELECT COUNT(*) FROM sa_mutes WHERE player_steamid = @PlayerSteamID AND type = 'SILENCE'{Server(multiServer)}) AS TotalSilences,
             (SELECT COUNT(*) FROM sa_warns WHERE player_steamid = @PlayerSteamID{Server(multiServer)}) AS TotalWarns
         """;

    /// <summary>TimeMode 0: add the credited online minutes to every active timed mute of the listed players.</summary>
    public static string UpdateMutePassedBatch(bool multiServer) =>
        $"UPDATE sa_mutes SET passed = COALESCE(passed, 0) + @minutes WHERE player_steamid IN @ids AND duration > 0 AND status = 'ACTIVE'{Server(multiServer)}";

    /// <summary>TimeMode 0: active timed mutes of the listed players whose online time is used up (minimal columns).</summary>
    public static string ExpiredOnlineMutesBatch(bool multiServer) =>
        $"SELECT player_steamid AS SteamId, ends AS Ends FROM sa_mutes WHERE player_steamid IN @ids AND passed >= duration AND duration > 0 AND status = 'ACTIVE'{Server(multiServer)}";

    /// <summary>
    /// History filter word (plural, as typed by the admin) → which tables/types to read.
    /// null = everything.
    /// </summary>
    internal static (bool Bans, string? MuteType, bool AllMutes, bool Warns) Parts(string? type) => type?.ToLowerInvariant() switch
    {
        null => (true, null, true, true),
        "bans" => (true, null, false, false),
        "gags" => (false, "GAG", false, false),
        "mutes" => (false, "MUTE", false, false),
        "silences" => (false, "SILENCE", false, false),
        "warns" => (false, null, false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown history filter")
    };

    private static List<string> HistoryBranches(bool multiServer, string? type)
    {
        var (bans, muteType, allMutes, warns) = Parts(type);
        var branches = new List<string>(3);
        if (bans)
            branches.Add($"""
                          SELECT b.id AS id, 'BAN' AS type, b.player_name AS player_name, b.admin_name AS admin_name, b.reason AS reason,
                                 b.duration AS duration, b.created AS created, b.ends AS ends, b.status AS status,
                                 ub.reason AS lift_reason, ub.date AS lift_date, ua.player_name AS lift_admin
                          FROM sa_bans b
                          LEFT JOIN sa_unbans ub ON ub.id = b.unban_id
                          LEFT JOIN sa_admins ua ON ua.id = ub.admin_id
                          WHERE b.player_steamid = @PlayerSteamID{Server(multiServer, "b.")}
                          """);
        if (allMutes || muteType != null)
            branches.Add($"""
                          SELECT m.id AS id, m.type AS type, m.player_name AS player_name, m.admin_name AS admin_name, m.reason AS reason,
                                 m.duration AS duration, m.created AS created, m.ends AS ends, m.status AS status,
                                 um.reason AS lift_reason, um.date AS lift_date, ua.player_name AS lift_admin
                          FROM sa_mutes m
                          LEFT JOIN sa_unmutes um ON um.id = m.unmute_id
                          LEFT JOIN sa_admins ua ON ua.id = um.admin_id
                          WHERE m.player_steamid = @PlayerSteamID{(muteType != null ? " AND m.type = @muteType" : "")}{Server(multiServer, "m.")}
                          """);
        if (warns)
            branches.Add($"""
                          SELECT w.id AS id, 'WARN' AS type, w.player_name AS player_name, w.admin_name AS admin_name, w.reason AS reason,
                                 w.duration AS duration, w.created AS created, w.ends AS ends, w.status AS status,
                                 NULL AS lift_reason, NULL AS lift_date, NULL AS lift_admin
                          FROM sa_warns w
                          WHERE w.player_steamid = @PlayerSteamID{Server(multiServer, "w.")}
                          """);
        return branches;
    }

    /// <summary>
    /// One page of history, newest first. (created, type, id) is unique across the union, so the order is stable
    /// and pages neither repeat nor skip rows that share a timestamp.
    /// </summary>
    public static string PenaltyHistoryPage(bool multiServer, string? type) =>
        string.Join("\nUNION ALL\n", HistoryBranches(multiServer, type)) +
        "\nORDER BY created DESC, type ASC, id DESC\nLIMIT @limit OFFSET @offset";

    public static string PenaltyHistoryCount(bool multiServer, string? type)
    {
        var (bans, muteType, allMutes, warns) = Parts(type);
        var parts = new List<string>(3);
        if (bans) parts.Add($"(SELECT COUNT(*) FROM sa_bans WHERE player_steamid = @PlayerSteamID{Server(multiServer)})");
        if (allMutes || muteType != null)
            parts.Add($"(SELECT COUNT(*) FROM sa_mutes WHERE player_steamid = @PlayerSteamID{(muteType != null ? " AND type = @muteType" : "")}{Server(multiServer)})");
        if (warns) parts.Add($"(SELECT COUNT(*) FROM sa_warns WHERE player_steamid = @PlayerSteamID{Server(multiServer)})");
        return "SELECT " + string.Join(" + ", parts) + " AS Total";
    }
}
