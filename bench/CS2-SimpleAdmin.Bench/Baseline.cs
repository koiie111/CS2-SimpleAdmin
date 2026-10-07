using System.Collections.Concurrent;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;

namespace CS2_SimpleAdmin.Bench;

// Faithful copies of the algorithms of commit f544af7 (the audited version), reduced to their data structures:
// no CSS/DB calls, background side effects (Task.Run(UpdatePlayerData)) replaced by a counter. Used only to
// compare costs on the same machine and data. Comments mark where code was taken from.

/// <summary>Original BanRecord: StatusEnum upper-cases Status on every access.</summary>
public record OldBanRecord
{
    public int Id { get; init; }
    public string? PlayerName { get; set; }
    public ulong? PlayerSteamId { get; set; }
    public string? PlayerIp { get; set; }
    public DateTime Created { get; init; }
    public required string Status { get; init; }

    public BanStatus StatusEnum => Status.ToUpper() switch
    {
        "ACTIVE" => BanStatus.ACTIVE,
        "UNBANNED" => BanStatus.UNBANNED,
        "EXPIRED" => BanStatus.EXPIRED,
        _ => BanStatus.UNKNOWN
    };
}

/// <summary>CacheManager.cs @ f544af7 (InitializeCacheAsync body, RebuildIndexesCore, GetAccountsByIp, IsPlayerOrAnyIpBanned).</summary>
public sealed class OldCache
{
    private readonly ConcurrentDictionary<int, OldBanRecord> _banCache = [];
    private ConcurrentDictionary<ulong, List<OldBanRecord>> _steamIdIndex = [];
    private ConcurrentDictionary<uint, List<OldBanRecord>> _ipIndex = [];
    private readonly ConcurrentDictionary<ulong, HashSet<IpRecord>> _playerIpsCache = [];
    private readonly HashSet<uint> _cachedIgnoredIps = [];
    public int SideEffects;

    public void Initialize(IEnumerable<OldBanRecord> bans, IEnumerable<IpHistoryRow> ipHistorySortedBySteamAddressUsedAtDesc)
    {
        var unknownName = "Unknown";
        var currentSteamId = 0UL;
        var currentIpSet = new HashSet<IpRecord>(new IpRecordComparer());
        var latestIpTimestamps = new Dictionary<uint, DateTime>();

        foreach (var record in ipHistorySortedBySteamAddressUsedAtDesc)
        {
            if ((ulong)record.Steamid != currentSteamId && currentSteamId != 0)
            {
                _playerIpsCache[currentSteamId] = currentIpSet;
                currentIpSet = new HashSet<IpRecord>(new IpRecordComparer());
                latestIpTimestamps.Clear();
            }

            currentSteamId = (ulong)record.Steamid;
            if (!latestIpTimestamps.TryGetValue(record.Address, out var existingTimestamp) || record.Used_at > existingTimestamp)
            {
                latestIpTimestamps[record.Address] = record.Used_at;
                currentIpSet.Add(new IpRecord(record.Address, record.Used_at, string.IsNullOrEmpty(record.Name) ? unknownName : record.Name));
            }
        }

        if (currentSteamId != 0) _playerIpsCache[currentSteamId] = currentIpSet;
        foreach (var ban in bans) _banCache.TryAdd(ban.Id, ban);
        RebuildIndexesCore(banType: 1);
    }

    public void RebuildIndexesCore(int banType)
    {
        var steamIdIndex = new ConcurrentDictionary<ulong, List<OldBanRecord>>();
        var ipIndex = new ConcurrentDictionary<uint, List<OldBanRecord>>();
        var checkIpBans = banType != 0;
        var activeBans = _banCache.Values.Where(b => b.StatusEnum == BanStatus.ACTIVE);
        foreach (var ban in activeBans)
        {
            if (ban.PlayerSteamId.HasValue)
            {
                var steamId = ban.PlayerSteamId.Value;
                if (!steamIdIndex.TryGetValue(steamId, out var steamList))
                {
                    steamList = new List<OldBanRecord>();
                    steamIdIndex[steamId] = steamList;
                }

                steamList.Add(ban);
            }

            if (checkIpBans && !string.IsNullOrEmpty(ban.PlayerIp) && IpHelper.TryConvertIpToUint(ban.PlayerIp, out var ipUInt))
            {
                if (!ipIndex.TryGetValue(ipUInt, out var ipList))
                {
                    ipList = new List<OldBanRecord>();
                    ipIndex[ipUInt] = ipList;
                }

                ipList.Add(ban);
            }
        }

        Volatile.Write(ref _steamIdIndex, steamIdIndex);
        Volatile.Write(ref _ipIndex, ipIndex);
    }

