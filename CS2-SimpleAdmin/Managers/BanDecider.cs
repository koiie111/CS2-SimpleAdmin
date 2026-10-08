using CS2_SimpleAdmin.Database;
using CS2_SimpleAdmin.Models;
using Dapper;

namespace CS2_SimpleAdmin.Managers;

/// <summary>
/// The connect-time ban decision. Bans are network-wide and the shared database is the only authority:
/// <list type="bullet">
/// <item>An active (status ACTIVE, permanent or <c>ends</c> in the future) ban of the connecting SteamID64 is read
/// <b>from the database, by SteamID, for every connection</b>. It does not depend on BanType, on the IP, on
/// IgnoredIps, on CheckMultiAccountsByIp, on which server issued the ban or on how fresh the local cache is (a ban
/// written on another server one second ago is found; a ban lifted one second ago is not a ban).</item>
/// <item>IP-derived bans (BanType &gt; 0, shared IPs, multi-account links) are an addition: the local cache proposes a
/// candidate and the database confirms that exact ban row is still active, so an old positive cache entry cannot
/// block someone whose ban was lifted on another server.</item>
/// <item>A database error is an exception, never "not banned": the caller keeps the connection unverified.</item>
/// </list>
/// </summary>
internal static class BanDecider
{
    internal static async Task<BanCheckResult> DecideAsync(IDatabaseProvider provider, CacheManager cache,
        CS2_SimpleAdminConfig config, ulong steamId, string? ipAddress, DateTime now, CancellationToken ct)
    {
        if (steamId == 0)
            throw new InvalidOperationException("SteamID is not known yet (authorization not finished): the ban state cannot be verified");

        var steamBans = await ReadSteamBansAsync(provider, steamId, now, ct).ConfigureAwait(false);
        if (steamBans.Count > 0)
            return new BanCheckResult(true, steamBans[0], BanMatch.SteamId);

        var candidate = cache.CheckBanByIpOnly(config, steamId, ipAddress, now);
        if (!candidate.IsBanned || candidate.Ban == null)
            return BanCheckResult.NotBanned;

        var active = await ConfirmActiveAsync(provider, [candidate.Ban.Id], now, ct).ConfigureAwait(false);
        return active.Contains(candidate.Ban.Id) ? candidate : BanCheckResult.NotBanned;
    }

    private static async Task<List<BanRecord>> ReadSteamBansAsync(IDatabaseProvider provider, ulong steamId, DateTime now,
        CancellationToken ct)
    {
        await using var connection = await provider.CreateConnectionAsync(ct).ConfigureAwait(false);
        return (await connection.QueryAsync<BanRecord>(new CommandDefinition(provider.GetActiveSteamBansQuery(),
            new { PlayerSteamID = steamId, CurrentTime = now }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
    }

    /// <summary>Which of the given ban rows are active and unexpired in the database right now. Throws on a database error.</summary>
    internal static async Task<HashSet<int>> ConfirmActiveAsync(IDatabaseProvider provider, IReadOnlyCollection<int> banIds,
        DateTime now, CancellationToken ct)
    {
        var active = new HashSet<int>();
        if (banIds.Count == 0) return active;
        await using var connection = await provider.CreateConnectionAsync(ct).ConfigureAwait(false);
        var ids = banIds.Distinct().ToList();
        for (var i = 0; i < ids.Count; i += 500)
        {
            var batch = ids.GetRange(i, Math.Min(500, ids.Count - i));
            var rows = await connection.QueryAsync<BanRecord>(new CommandDefinition(provider.GetActiveBansByIdsQuery(),
                new { ids = batch, CurrentTime = now }, cancellationToken: ct)).ConfigureAwait(false);
            foreach (var row in rows) active.Add(row.Id);
        }

        return active;
    }
}
