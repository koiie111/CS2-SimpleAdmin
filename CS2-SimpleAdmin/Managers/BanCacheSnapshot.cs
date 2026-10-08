using System.Collections.Immutable;
using CS2_SimpleAdmin.Models;

namespace CS2_SimpleAdmin.Managers;

/// <summary>
/// One consistent, immutable generation of the bans cache.
/// <list type="bullet">
/// <item><see cref="ActiveBans"/> holds only ACTIVE bans (the operational set). History/statistics are read from
/// the database on demand, not kept here.</item>
/// <item><see cref="BySteamId"/>/<see cref="ByIp"/> are derived from <see cref="ActiveBans"/> in the same build, so
/// a reader can never see a SteamID index of one generation together with an IP index of another.</item>
/// <item><see cref="IpHistory"/> (multi-account history) and its reverse index IP → accounts: compact base + small
/// persistent overlay, updated incrementally (see <see cref="IpHistoryIndex"/>).</item>
/// </list>
/// A reader takes one snapshot reference per operation and never waits for the writer.
/// </summary>
internal sealed class BanCacheSnapshot
{
    public static readonly BanCacheSnapshot Empty = new(
        ImmutableDictionary<int, BanRecord>.Empty,
        IpHistoryIndex.Empty,
        ImmutableHashSet<uint>.Empty,
        isInitialized: false);

    private BanCacheSnapshot(
        ImmutableDictionary<int, BanRecord> activeBans,
        IpHistoryIndex ipHistory,
        ImmutableHashSet<uint> ignoredIps,
        bool isInitialized)
    {
        ActiveBans = activeBans;
        IpHistory = ipHistory;
        IgnoredIps = ignoredIps;
        IsInitialized = isInitialized;

        var bySteam = new Dictionary<ulong, BanRecord[]>();
        var byIp = new Dictionary<uint, BanRecord[]>();
        long idSum = 0;
        foreach (var ban in activeBans.Values)
        {
            idSum += ban.Id;
            if (ban.PlayerSteamId is { } steamId) Append(bySteam, steamId, ban);
            if (!string.IsNullOrEmpty(ban.PlayerIp) && IpHelper.TryConvertIpToUint(ban.PlayerIp, out var ip))
                Append(byIp, ip, ban);
        }

        BySteamId = bySteam;
        ByIp = byIp;
        ActiveIdSum = idSum;
    }

    public ImmutableDictionary<int, BanRecord> ActiveBans { get; }
    public IReadOnlyDictionary<ulong, BanRecord[]> BySteamId { get; }
    public IReadOnlyDictionary<uint, BanRecord[]> ByIp { get; }
    public IpHistoryIndex IpHistory { get; }
    public ImmutableHashSet<uint> IgnoredIps { get; }
    public bool IsInitialized { get; }

    /// <summary>Checksum of the ACTIVE set, compared with the DB to detect deletions/missed changes.</summary>
    public long ActiveIdSum { get; }
    public int ActiveCount => ActiveBans.Count;
    public int IpAccountCount => IpHistory.AccountCount;
    public int IpAddressCount => IpHistory.AddressCount;

    private static void Append<TKey>(Dictionary<TKey, BanRecord[]> index, TKey key, BanRecord ban) where TKey : notnull
    {
        if (index.TryGetValue(key, out var existing))
        {
            var grown = new BanRecord[existing.Length + 1];
            existing.CopyTo(grown, 0);
            grown[^1] = ban;
            index[key] = grown;
        }
        else
        {
            index[key] = [ban];
        }
    }

    // ------------------------------------------------------------------ building

    public static BanCacheSnapshot Create(IEnumerable<BanRecord> activeBans, IpHistoryIndex ipHistory, IEnumerable<uint> ignoredIps)
    {
        var builder = ImmutableDictionary.CreateBuilder<int, BanRecord>();
        foreach (var ban in activeBans)
            if (ban.StatusEnum == BanStatus.ACTIVE)
                builder[ban.Id] = ban;
        return new BanCacheSnapshot(builder.ToImmutable(), ipHistory, ignoredIps.ToImmutableHashSet(), true);
    }

