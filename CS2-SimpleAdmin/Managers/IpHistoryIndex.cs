using System.Collections.Immutable;
using CS2_SimpleAdmin.Models;

namespace CS2_SimpleAdmin.Managers;

/// <summary>
/// Immutable IP history of all accounts, with the reverse index IP → accounts.
/// <para>
/// Layout: a large read-only base (plain dictionaries + arrays, built once by the full load: cheap to build, compact,
/// fast to read) and a small persistent overlay (<see cref="ImmutableDictionary{TKey,TValue}"/>) holding the keys
/// changed by incremental refreshes. Lookups check the overlay first. A refresh therefore costs
/// O(changed rows · log overlay) instead of copying a million-entry dictionary; when the overlay grows past
/// <see cref="CompactThreshold"/> keys the next update folds it into a new base (rare, background).
/// </para>
/// Nothing is mutated after construction; every update returns a new instance sharing the unchanged parts.
/// <para>
/// Removal: <see cref="With"/> only merges, so links that disappeared from SQL (the ExpireOldIpBans job, the site,
/// manual DELETEs) must be removed explicitly, in <b>both</b> directions (account → IPs and IP → accounts):
/// <see cref="Prune"/> drops records older than a cutoff for a bounded slice of accounts per call, and the cache
/// rebuilds the index when a checksum against SQL detects a deletion that has no time rule. An emptied entry is stored
/// in the overlay as an empty array (a tombstone shadowing the base) and disappears at the next compaction.
/// </para>
/// </summary>
internal sealed class IpHistoryIndex
{
    internal const int CompactThreshold = 50_000;

    public static readonly IpHistoryIndex Empty = new(new Dictionary<ulong, IpRecord[]>(), new Dictionary<uint, ulong[]>(),
        ImmutableDictionary<ulong, IpRecord[]>.Empty, ImmutableDictionary<uint, ulong[]>.Empty);

    private readonly Dictionary<ulong, IpRecord[]> _baseIps;
    private readonly Dictionary<uint, ulong[]> _baseAccounts;
    private readonly ImmutableDictionary<ulong, IpRecord[]> _overlayIps;
    private readonly ImmutableDictionary<uint, ulong[]> _overlayAccounts;

    private IpHistoryIndex(Dictionary<ulong, IpRecord[]> baseIps, Dictionary<uint, ulong[]> baseAccounts,
        ImmutableDictionary<ulong, IpRecord[]> overlayIps, ImmutableDictionary<uint, ulong[]> overlayAccounts)
    {
        _baseIps = baseIps;
        _baseAccounts = baseAccounts;
        _overlayIps = overlayIps;
        _overlayAccounts = overlayAccounts;
        AccountCount = CountKeys(baseIps, overlayIps);
        AddressCount = CountKeys(baseAccounts, overlayAccounts);
    }

    /// <summary>Distinct non-empty keys of base ∪ overlay (an empty overlay value hides the base key).</summary>
    private static int CountKeys<TKey, TValue>(Dictionary<TKey, TValue[]> baseMap, ImmutableDictionary<TKey, TValue[]> overlay)
        where TKey : notnull
    {
        var count = baseMap.Count;
        foreach (var (key, value) in overlay)
        {
            var inBase = baseMap.ContainsKey(key);
            if (value.Length == 0) { if (inBase) count--; }
            else if (!inBase) count++;
        }

        return count;
    }

    public int AccountCount { get; }
    public int AddressCount { get; }
    public int OverlayCount => _overlayIps.Count + _overlayAccounts.Count;

    public bool TryGetIps(ulong steamId, out IpRecord[] records)
    {
        if (_overlayIps.TryGetValue(steamId, out records!)) return records.Length > 0; // empty = removed
        return _baseIps.TryGetValue(steamId, out records!);
    }

    public bool TryGetAccounts(uint ip, out ulong[] accounts)
    {
        if (_overlayAccounts.TryGetValue(ip, out accounts!)) return accounts.Length > 0;
        return _baseAccounts.TryGetValue(ip, out accounts!);
    }

    /// <summary>When this account last used this IP, per the account's own record.</summary>
    public bool TryGetUsedAt(ulong steamId, uint ip, out DateTime usedAt)
    {
        if (TryGetIps(steamId, out var records))
            foreach (var r in records)
                if (r.Ip == ip)
                {
                    usedAt = r.UsedAt;
                    return true;
                }

        usedAt = default;
        return false;
    }

