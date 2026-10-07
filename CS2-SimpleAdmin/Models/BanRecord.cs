using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace CS2_SimpleAdmin.Models;

public enum BanStatus
{
    [Description("ACTIVE")] ACTIVE,
    [Description("UNBANNED")] UNBANNED,
    [Description("EXPIRED")] EXPIRED,
    [Description("")] UNKNOWN
}

/// <summary>
/// One sa_bans row as cached. Immutable: a published cache snapshot is never modified; changes produce a new
/// record (<c>with</c>) inside a new snapshot.
/// </summary>
public sealed record BanRecord
{
    [Column("id")]
    public int Id { get; init; }

    [Column("player_name")]
    public string? PlayerName { get; init; }

    [Column("player_steamid")]
    public ulong? PlayerSteamId { get; init; }

    [Column("player_ip")]
    public string? PlayerIp { get; init; }

    [Column("created")]
    public DateTime Created { get; init; }

    /// <summary>End of a timed ban (same clock as <c>created</c>). Ignored when <see cref="Duration"/> is 0.</summary>
    [Column("ends")]
    public DateTime? Ends { get; init; }

    /// <summary>Minutes; 0 = permanent.</summary>
    [Column("duration")]
    public int Duration { get; init; }

    [Column("status")]
    public required string Status { get; init; }

    [NotMapped]
    public BanStatus StatusEnum =>
        string.Equals(Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) ? BanStatus.ACTIVE :
        string.Equals(Status, "UNBANNED", StringComparison.OrdinalIgnoreCase) ? BanStatus.UNBANNED :
        string.Equals(Status, "EXPIRED", StringComparison.OrdinalIgnoreCase) ? BanStatus.EXPIRED :
        BanStatus.UNKNOWN;

    /// <summary>
    /// ACTIVE and not past its end. The second condition closes the window between a timed ban reaching
    /// <c>ends</c> and the periodic SQL expiry marking it EXPIRED (same rule as the expire query: ends &lt;= now).
    /// </summary>
    public bool IsEffectivelyActive(DateTime now) =>
        StatusEnum == BanStatus.ACTIVE && (Duration <= 0 || Ends == null || Ends.Value > now);
}
