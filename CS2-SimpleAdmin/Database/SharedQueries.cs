namespace CS2_SimpleAdmin.Database;

/// <summary>
/// Queries whose text is identical for MySQL/MariaDB and SQLite.
/// Penalties (bans, mutes, warns) are network-wide: no query here filters by <c>server_id</c>. The column is only
/// information about where a penalty was issued.
/// Parameters: @PlayerSteamID, @ids, @minutes, @limit, @offset, @muteType.
/// </summary>
internal static class SharedQueries
{
    /// <summary>Historic totals for one player (all rows, any status) in one round trip.</summary>
    public static string PlayerPenaltyStats() =>
        $"""
         SELECT
             (SELECT COUNT(*) FROM sa_bans  WHERE player_steamid = @PlayerSteamID) AS TotalBans,
             (SELECT COUNT(*) FROM sa_mutes WHERE player_steamid = @PlayerSteamID AND type = 'MUTE') AS TotalMutes,
             (SELECT COUNT(*) FROM sa_mutes WHERE player_steamid = @PlayerSteamID AND type = 'GAG') AS TotalGags,
             (SELECT COUNT(*) FROM sa_mutes WHERE player_steamid = @PlayerSteamID AND type = 'SILENCE') AS TotalSilences,
             (SELECT COUNT(*) FROM sa_warns WHERE player_steamid = @PlayerSteamID) AS TotalWarns
         """;

    /// <summary>
    /// TimeMode 0, step 1 (read): the active timed mutes of the listed players with their current <c>passed</c>, the
    /// pre-image of an <see cref="Managers.OnlineCredit"/> plan.
    /// </summary>
    public static string OnlineCreditPlan() =>
        $"SELECT id AS Id, player_steamid AS SteamId, COALESCE(passed, 0) AS Passed, created AS Created, duration AS Duration FROM sa_mutes WHERE player_steamid IN @ids AND duration > 0 AND status = 'ACTIVE'";

    /// <summary>
    /// TimeMode 0, step 2 (write): the compare-and-set of a set of planned steps in ONE statement. Per row it is
    /// <c>UPDATE … SET passed = target WHERE id = … AND passed = expected</c>, which changes the row only while
    /// <c>passed</c> still equals the pre-image of the plan, so re-running the statement any number of times (retry,
    /// lost commit acknowledgement) can apply each step at most once. Only integers taken from the plan are
    /// formatted into the text (no strings, no injection surface); the statement is bounded by the caller.
    /// </summary>
    public static string ApplyOnlineCredit(IReadOnlyList<Managers.OnlineCreditStep> steps)
    {
        if (steps.Count == 0) throw new ArgumentException("no steps", nameof(steps));
        var set = new System.Text.StringBuilder("UPDATE sa_mutes SET passed = CASE id");
        var where = new System.Text.StringBuilder();
        foreach (var s in steps)
        {
            set.Append(" WHEN ").Append(s.MuteId).Append(" THEN ").Append(s.Target);
            if (where.Length > 0) where.Append(" OR ");
            where.Append("(id = ").Append(s.MuteId).Append(" AND COALESCE(passed, 0) = ").Append(s.Expected).Append(')');
        }

        return set.Append(" END WHERE status = 'ACTIVE' AND (").Append(where).Append(')').ToString();
    }

    /// <summary>TimeMode 0: active timed mutes of the listed players whose online time is used up (minimal columns).</summary>
    public static string ExpiredOnlineMutesBatch() =>
        $"SELECT id AS Id, player_steamid AS SteamId, ends AS Ends FROM sa_mutes WHERE player_steamid IN @ids AND passed >= duration AND duration > 0 AND status = 'ACTIVE'";

    /// <summary>
    /// Active mutes of the listed players with the row id (stable identity for reconciling the in-memory state).
    /// Same activity rule as <c>GetIsMutedQuery</c>: permanent (duration = 0) or still running by real time (TimeMode 1,
    /// <c>@CurrentTime</c>) / by online minutes (TimeMode 0).
    /// </summary>
    public static string ActiveMutesBatch(int timeMode) =>
        "SELECT id AS Id, player_steamid AS SteamId, type AS Type, ends AS Ends, duration AS Duration, created AS Created, COALESCE(passed, 0) AS Passed " +
        "FROM sa_mutes WHERE player_steamid IN @ids AND status = 'ACTIVE' AND " +
        (timeMode == 1 ? "(duration = 0 OR ends > @CurrentTime)" : "(duration = 0 OR duration > COALESCE(passed, 0))");