    /// <summary>Every account with at least one record (O(accounts); used to start a prune sweep).</summary>
    public ulong[] AccountIds()
    {
        var ids = new List<ulong>(AccountCount);
        foreach (var key in _baseIps.Keys)
            if (!_overlayIps.TryGetValue(key, out var shadow) || shadow.Length > 0) ids.Add(key);
        foreach (var (key, value) in _overlayIps)
            if (value.Length > 0 && !_baseIps.ContainsKey(key)) ids.Add(key);
        return ids.ToArray();
    }

    /// <summary>Modulus applied to SteamID64 in the checksum; must equal the literal in <see cref="CacheManager.IpChecksumSql"/>.</summary>
    internal const long SteamModulus = 2147483647;

    /// <summary>
    /// Count and sums over the records newer than <paramref name="cutoff"/> (the same triple SQL computes with
    /// COUNT/SUM over sa_players_ips), to detect rows deleted behind the cache's back. O(records), allocation-free;
    /// run on a background thread only, rarely.
    /// </summary>
    public (long Count, long SteamSum, long AddressSum) Checksum(DateTime cutoff)
    {
        // Terms are < 2^32 (steam % (2^31-1), UInt32 address), so the sums cannot overflow below 2^31-1 rows; the
        // explicit checked() turns a violated bound into an exception instead of a silent wrap.
        long count = 0, steamSum = 0, addressSum = 0;
        foreach (var (steam, records) in _baseIps)
        {
            if (_overlayIps.ContainsKey(steam)) continue;
            Accumulate(steam, records);
        }

        foreach (var (steam, records) in _overlayIps) Accumulate(steam, records);
        return (count, steamSum, addressSum);

        void Accumulate(ulong steam, IpRecord[] records)
        {
            foreach (var r in records)
            {
                if (r.UsedAt <= cutoff) continue;
                count++;
                steamSum = checked(steamSum + (long)(steam % (ulong)SteamModulus));
                addressSum = checked(addressSum + r.Ip);
            }
        }
    }

    /// <summary>
    /// New index without the records of <paramref name="accounts"/> that are not newer than <paramref name="cutoff"/>,
    /// removed from both directions. Returns this if nothing was removed. Cost O(accounts · own records), so the
    /// caller bounds the slice; never scans the whole index.
    /// </summary>
    public IpHistoryIndex Prune(DateTime cutoff, ReadOnlySpan<ulong> accounts)
    {
        ImmutableDictionary<ulong, IpRecord[]>.Builder? overlayIps = null;
        ImmutableDictionary<uint, ulong[]>.Builder? overlayAccounts = null;
        foreach (var steamId in accounts)
        {
            if (!TryGetIps(steamId, out var current)) continue;
            var kept = 0;
            foreach (var r in current)
                if (r.UsedAt > cutoff) kept++;
            if (kept == current.Length) continue;

            overlayIps ??= _overlayIps.ToBuilder();
            overlayAccounts ??= _overlayAccounts.ToBuilder();
            var survivors = new IpRecord[kept];
            var n = 0;
            foreach (var r in current)
            {
                if (r.UsedAt > cutoff) { survivors[n++] = r; continue; }
                var owners = overlayAccounts.TryGetValue(r.Ip, out var oa) ? oa
                    : _baseAccounts.TryGetValue(r.Ip, out var ba) ? ba : [];
                overlayAccounts[r.Ip] = owners.Where(o => o != steamId).ToArray(); // empty = removed
            }

            overlayIps[steamId] = survivors; // empty = removed
        }

        if (overlayIps == null) return this;
        var next = new IpHistoryIndex(_baseIps, _baseAccounts, overlayIps.ToImmutable(), overlayAccounts!.ToImmutable());
        return next.OverlayCount > CompactThreshold ? next.Compact() : next;
    }