    public List<(ulong SteamId, DateTime UsedAt, string PlayerName)> GetAccountsByIp(string ipAddress)
    {
        var ipAsUint = IpHelper.IpToUint(ipAddress);
        var results = new List<(ulong, DateTime, string)>();
        foreach (var (steamId, ipSet) in _playerIpsCache)
        foreach (var entry in ipSet)
            if (entry.Ip == ipAsUint)
                results.Add((steamId, entry.UsedAt, entry.PlayerName));
        return results;
    }

    public bool IsPlayerOrAnyIpBanned(string playerName, ulong steamId, string? ipAddress, DateTime now, int expireOldIpBans = 0)
    {
        if (_steamIdIndex.TryGetValue(steamId, out var steamBans))
        {
            var activeBan = steamBans.FirstOrDefault(b => b.StatusEnum == BanStatus.ACTIVE);
            if (activeBan != null && _banCache.TryGetValue(activeBan.Id, out var cachedBan) && cachedBan.StatusEnum == BanStatus.ACTIVE)
                return true;
        }

        if (string.IsNullOrEmpty(ipAddress) || !IpHelper.TryConvertIpToUint(ipAddress, out var ipUInt)) return false;
        if (_cachedIgnoredIps.Contains(ipUInt)) return false;

        if (_ipIndex.TryGetValue(ipUInt, out var ipBanRecords))
        {
            var ipBan = ipBanRecords.FirstOrDefault(r => r.StatusEnum == BanStatus.ACTIVE);
            if (ipBan != null && _banCache.TryGetValue(ipBan.Id, out var cachedIpBan) && cachedIpBan.StatusEnum == BanStatus.ACTIVE)
            {
                if (expireOldIpBans <= 0 || ipBan.Created >= now.AddDays(-expireOldIpBans))
                {
                    SideEffects++;
                    return true;
                }
            }
        }

        if (!_playerIpsCache.IsEmpty)
        {
            foreach (var (otherSteamId, ipSet) in _playerIpsCache)
            {
                if (otherSteamId == steamId) continue;
                if (ipSet.All(record => record.Ip != ipUInt)) continue;
                if (!_steamIdIndex.TryGetValue(otherSteamId, out var otherSteamBans)) continue;
                var activeBan = otherSteamBans.FirstOrDefault(b => b.StatusEnum == BanStatus.ACTIVE);
                if (activeBan == null || !_banCache.TryGetValue(activeBan.Id, out var cachedBan) || cachedBan.StatusEnum != BanStatus.ACTIVE) continue;
                SideEffects++;
                return true;
            }
        }

        if (!_playerIpsCache.TryGetValue(steamId, out var playerIps)) return false;
        foreach (var playerIpRecord in playerIps)
        {
            foreach (var (otherSteamId, otherIpSet) in _playerIpsCache)
            {
                if (otherSteamId == steamId) continue;
                if (otherIpSet.All(record => record.Ip != playerIpRecord.Ip)) continue;
                if (!_steamIdIndex.TryGetValue(otherSteamId, out var otherSteamBans)) continue;
                var activeBan = otherSteamBans.FirstOrDefault(b => b.StatusEnum == BanStatus.ACTIVE);
                if (activeBan == null || !_banCache.TryGetValue(activeBan.Id, out var cachedBan) || cachedBan.StatusEnum != BanStatus.ACTIVE) continue;
                SideEffects++;
                return true;
            }
        }

        return false;
    }
}

/// <summary>PlayerPenaltyManager.IsPenalized @ f544af7, TimeMode 1 branch (reads mutate, ToList per call).</summary>
public static class OldPenalties
{
    public static readonly ConcurrentDictionary<int, Dictionary<CS2_SimpleAdminApi.PenaltyType, List<(DateTime EndDateTime, int Duration, bool Passed)>>> Penalties = new();

    public static bool IsPenalized(int slot, CS2_SimpleAdminApi.PenaltyType penaltyType, DateTime now, out DateTime? endDateTime)
    {
        endDateTime = null;
        if (!Penalties.TryGetValue(slot, out var penaltyDict) || !penaltyDict.TryGetValue(penaltyType, out var penaltiesList)) return false;
        foreach (var penalty in penaltiesList.ToList())
        {
            if (penalty.Duration > 0 && now >= penalty.EndDateTime)
            {
                penaltiesList.Remove(penalty);
                if (penaltiesList.Count == 0) penaltyDict.Remove(penaltyType);
            }
            else if (penalty.Duration == 0 || now < penalty.EndDateTime)
            {
                endDateTime = penalty.EndDateTime;
                return true;
            }
        }

        return false;
    }
}