    /// <summary>The connect-time ban decision: active bans of one SteamID64, permanent or not yet ended.</summary>
    public const string ActiveSteamBans =
        "SELECT id AS Id, player_name AS PlayerName, player_steamid AS PlayerSteamId, player_ip AS PlayerIp, status AS Status, " +
        "created AS Created, ends AS Ends, duration AS Duration FROM sa_bans " +
        "WHERE player_steamid = @PlayerSteamID AND status = 'ACTIVE' AND (duration <= 0 OR ends IS NULL OR ends > @CurrentTime) ORDER BY id";

    public const string ActiveBansByIds =
        "SELECT id AS Id, player_name AS PlayerName, player_steamid AS PlayerSteamId, player_ip AS PlayerIp, status AS Status, " +
        "created AS Created, ends AS Ends, duration AS Duration FROM sa_bans " +
        "WHERE id IN @ids AND status = 'ACTIVE' AND (duration <= 0 OR ends IS NULL OR ends > @CurrentTime)";

    /// <summary>Longest reason text the warns menu ever reads from SQL (longer ones are cut there, not after loading).</summary>
    public const int WarnMenuReasonChars = 80;

    /// <summary>
    /// One page of a player's warns for the menu: active first, then newest first. (active-flag, id) is unique, so the
    /// order is stable and pages neither repeat nor skip rows. Reason is cut in SQL.
    /// </summary>
    public static string WarnsMenuPage() =>
        $"SELECT id AS Id, status AS Status, SUBSTR(reason, 1, {WarnMenuReasonChars}) AS Reason FROM sa_warns WHERE player_steamid = @PlayerSteamID ORDER BY CASE WHEN status = 'ACTIVE' THEN 0 ELSE 1 END, id DESC LIMIT @limit OFFSET @offset";

    public static string WarnsMenuCount() =>
        $"SELECT COUNT(*) FROM sa_warns WHERE player_steamid = @PlayerSteamID";

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

    private static List<string> HistoryBranches(string? type)
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
                          WHERE b.player_steamid = @PlayerSteamID
                          """);
        if (allMutes || muteType != null)
            branches.Add($"""
                          SELECT m.id AS id, m.type AS type, m.player_name AS player_name, m.admin_name AS admin_name, m.reason AS reason,
                                 m.duration AS duration, m.created AS created, m.ends AS ends, m.status AS status,
                                 um.reason AS lift_reason, um.date AS lift_date, ua.player_name AS lift_admin
                          FROM sa_mutes m
                          LEFT JOIN sa_unmutes um ON um.id = m.unmute_id
                          LEFT JOIN sa_admins ua ON ua.id = um.admin_id
                          WHERE m.player_steamid = @PlayerSteamID{(muteType != null ? " AND m.type = @muteType" : "")}
                          """);
        if (warns)
            branches.Add($"""
                          SELECT w.id AS id, 'WARN' AS type, w.player_name AS player_name, w.admin_name AS admin_name, w.reason AS reason,
                                 w.duration AS duration, w.created AS created, w.ends AS ends, w.status AS status,
                                 NULL AS lift_reason, NULL AS lift_date, NULL AS lift_admin
                          FROM sa_warns w
                          WHERE w.player_steamid = @PlayerSteamID
                          """);
        return branches;
    }

    /// <summary>
    /// One page of history, newest first. (created, type, id) is unique across the union, so the order is stable
    /// and pages neither repeat nor skip rows that share a timestamp.
    /// </summary>
    public static string PenaltyHistoryPage(string? type) =>
        string.Join("\nUNION ALL\n", HistoryBranches(type)) +
        "\nORDER BY created DESC, type ASC, id DESC\nLIMIT @limit OFFSET @offset";

    public static string PenaltyHistoryCount(string? type)
    {
        var (bans, muteType, allMutes, warns) = Parts(type);
        var parts = new List<string>(3);
        if (bans) parts.Add($"(SELECT COUNT(*) FROM sa_bans WHERE player_steamid = @PlayerSteamID)");
        if (allMutes || muteType != null)
            parts.Add($"(SELECT COUNT(*) FROM sa_mutes WHERE player_steamid = @PlayerSteamID{(muteType != null ? " AND type = @muteType" : "")})");
        if (warns) parts.Add($"(SELECT COUNT(*) FROM sa_warns WHERE player_steamid = @PlayerSteamID)");
        return "SELECT " + string.Join(" + ", parts) + " AS Total";
    }
}
