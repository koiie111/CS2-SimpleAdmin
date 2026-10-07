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
    public string Type { get; init; } = "GAG";
    public DateTime? Ends { get; init; }
    public int Duration { get; init; }
}

/// <summary>TimeMode 0: a mute whose online minutes are used up.</summary>
public sealed class ExpiredOnlineMuteRow
{
    public long SteamId { get; init; }
    public DateTime? Ends { get; init; }
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
