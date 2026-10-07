using System.Collections.Immutable;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;

namespace CS2_SimpleAdmin.Tests;

public class BanCacheSnapshotTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0);

    private static BanRecord Ban(int id, ulong? steam = null, string? ip = null, string status = "ACTIVE", int duration = 0,
        DateTime? ends = null, DateTime? created = null) =>
        new()
        {
            Id = id, PlayerSteamId = steam, PlayerIp = ip, Status = status, Duration = duration,
            Ends = ends ?? (duration == 0 ? created ?? Now.AddDays(-1) : Now.AddMinutes(duration)),
            Created = created ?? Now.AddDays(-1)
        };

    private static IpHistoryRow Ip(ulong steam, string ip, DateTime? usedAt = null) =>
        new() { Steamid = (long)steam, Address = IpHelper.IpToUint(ip), Used_at = usedAt ?? Now.AddDays(-1), Name = "n" };

    private static BanCacheSnapshot Build(IEnumerable<BanRecord> bans, IEnumerable<IpHistoryRow>? history = null, params string[] ignored)
    {
        var (ips, accounts) = BanCacheSnapshot.BuildIpIndexes(history ?? [], "Unknown");
        return BanCacheSnapshot.Create(bans, ips, accounts, ignored.Select(IpHelper.IpToUint));
    }

    [Fact]
    public void SteamBanAndBanTypeRules()
    {
        var s = Build([Ban(1, steam: 10), Ban(2, ip: "1.1.1.1")]);
        Assert.True(s.CheckPlayer(10, null, 0, 0, Now).IsBanned);
        Assert.False(s.CheckPlayer(11, "1.1.1.1", 0, 0, Now).IsBanned); // BanType 0: no IP bans
        Assert.True(s.CheckPlayer(11, "1.1.1.1", 1, 0, Now).IsBanned);
    }

    [Fact]
    public void IgnoredIpsAreNeverIpBanned()
    {
        var s = Build([Ban(2, ip: "1.1.1.1")], null, "1.1.1.1");
        Assert.False(s.CheckPlayer(11, "1.1.1.1", 1, 0, Now).IsBanned);
        Assert.False(s.CheckPlayerOrAnyIp(11, "1.1.1.1", 1, 0, true, Now).IsBanned);
    }

    [Fact]
    public void TimedBanPastItsEndIsNotActiveEvenBeforeTheSqlExpiry()
    {
        // F06: refresh could keep an ACTIVE row that already reached ends for another 61 s
        var s = Build([Ban(1, steam: 10, duration: 5, ends: Now.AddSeconds(-1))]);
        Assert.False(s.CheckPlayer(10, null, 0, 0, Now).IsBanned);
        Assert.True(s.CheckPlayer(10, null, 0, 0, Now.AddSeconds(-2)).IsBanned);
    }

    [Fact]
    public void ExpireOldIpBansUsesTheBanCreationDate()
    {
        // §5: Created was not selected, so it was DateTime.MinValue and every direct IP ban looked "older than N days"
        var recent = Build([Ban(1, steam: 99, ip: "2.2.2.2", created: Now.AddDays(-2))]);
        var old = Build([Ban(1, steam: 99, ip: "2.2.2.2", created: Now.AddDays(-40))]);
        Assert.True(recent.CheckPlayerOrAnyIp(10, "2.2.2.2", 1, 30, true, Now).IsBanned);
        Assert.False(old.CheckPlayerOrAnyIp(10, "2.2.2.2", 1, 30, true, Now).IsBanned);
        Assert.True(old.CheckPlayerOrAnyIp(10, "2.2.2.2", 1, 0, true, Now).IsBanned); // 0 = never expire
    }

    [Fact]
    public void SharedAndHistoricIpsFindBannedOtherAccounts()
    {
        var history = new[]
        {
            Ip(20, "5.5.5.5"), // banned account used 5.5.5.5
            Ip(10, "5.5.5.5"), // our player used it before
            Ip(30, "6.6.6.6")
        };
        var s = Build([Ban(1, steam: 20)], history);

        Assert.Equal(BanMatch.SharedIp, s.CheckPlayerOrAnyIp(31, "5.5.5.5", 1, 0, true, Now).Match);
        Assert.Equal(BanMatch.SharedIpHistory, s.CheckPlayerOrAnyIp(10, "7.7.7.7", 1, 0, true, Now).Match);
        Assert.False(s.CheckPlayerOrAnyIp(10, "7.7.7.7", 1, 0, false, Now).IsBanned); // multi-account history disabled
        Assert.False(s.CheckPlayerOrAnyIp(30, "6.6.6.6", 1, 0, true, Now).IsBanned);
    }

    [Fact]
    public void GetAccountsByIpUsesTheReverseIndex()
    {
        var s = Build([], [Ip(1, "9.9.9.9"), Ip(2, "9.9.9.9"), Ip(3, "8.8.8.8")]);
        Assert.Equal([1UL, 2UL], s.GetAccountsByIp(IpHelper.IpToUint("9.9.9.9")).Select(a => a.SteamId).Order());
    }

    [Fact]
    public void PublishedSnapshotsAreNotChangedByLaterGenerations()
    {
        // F04: RemoveAll/HashSet edits/BanRecord setters used to mutate what readers were iterating
        var v1 = Build([Ban(1, steam: 10)], [Ip(10, "1.1.1.1")]);
        var v2 = v1.WithBans([Ban(1, steam: 10, status: "UNBANNED")]).WithIpHistory([Ip(10, "2.2.2.2", Now)], "Unknown");
        Assert.True(v1.CheckPlayer(10, null, 0, 0, Now).IsBanned);
        Assert.Single(v1.IpsBySteamId[10]);
        Assert.False(v2.CheckPlayer(10, null, 0, 0, Now).IsBanned);
        Assert.Equal(2, v2.IpsBySteamId[10].Length);
    }

    [Fact]
    public void IndexesAreBuiltFromTheSameActiveSet()
    {
        var s = Build([Ban(1, steam: 10, ip: "1.1.1.1"), Ban(2, steam: 11, ip: "1.1.1.1", status: "EXPIRED")]);
        Assert.Equal(1, s.ActiveCount);
        Assert.Single(s.ByIp[IpHelper.IpToUint("1.1.1.1")]);
        Assert.False(s.BySteamId.ContainsKey(11));
        Assert.Equal(1, s.ActiveIdSum);
    }

    /// <summary>
    /// Reverse-index lookup must give the same answer as the old full scan of the IP history (same rules).
    /// Randomised over many datasets.
    /// </summary>
    [Fact]
    public void ReverseIndexMatchesFullScanReference()
    {
        var rnd = new Random(12345);
        for (var round = 0; round < 200; round++)
        {
            var accounts = rnd.Next(5, 60);
            var ipsPool = Enumerable.Range(0, rnd.Next(3, 25)).Select(i => $"10.0.{rnd.Next(0, 3)}.{i}").ToArray();
            var history = new List<IpHistoryRow>();
            for (var a = 1; a <= accounts; a++)
                for (var k = rnd.Next(0, 4); k > 0; k--)
                    history.Add(Ip((ulong)a, ipsPool[rnd.Next(ipsPool.Length)], Now.AddDays(-rnd.Next(0, 60))));
            var bans = new List<BanRecord>();
            for (var b = 1; b <= rnd.Next(0, 8); b++)
                bans.Add(Ban(b, steam: rnd.Next(2) == 0 ? (ulong)rnd.Next(1, accounts + 1) : null,
                    ip: rnd.Next(3) == 0 ? ipsPool[rnd.Next(ipsPool.Length)] : null,
                    status: rnd.Next(5) == 0 ? "EXPIRED" : "ACTIVE",
                    created: Now.AddDays(-rnd.Next(0, 60))));
            var ignored = rnd.Next(4) == 0 ? new[] { ipsPool[0] } : [];
            var expireDays = rnd.Next(3) == 0 ? 30 : 0;
            var snapshot = Build(bans, history, ignored);

            for (var player = 1; player <= accounts; player++)
            {
                var ip = ipsPool[rnd.Next(ipsPool.Length)];
                var expected = Reference(bans, history, ignored, (ulong)player, ip, expireDays);
                var actual = snapshot.CheckPlayerOrAnyIp((ulong)player, ip, 1, expireDays, true, Now).IsBanned;
                Assert.True(expected == actual, $"round {round} player {player} ip {ip}: expected {expected}");
            }
        }
    }

    /// <summary>The original algorithm (full scans over every account's IP set), with the same rules applied.</summary>
    private static bool Reference(List<BanRecord> bans, List<IpHistoryRow> history, string[] ignoredIps, ulong steamId,
        string ip, int expireDays)
    {
        bool ActiveSteam(ulong s) => bans.Any(b => b.PlayerSteamId == s && b.IsEffectivelyActive(Now));
        if (ActiveSteam(steamId)) return true;
        var ipUint = IpHelper.IpToUint(ip);
        if (ignoredIps.Contains(ip)) return false;
        var cutoff = Now.AddDays(-expireDays);
        if (bans.Any(b => b.IsEffectivelyActive(Now) && b.PlayerIp != null && IpHelper.IpToUint(b.PlayerIp) == ipUint &&
                          (expireDays <= 0 || (b.Ends > cutoff && b.Created >= cutoff))))
            return true;

        var perAccount = history.GroupBy(h => (ulong)h.Steamid).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (other, rows) in perAccount)
            if (other != steamId && rows.Any(r => r.Address == ipUint) && ActiveSteam(other)) return true;

        if (!perAccount.TryGetValue(steamId, out var own)) return false;
        foreach (var record in own.GroupBy(r => r.Address).Select(g => g.MaxBy(r => r.Used_at)!))
        {
            if (expireDays > 0 && record.Used_at <= cutoff) continue;
            foreach (var (other, rows) in perAccount)
                if (other != steamId && rows.Any(r => r.Address == record.Address) && ActiveSteam(other)) return true;
        }

        return false;
    }
}
