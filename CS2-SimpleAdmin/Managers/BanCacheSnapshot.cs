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
/// <item><see cref="IpsBySteamId"/> (multi-account history) and its reverse index <see cref="AccountsByIp"/> are
/// persistent immutable dictionaries, updated incrementally with structural sharing (O(changes·log n)).</item>
/// </list>
/// A reader takes one snapshot reference per operation and never waits for the writer.
/// </summary>
internal sealed class BanCacheSnapshot
{
    public static readonly BanCacheSnapshot Empty = new(
        ImmutableDictionary<int, BanRecord>.Empty,
        ImmutableDictionary<ulong, ImmutableArray<IpRecord>>.Empty,
        ImmutableDictionary<uint, ImmutableArray<ulong>>.Empty,
        ImmutableHashSet<uint>.Empty,
        isInitialized: false);

    private BanCacheSnapshot(
        ImmutableDictionary<int, BanRecord> activeBans,
        ImmutableDictionary<ulong, ImmutableArray<IpRecord>> ipsBySteamId,
        ImmutableDictionary<uint, ImmutableArray<ulong>> accountsByIp,
        ImmutableHashSet<uint> ignoredIps,
        bool isInitialized)
    {
        ActiveBans = activeBans;
        IpsBySteamId = ipsBySteamId;
        AccountsByIp = accountsByIp;
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
    public ImmutableDictionary<ulong, ImmutableArray<IpRecord>> IpsBySteamId { get; }
    public ImmutableDictionary<uint, ImmutableArray<ulong>> AccountsByIp { get; }
    public ImmutableHashSet<uint> IgnoredIps { get; }
    public bool IsInitialized { get; }

    /// <summary>Checksum of the ACTIVE set, compared with the DB to detect deletions/missed changes.</summary>
    public long ActiveIdSum { get; }
    public int ActiveCount => ActiveBans.Count;
    public int IpRecordCount => IpsBySteamId.Count;

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

    public static BanCacheSnapshot Create(IEnumerable<BanRecord> activeBans,
        ImmutableDictionary<ulong, ImmutableArray<IpRecord>> ipsBySteamId,
        ImmutableDictionary<uint, ImmutableArray<ulong>> accountsByIp,
        IEnumerable<uint> ignoredIps)
    {
        var builder = ImmutableDictionary.CreateBuilder<int, BanRecord>();
        foreach (var ban in activeBans)
            if (ban.StatusEnum == BanStatus.ACTIVE)
                builder[ban.Id] = ban;
        return new BanCacheSnapshot(builder.ToImmutable(), ipsBySteamId, accountsByIp, ignoredIps.ToImmutableHashSet(), true);
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
        return ReferenceEquals(next, ActiveBans) ? this : new BanCacheSnapshot(next, IpsBySteamId, AccountsByIp, IgnoredIps, IsInitialized);
    }

    /// <summary>New generation with IP-history rows merged in (latest used_at per (steamid, ip) wins).</summary>
    public BanCacheSnapshot WithIpHistory(IEnumerable<IpHistoryRow> rows, string unknownName)
    {
        var ips = IpsBySteamId.ToBuilder();
        var accounts = AccountsByIp.ToBuilder();
        var changed = false;
        foreach (var row in rows)
        {
            var steamId = (ulong)row.Steamid;
            var name = string.IsNullOrEmpty(row.Name) ? unknownName : row.Name;
            var current = ips.TryGetValue(steamId, out var list) ? list : ImmutableArray<IpRecord>.Empty;
            var index = -1;
            for (var i = 0; i < current.Length; i++)
                if (current[i].Ip == row.Address) { index = i; break; }

            if (index < 0)
            {
                ips[steamId] = current.Add(new IpRecord(row.Address, row.Used_at, name));
                var owners = accounts.TryGetValue(row.Address, out var o) ? o : ImmutableArray<ulong>.Empty;
                if (!owners.Contains(steamId)) accounts[row.Address] = owners.Add(steamId);
                changed = true;
            }
            else if (row.Used_at >= current[index].UsedAt)
            {
                ips[steamId] = current.SetItem(index, new IpRecord(row.Address, row.Used_at, name));
                changed = true;
            }
        }

        return changed ? new BanCacheSnapshot(ActiveBans, ips.ToImmutable(), accounts.ToImmutable(), IgnoredIps, IsInitialized) : this;
    }

    /// <summary>Builds both IP dictionaries from a full history (used by the full load).</summary>
    public static (ImmutableDictionary<ulong, ImmutableArray<IpRecord>> Ips, ImmutableDictionary<uint, ImmutableArray<ulong>> Accounts)
        BuildIpIndexes(IEnumerable<IpHistoryRow> rows, string unknownName)
    {
        var builder = new IpIndexBuilder(unknownName);
        builder.Add(rows);
        return builder.Build();
    }

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
        bool checkMultiAccounts, DateTime now)
    {
        if (FindActiveBySteamId(steamId, now) is { } steamBan)
            return new BanCheckResult(true, steamBan, BanMatch.SteamId);

        if (banType == 0 || string.IsNullOrEmpty(ipAddress) || !IpHelper.TryConvertIpToUint(ipAddress, out var ip) ||
            IgnoredIps.Contains(ip))
            return BanCheckResult.NotBanned;

        var ipBan = FindActiveByIp(ip, now, expireOldIpBansDays);
        if (ipBan != null && (expireOldIpBansDays <= 0 || ipBan.Created >= now.AddDays(-expireOldIpBansDays)))
            return new BanCheckResult(true, ipBan, BanMatch.Ip);

        // Accounts that used the current IP
        if (FindBannedOtherAccount(ip, steamId, now) is { } sharedBan)
            return new BanCheckResult(true, sharedBan, BanMatch.SharedIp);

        if (!checkMultiAccounts || !IpsBySteamId.TryGetValue(steamId, out var ownIps))
            return BanCheckResult.NotBanned;

        // Accounts that share any IP from this player's history. Records older than ExpireOldIpBans are deleted
        // from sa_players_ips by the expiry job, so they are skipped here too.
        var historyCutoff = expireOldIpBansDays > 0 ? now.AddDays(-expireOldIpBansDays) : DateTime.MinValue;
        foreach (var record in ownIps)
        {
            if (record.UsedAt <= historyCutoff && expireOldIpBansDays > 0) continue;
            if (FindBannedOtherAccount(record.Ip, steamId, now) is { } historyBan)
                return new BanCheckResult(true, historyBan, BanMatch.SharedIpHistory);
        }

        return BanCheckResult.NotBanned;
    }