    /// <summary>New generation with the given ban rows upserted (ACTIVE) or removed (any other status).</summary>
    public BanCacheSnapshot WithBans(IEnumerable<BanRecord> changed, IEnumerable<int>? removedIds = null)
    {
        var builder = ActiveBans.ToBuilder();
        foreach (var ban in changed)
        {
            if (ban.StatusEnum == BanStatus.ACTIVE) builder[ban.Id] = ban;
            else builder.Remove(ban.Id);
        }

        if (removedIds != null)
            foreach (var id in removedIds) builder.Remove(id);

        var next = builder.ToImmutable();
        return ReferenceEquals(next, ActiveBans) ? this : new BanCacheSnapshot(next, IpHistory, IgnoredIps, IsInitialized);
    }

    /// <summary>New generation with IP-history rows merged in (latest used_at per (steamid, ip) wins).</summary>
    public BanCacheSnapshot WithIpHistory(IEnumerable<IpHistoryRow> rows, string unknownName)
    {
        var next = IpHistory.With(rows, unknownName);
        return ReferenceEquals(next, IpHistory) ? this : new BanCacheSnapshot(ActiveBans, next, IgnoredIps, IsInitialized);
    }

    /// <summary>New generation with the IP history of a slice of accounts pruned (see <see cref="IpHistoryIndex.Prune"/>).</summary>
    public BanCacheSnapshot WithIpPrune(DateTime cutoff, ReadOnlySpan<ulong> accounts)
    {
        var next = IpHistory.Prune(cutoff, accounts);
        return ReferenceEquals(next, IpHistory) ? this : new BanCacheSnapshot(ActiveBans, next, IgnoredIps, IsInitialized);
    }

    /// <summary>New generation with the IP history replaced (rebuilt from SQL).</summary>
    public BanCacheSnapshot WithIpIndex(IpHistoryIndex index) =>
        new(ActiveBans, index, IgnoredIps, IsInitialized);

    /// <summary>Builds the IP history index from a full history (used by the full load and tests).</summary>
    public static IpHistoryIndex BuildIpIndexes(IEnumerable<IpHistoryRow> rows, string unknownName) =>
        IpHistoryIndex.Build(rows.ToList(), unknownName);

    // ------------------------------------------------------------------ lookups (pure, any thread)

    public BanRecord? FindActiveBySteamId(ulong steamId, DateTime now)
    {
        if (!BySteamId.TryGetValue(steamId, out var bans)) return null;
        foreach (var ban in bans)
            if (ban.IsEffectivelyActive(now)) return ban;
        return null;
    }

    /// <summary>
    /// Active ban on this exact IP, honouring ExpireOldIpBans the way the SQL job does
    /// (the IP of a ban whose <c>ends</c> is older than N days is cleared in the database).
    /// </summary>
    public BanRecord? FindActiveByIp(uint ip, DateTime now, int expireOldIpBansDays)
    {
        if (!ByIp.TryGetValue(ip, out var bans)) return null;
        var cutoff = expireOldIpBansDays > 0 ? now.AddDays(-expireOldIpBansDays) : DateTime.MinValue;
        foreach (var ban in bans)
        {
            if (!ban.IsEffectivelyActive(now)) continue;
            if (expireOldIpBansDays > 0 && ban.Ends is { } ends && ends <= cutoff) continue;
            return ban;
        }

        return null;
    }

    /// <summary>
    /// Original semantics of <c>IsPlayerBanned</c>: SteamID ban, otherwise (BanType != 0) a ban on the current IP
    /// unless the IP is in IgnoredIps.
    /// </summary>
    public BanCheckResult CheckPlayer(ulong? steamId, string? ipAddress, int banType, int expireOldIpBansDays, DateTime now)
    {
        if (steamId.HasValue && FindActiveBySteamId(steamId.Value, now) is { } steamBan)
            return new BanCheckResult(true, steamBan, BanMatch.SteamId);

        if (banType == 0 || string.IsNullOrEmpty(ipAddress) || !IpHelper.TryConvertIpToUint(ipAddress, out var ip) ||
            IgnoredIps.Contains(ip))
            return BanCheckResult.NotBanned;

        return FindActiveByIp(ip, now, expireOldIpBansDays) is { } ipBan
            ? new BanCheckResult(true, ipBan, BanMatch.Ip)
            : BanCheckResult.NotBanned;
    }