    /// <summary>Builds a base from any number of rows (any order, duplicates allowed; latest used_at per (account, ip) wins).</summary>
    public static IpHistoryIndex Build(List<IpHistoryRow> rows, string unknownName)
    {
        // Sort once and group, instead of a dictionary per account
        var sorted = rows.ToArray();
        Array.Sort(sorted, static (a, b) =>
        {
            var c = a.Steamid.CompareTo(b.Steamid);
            if (c != 0) return c;
            c = a.Address.CompareTo(b.Address);
            return c != 0 ? c : b.Used_at.CompareTo(a.Used_at); // newest first within (account, ip)
        });

        var ips = new Dictionary<ulong, IpRecord[]>();
        var ownerCounts = new Dictionary<uint, int>();
        var buffer = new List<IpRecord>(8);
        for (var i = 0; i < sorted.Length;)
        {
            var steam = sorted[i].Steamid;
            buffer.Clear();
            while (i < sorted.Length && sorted[i].Steamid == steam)
            {
                var row = sorted[i];
                buffer.Add(new IpRecord(row.Address, row.Used_at, string.IsNullOrEmpty(row.Name) ? unknownName : row.Name));
                ownerCounts[row.Address] = ownerCounts.GetValueOrDefault(row.Address) + 1;
                i++;
                while (i < sorted.Length && sorted[i].Steamid == steam && sorted[i].Address == row.Address) i++; // older duplicates
            }

            ips[(ulong)steam] = buffer.ToArray();
        }

        var accounts = new Dictionary<uint, ulong[]>(ownerCounts.Count);
        var fill = new Dictionary<uint, int>(ownerCounts.Count);
        foreach (var (ip, count) in ownerCounts) accounts[ip] = new ulong[count];
        foreach (var (steam, records) in ips)
            foreach (var r in records)
            {
                var n = fill.GetValueOrDefault(r.Ip);
                accounts[r.Ip][n] = steam;
                fill[r.Ip] = n + 1;
            }

        return new IpHistoryIndex(ips, accounts, ImmutableDictionary<ulong, IpRecord[]>.Empty, ImmutableDictionary<uint, ulong[]>.Empty);
    }

    /// <summary>New index with rows merged in (latest used_at per (account, ip) wins). Returns this if nothing changed.</summary>
    public IpHistoryIndex With(IEnumerable<IpHistoryRow> rows, string unknownName)
    {
        var overlayIps = _overlayIps.ToBuilder();
        var overlayAccounts = _overlayAccounts.ToBuilder();
        var changed = false;
        foreach (var row in rows)
        {
            var steamId = (ulong)row.Steamid;
            var name = string.IsNullOrEmpty(row.Name) ? unknownName : row.Name;
            var current = overlayIps.TryGetValue(steamId, out var o) ? o : _baseIps.TryGetValue(steamId, out var b) ? b : [];
            var index = Array.FindIndex(current, r => r.Ip == row.Address);
            if (index < 0)
            {
                overlayIps[steamId] = [..current, new IpRecord(row.Address, row.Used_at, name)];
                var owners = overlayAccounts.TryGetValue(row.Address, out var oa) ? oa : _baseAccounts.TryGetValue(row.Address, out var ba) ? ba : [];
                if (Array.IndexOf(owners, steamId) < 0) overlayAccounts[row.Address] = [..owners, steamId];
                changed = true;
            }
            else if (row.Used_at >= current[index].UsedAt)
            {
                var copy = (IpRecord[])current.Clone();
                copy[index] = new IpRecord(row.Address, row.Used_at, name);
                overlayIps[steamId] = copy;
                changed = true;
            }
        }

        if (!changed) return this;
        var next = new IpHistoryIndex(_baseIps, _baseAccounts, overlayIps.ToImmutable(), overlayAccounts.ToImmutable());
        return next.OverlayCount > CompactThreshold ? next.Compact() : next;
    }

    /// <summary>Folds the overlay into a new base (copy of the base dictionaries; records arrays are shared).</summary>
    public IpHistoryIndex Compact()
    {
        var ips = new Dictionary<ulong, IpRecord[]>(_baseIps);
        foreach (var (k, v) in _overlayIps)
            if (v.Length > 0) ips[k] = v; else ips.Remove(k);
        var accounts = new Dictionary<uint, ulong[]>(_baseAccounts);
        foreach (var (k, v) in _overlayAccounts)
            if (v.Length > 0) accounts[k] = v; else accounts.Remove(k);
        return new IpHistoryIndex(ips, accounts, ImmutableDictionary<ulong, IpRecord[]>.Empty, ImmutableDictionary<uint, ulong[]>.Empty);
    }
}

/// <summary>Accumulates IP-history pages of the full load, then builds the index once.</summary>
internal sealed class IpIndexBuilder(string unknownName)
{
    private readonly List<IpHistoryRow> _rows = new();

    public void Add(IEnumerable<IpHistoryRow> rows) => _rows.AddRange(rows);

    public IpHistoryIndex Build() => IpHistoryIndex.Build(_rows, unknownName);
}