    private BanRecord? FindBannedOtherAccount(uint ip, ulong self, DateTime now)
    {
        if (!AccountsByIp.TryGetValue(ip, out var owners)) return null;
        foreach (var other in owners)
        {
            if (other == self) continue;
            if (FindActiveBySteamId(other, now) is { } ban) return ban;
        }

        return null;
    }

    public IReadOnlyList<(ulong SteamId, DateTime UsedAt, string PlayerName)> GetAccountsByIp(uint ip)
    {
        if (!AccountsByIp.TryGetValue(ip, out var owners)) return [];
        var result = new List<(ulong, DateTime, string)>(owners.Length);
        foreach (var owner in owners)
        {
            if (!IpsBySteamId.TryGetValue(owner, out var records)) continue;
            foreach (var r in records)
                if (r.Ip == ip) { result.Add((owner, r.UsedAt, r.PlayerName)); break; }
        }

        return result;
    }
}

/// <summary>Accumulates IP-history rows (any order, duplicates allowed) and builds both immutable indexes.</summary>
internal sealed class IpIndexBuilder(string unknownName)
{
    private readonly Dictionary<ulong, Dictionary<uint, IpRecord>> _perSteam = new();

    public void Add(IEnumerable<IpHistoryRow> rows)
    {
        foreach (var row in rows)
        {
            var steamId = (ulong)row.Steamid;
            if (!_perSteam.TryGetValue(steamId, out var map))
                _perSteam[steamId] = map = new Dictionary<uint, IpRecord>(2);
            if (!map.TryGetValue(row.Address, out var existing) || row.Used_at > existing.UsedAt)
                map[row.Address] = new IpRecord(row.Address, row.Used_at, string.IsNullOrEmpty(row.Name) ? unknownName : row.Name);
        }
    }

    public (ImmutableDictionary<ulong, ImmutableArray<IpRecord>> Ips, ImmutableDictionary<uint, ImmutableArray<ulong>> Accounts) Build()
    {
        var ips = ImmutableDictionary.CreateBuilder<ulong, ImmutableArray<IpRecord>>();
        var accountLists = new Dictionary<uint, List<ulong>>();
        foreach (var (steamId, map) in _perSteam)
        {
            ips[steamId] = [..map.Values];
            foreach (var ip in map.Keys)
            {
                if (!accountLists.TryGetValue(ip, out var owners))
                    accountLists[ip] = owners = new List<ulong>(1);
                owners.Add(steamId);
            }
        }

        var accounts = ImmutableDictionary.CreateBuilder<uint, ImmutableArray<ulong>>();
        foreach (var (ip, owners) in accountLists)
            accounts[ip] = [..owners];
        return (ips.ToImmutable(), accounts.ToImmutable());
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
