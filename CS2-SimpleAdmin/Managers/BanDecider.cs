using CS2_SimpleAdmin.Database;
using CS2_SimpleAdmin.Models;
using Dapper;

namespace CS2_SimpleAdmin.Managers;

/// <summary>
/// The ban decision. Bans are network-wide and the shared database is the only authority:
/// <list type="bullet">
/// <item>An active (status ACTIVE, permanent or <c>ends</c> in the future) ban of the connecting SteamID64 is read
/// <b>from the database, by SteamID, for every connection</b> and for every online player in the periodic pass. It does
/// not depend on which server issued the ban or on how fresh the local cache is (a ban written on another server one
/// second ago is found; a ban lifted one second ago is not a ban).</item>
/// <item><b>IP addresses never ban.</b> BanType, IgnoredIps, CheckMultiAccountsByIp, shared addresses and IP history are not
/// consulted: a player is refused only for a ban of their own SteamID64. There are no cache candidates to confirm.</item>
/// <item>A database error is an exception, never "not banned": the caller keeps the connection unverified.</item>
/// </list>
/// </summary>
internal static class BanDecider
{
    /// <summary>Players per active-bans statement (keeps the IN list and the result bounded).</summary>
    internal const int BatchSize = 64;

    internal static async Task<BanCheckResult> DecideAsync(IDatabaseProvider provider, ulong steamId, DateTime now, CancellationToken ct)
    {
        if (steamId == 0)
            throw new InvalidOperationException("SteamID is not known yet (authorization not finished): the ban state cannot be verified");

        await using var connection = await provider.CreateConnectionAsync(ct).ConfigureAwait(false);
        var bans = (await connection.QueryAsync<BanRecord>(new CommandDefinition(provider.GetActiveSteamBansQuery(),
            new { PlayerSteamID = steamId, CurrentTime = now }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
        return bans.Count > 0 ? new BanCheckResult(true, bans[0], BanMatch.SteamId) : BanCheckResult.NotBanned;
    }

    /// <summary>
    /// Which of the given SteamID64s have an active, unexpired ban in the database right now (online players, one statement
    /// per <see cref="BatchSize"/> players, one connection). Throws on a database error: an unreadable database never
    /// means "nobody is banned" nor "everybody is".
    /// </summary>
    internal static async Task<HashSet<ulong>> FindBannedAsync(IDatabaseProvider provider, IReadOnlyCollection<ulong> steamIds,
        DateTime now, CancellationToken ct)
    {
        var banned = new HashSet<ulong>();
        var ids = steamIds.Where(id => id != 0).Distinct().ToList();
        if (ids.Count == 0) return banned;

        await using var connection = await provider.CreateConnectionAsync(ct).ConfigureAwait(false);
        var sql = provider.GetActiveSteamBansBatchQuery();
        for (var i = 0; i < ids.Count; i += BatchSize)
        {
            var batch = ids.GetRange(i, Math.Min(BatchSize, ids.Count - i));
            var rows = await connection.QueryAsync<ulong>(new CommandDefinition(sql, new { ids = batch, CurrentTime = now },
                cancellationToken: ct)).ConfigureAwait(false);
            foreach (var id in rows) banned.Add(id);
        }

        return banned;
    }
}