    /// <summary>
    /// Original semantics of <c>IsPlayerOrAnyIpBanned</c>, without scanning the whole IP history:
    /// SteamID ban → direct IP ban (also requiring <c>created</c> within ExpireOldIpBans days, as before) →
    /// another account that used the current IP is banned → another account that shares any IP from this
    /// player's history is banned. Uses the IP → accounts reverse index: O(own IPs + accounts sharing them).
    /// </summary>
    public BanCheckResult CheckPlayerOrAnyIp(ulong steamId, string? ipAddress, int banType, int expireOldIpBansDays,
        bool checkMultiAccounts, DateTime now, bool includeSteamMatch = true)
    {
        if (includeSteamMatch && FindActiveBySteamId(steamId, now) is { } steamBan)
            return new BanCheckResult(true, steamBan, BanMatch.SteamId);

        if (banType == 0 || string.IsNullOrEmpty(ipAddress) || !IpHelper.TryConvertIpToUint(ipAddress, out var ip) ||
            IgnoredIps.Contains(ip))
            return BanCheckResult.NotBanned;

        var ipBan = FindActiveByIp(ip, now, expireOldIpBansDays);
        if (ipBan != null && (expireOldIpBansDays <= 0 || ipBan.Created >= now.AddDays(-expireOldIpBansDays)))
            return new BanCheckResult(true, ipBan, BanMatch.Ip);

        // Accounts that used the current IP (their own link to it must still be inside ExpireOldIpBans)
        if (FindBannedOtherAccount(ip, steamId, now, expireOldIpBansDays) is { } sharedBan)
            return new BanCheckResult(true, sharedBan, BanMatch.SharedIp);

        if (!checkMultiAccounts || !IpHistory.TryGetIps(steamId, out var ownIps))
            return BanCheckResult.NotBanned;

        // Accounts that share any IP from this player's history. Records older than ExpireOldIpBans are deleted
        // from sa_players_ips by the expiry job, so they are skipped here too.
        var historyCutoff = expireOldIpBansDays > 0 ? now.AddDays(-expireOldIpBansDays) : DateTime.MinValue;
        foreach (var record in ownIps)
        {
            if (record.UsedAt <= historyCutoff && expireOldIpBansDays > 0) continue;
            if (FindBannedOtherAccount(record.Ip, steamId, now, expireOldIpBansDays) is { } historyBan)
                return new BanCheckResult(true, historyBan, BanMatch.SharedIpHistory);
        }

        return BanCheckResult.NotBanned;
    }

    /// <summary>
    /// A banned account other than <paramref name="self"/> that uses <paramref name="ip"/>. The link counts only while
    /// <i>that owner's own</i> record of the IP is newer than the ExpireOldIpBans cutoff, exactly as if the SQL expiry
    /// job (which deletes older sa_players_ips rows) had already run; so the answer equals a full rebuild even before
    /// the background prune removed the stale link from memory.
    /// </summary>
    private BanRecord? FindBannedOtherAccount(uint ip, ulong self, DateTime now, int expireOldIpBansDays)
    {
        if (!IpHistory.TryGetAccounts(ip, out var owners)) return null;
        var cutoff = expireOldIpBansDays > 0 ? now.AddDays(-expireOldIpBansDays) : DateTime.MinValue;
        foreach (var other in owners)
        {
            if (other == self) continue;
            if (!IpHistory.TryGetUsedAt(other, ip, out var usedAt)) continue; // reverse link without a forward record
            if (expireOldIpBansDays > 0 && usedAt <= cutoff) continue;
            if (FindActiveBySteamId(other, now) is { } ban) return ban;
        }

        return null;
    }

    /// <summary>Accounts that used <paramref name="ip"/>; links older than ExpireOldIpBans days are ignored (0 = keep all).</summary>
    public IReadOnlyList<(ulong SteamId, DateTime UsedAt, string PlayerName)> GetAccountsByIp(uint ip, DateTime now,
        int expireOldIpBansDays)
    {
        if (!IpHistory.TryGetAccounts(ip, out var owners)) return [];
        var cutoff = expireOldIpBansDays > 0 ? now.AddDays(-expireOldIpBansDays) : DateTime.MinValue;
        var result = new List<(ulong, DateTime, string)>(owners.Length);
        foreach (var owner in owners)
        {
            if (!IpHistory.TryGetIps(owner, out var records)) continue;
            foreach (var r in records)
                if (r.Ip == ip)
                {
                    if (expireOldIpBansDays <= 0 || r.UsedAt > cutoff) result.Add((owner, r.UsedAt, r.PlayerName));
                    break;
                }
        }

        return result;
    }
}

internal enum BanMatch
{
    None,
    SteamId,
    Ip,
    SharedIp,
    SharedIpHistory
}

internal readonly record struct BanCheckResult(bool IsBanned, BanRecord? Ban, BanMatch Match)
{
    public static readonly BanCheckResult NotBanned = new(false, null, BanMatch.None);
}
