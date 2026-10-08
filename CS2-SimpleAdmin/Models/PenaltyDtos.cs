namespace CS2_SimpleAdmin.Models;

/// <summary>Historic totals of one player (COUNT(*) is BIGINT on MySQL, INTEGER on SQLite → long).</summary>
public sealed class PlayerPenaltyStats
{
    public long TotalBans { get; init; }
    public long TotalMutes { get; init; }
    public long TotalGags { get; init; }
    public long TotalSilences { get; init; }
    public long TotalWarns { get; init; }
}

/// <summary>Result of the mute stats query (GetMuteStatsQuery).</summary>
public sealed class MuteStats
{
    public long TotalMutes { get; init; }
    public long TotalGags { get; init; }
    public long TotalSilences { get; init; }
}

/// <summary>An active gag/mute/silence as needed to apply it on connect.</summary>
public sealed class ActiveMuteRow
{
    /// <summary>sa_mutes.id: the identity of the row, so a later read can tell "same mute", "extended" and "gone" apart.</summary>
    public long Id { get; init; }

    /// <summary>Owner (player_steamid). Set by the batched read, 0 for the single-player read.</summary>
    public long SteamId { get; init; }

    public string Type { get; init; } = "GAG";
    public DateTime? Ends { get; init; }
    public int Duration { get; init; }
    public DateTime? Created { get; init; }

    /// <summary>Online minutes already used (TimeMode 0).</summary>
    public int Passed { get; init; }
}

/// <summary>TimeMode 0: a mute whose online minutes are used up.</summary>
public sealed class ExpiredOnlineMuteRow
{
    public long Id { get; init; }
    public long SteamId { get; init; }
    public DateTime? Ends { get; init; }
}

/// <summary>One line of the warns menu (reason already cut in SQL).</summary>
public sealed class WarnMenuRow
{
    public int Id { get; init; }
    public string Status { get; init; } = "";
    public string? Reason { get; init; }
}

/// <summary>An active timed mute with its current online-time counter (pre-image of an online credit plan).</summary>
public sealed class OnlineCreditPlanRow
{
    public int Id { get; init; }
    public long SteamId { get; init; }
    public int Passed { get; init; }
    public DateTime? Created { get; init; }
    public int Duration { get; init; }
}

/// <summary>One css_history line, newest first.</summary>
public sealed class PenaltyHistoryRow
{
    public int Id { get; init; }
    public string Type { get; init; } = "";
    public string? Player_Name { get; init; }
    public string? Admin_Name { get; init; }
    public string? Reason { get; init; }
    public int Duration { get; init; }
    public object? Created { get; init; }
    public object? Ends { get; init; }
    public string? Status { get; init; }
    public string? Lift_Reason { get; init; }
    public object? Lift_Date { get; init; }
    public string? Lift_Admin { get; init; }
}
