using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// R2: the incrementally refreshed IP-history cache must answer exactly like a full rebuild from SQL after expiry,
/// renewal, ordinary deletion and an external DELETE — in both directions of the index — and lookups must honour the
/// owner's own link age. No pass scans the whole history (the prune sweep is bounded per refresh).
/// </summary>
public class R2_IpHistoryConsistencyTests
{
    public static IEnumerable<object[]> Engines() => TestDatabases.All();

    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0);

    private static CS2_SimpleAdminConfig Config(string engine, int expireDays = 30) =>
        TestConfig.Use(c =>
        {
            c.DatabaseConfig.DatabaseType = engine == "SQLite" ? "SQLite" : "MySQL";
            c.MultiServerMode = true;
            c.OtherSettings.CheckMultiAccountsByIp = true;
            c.OtherSettings.BanType = 1;
            c.OtherSettings.ExpireOldIpBans = expireDays;
        });

    private static async Task Exec(TestDatabase db, string sql, object? args = null)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync(sql, args);
    }

    private static Task AddBan(TestDatabase db, ulong steam, DateTime now) =>
        Exec(db, "INSERT INTO sa_bans(player_steamid,admin_steamid,admin_name,reason,duration,ends,created,status) VALUES (@steam,0,'Console','probe',0,@now,@now,'ACTIVE')",
            new { steam, now });

    private static Task AddIp(TestDatabase db, ulong steam, string ip, DateTime usedAt) =>
        Exec(db, "INSERT INTO sa_players_ips(steamid,address,name,used_at) VALUES (@steam,@ip,'n',@usedAt)",
            new { steam, ip = IpHelper.IpToUint(ip), usedAt });

    // ---- ported review probe (R2) ----
    [Theory, MemberData(nameof(Engines))]
    public async Task DeletedIpOwnershipMustDisappearFromPublishedCache(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine);
        var now = DateTime.UtcNow;
        await AddBan(db, 2, now);
        await AddIp(db, 2, "10.20.30.40", now.AddDays(-100));
        using var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, null, default);
        await Exec(db, "DELETE FROM sa_players_ips WHERE used_at <= @cutoff", new { cutoff = now.AddDays(-30) });
        await cache.RefreshCacheAsync(config, null, default);
        using var rebuilt = new CacheManager();
        await rebuilt.InitializeCacheAsync(config, null, default);
        Assert.False(rebuilt.CheckBan(config, 1, "10.20.30.40", now).IsBanned);
        Assert.False(cache.CheckBan(config, 1, "10.20.30.40", now).IsBanned,
            "Incremental cache still bans a different account using IP ownership already deleted by SQL expiry");
    }

    private static readonly string[] Ips = ["10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6", "10.0.0.99"];

    /// <summary>Every observable answer of the cache for a grid of accounts × addresses.</summary>
    private static List<string> Observe(CacheManager cache, CS2_SimpleAdminConfig config, DateTime now)
    {
        var lines = new List<string>();
        for (ulong steam = 1; steam <= 9; steam++)
            foreach (var ip in Ips)
            {
                var r = cache.CheckBan(config, steam, ip, now);
                lines.Add($"check {steam} {ip}: {r.IsBanned} {r.Match} {r.Ban?.Id}");
            }

        foreach (var ip in Ips)
            lines.Add($"accounts {ip}: " + string.Join(",", cache.GetAccountsByIp(ip, now, config.OtherSettings.ExpireOldIpBans)
                .Select(a => a.SteamId).Order()));
        return lines;
    }

    private static async Task<CacheManager> Rebuild(CS2_SimpleAdminConfig config)
    {
        var rebuilt = new CacheManager();
        await rebuilt.InitializeCacheAsync(config, null, default);
        return rebuilt;
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task RefreshEqualsRebuildAfterExpiryRenewalAndDeletion(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine);
        var now = DateTime.UtcNow;

        // 2: permanent ban; its link to .1 is 100 days old (will be expired), its link to .2 is recent
        await AddBan(db, 2, now);
        await AddIp(db, 2, "10.0.0.1", now.AddDays(-100));
        await AddIp(db, 2, "10.0.0.2", now.AddDays(-5));
        // 3: shares .3 with the banned 4; its own link is old, then renewed (used again) later
        await AddBan(db, 4, now);
        await AddIp(db, 4, "10.0.0.3", now.AddDays(-2));
        await AddIp(db, 3, "10.0.0.3", now.AddDays(-80));
        // 5 and 6 share .4: 6 is banned, 5 is innocent and stays so
        await AddBan(db, 6, now);
        await AddIp(db, 6, "10.0.0.4", now.AddDays(-1));
        await AddIp(db, 5, "10.0.0.4", now.AddDays(-3));
        // 8 is banned; 7 shares .5; 8's row is deleted from SQL behind the cache's back
        await AddBan(db, 8, now);
        await AddIp(db, 8, "10.0.0.5", now.AddDays(-2));
        await AddIp(db, 7, "10.0.0.5", now.AddDays(-2));

        using var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, null, default);

        // expiry job, renewal, external delete
        await new BanManager(db.Provider).ExpireOldBans();
        await Exec(db, "UPDATE sa_players_ips SET used_at = @used WHERE steamid = 3 AND address = @ip",
            new { used = now, ip = IpHelper.IpToUint("10.0.0.3") });
        await Exec(db, "DELETE FROM sa_players_ips WHERE steamid = 8");

        // enough refreshes for: delta reads, the prune sweep, and a confirmed checksum mismatch (needs two checks)
        for (var i = 0; i < CacheManager.IpChecksumEveryRefreshes * 2 + 2; i++)
            await cache.RefreshCacheAsync(config, null, default);

        using var rebuilt = await Rebuild(config);
        Assert.Equal(Observe(rebuilt, config, now), Observe(cache, config, now));
        Assert.Equal(rebuilt.Snapshot.IpAccountCount, cache.Snapshot.IpAccountCount);
        Assert.Equal(rebuilt.Snapshot.IpAddressCount, cache.Snapshot.IpAddressCount);

        // spot checks of the intended semantics (not just "same as rebuild")
        Assert.False(cache.CheckBan(config, 1, "10.0.0.1", now).IsBanned);   // expired link of banned 2
        Assert.True(cache.CheckBan(config, 1, "10.0.0.2", now).IsBanned);    // recent link of banned 2
        Assert.True(cache.CheckBan(config, 3, "10.0.0.3", now).IsBanned);    // renewed link shares with banned 4
        Assert.True(cache.CheckBan(config, 5, "10.0.0.99", now).IsBanned);   // 5 shares .4 with banned 6 (history match)
        Assert.False(cache.CheckBan(config, 9, "10.0.0.99", now).IsBanned);  // an uninvolved account
        Assert.False(cache.CheckBan(config, 7, "10.0.0.5", now).IsBanned);   // banned 8's link was deleted externally
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task ExternalDeleteWithoutAnyTimeRuleIsDetectedAndRebuilt(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine, expireDays: 0); // expiry disabled: only the checksum can notice
        var now = DateTime.UtcNow;
        await AddBan(db, 2, now);
        await AddIp(db, 2, "10.0.0.2", now.AddDays(-400));
        using var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, null, default);
        Assert.True(cache.CheckBan(config, 1, "10.0.0.2", now).IsBanned); // kept indefinitely when expiry is off

        await Exec(db, "DELETE FROM sa_players_ips");
        for (var i = 0; i < CacheManager.IpChecksumEveryRefreshes * 2 + 2; i++)
            await cache.RefreshCacheAsync(config, null, default);

        Assert.False(cache.CheckBan(config, 1, "10.0.0.2", now).IsBanned);
        Assert.Equal(0, cache.Snapshot.IpAddressCount);
        Assert.True(cache.IpRebuilds >= 1);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task WithExpiryDisabledOldLinksAreKeptForever(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine, expireDays: 0);
        var now = DateTime.UtcNow;
        await AddBan(db, 2, now);
        await AddIp(db, 2, "10.0.0.2", now.AddDays(-4000));
        using var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, null, default);
        for (var i = 0; i < 40; i++) await cache.RefreshCacheAsync(config, null, default);

        Assert.True(cache.CheckBan(config, 1, "10.0.0.2", now).IsBanned);
        Assert.Equal(1, cache.Snapshot.IpAddressCount);
        Assert.Equal(0, cache.IpRebuilds); // an untouched table is never "rebuilt"
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task OrdinaryGrowthNeverTriggersARebuild(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine);
        var now = DateTime.UtcNow;
        using var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, null, default);
        for (var i = 0; i < CacheManager.IpChecksumEveryRefreshes * 3; i++)
        {
            // stamped by the database clock, as the plugin's own upsert does (CURRENT_TIMESTAMP); a literal UTC time would be
            // hours behind a MySQL server in another time zone and legitimately invisible to the incremental reader
            await Exec(db, "INSERT INTO sa_players_ips(steamid,address,name,used_at) VALUES (@steam,@ip,'n',CURRENT_TIMESTAMP)",
                new { steam = (ulong)(100 + i), ip = IpHelper.IpToUint($"10.1.0.{i % 200 + 1}") });
            await cache.RefreshCacheAsync(config, null, default);
        }

        Assert.Equal(0, cache.IpRebuilds);
    }

    // ---- pure index / snapshot rules ----
    private static IpHistoryRow Row(ulong steam, string ip, DateTime usedAt) =>
        new() { Steamid = (long)steam, Address = IpHelper.IpToUint(ip), Used_at = usedAt, Name = "n" };

    private static BanRecord Ban(int id, ulong steam) =>
        new() { Id = id, PlayerSteamId = steam, Status = "ACTIVE", Duration = 0, Created = Now.AddDays(-1), Ends = Now.AddDays(-1) };

    private static BanCacheSnapshot Snapshot(IEnumerable<BanRecord> bans, IEnumerable<IpHistoryRow> rows) =>
        BanCacheSnapshot.Create(bans, BanCacheSnapshot.BuildIpIndexes(rows, "Unknown"), []);

    [Fact]
    public void CutoffBoundaryIsExclusiveForTheOwnersOwnLink()
    {
        var cutoff = Now.AddDays(-30);
        var atCutoff = Snapshot([Ban(1, 2)], [Row(2, "9.9.9.9", cutoff)]);
        var justInside = Snapshot([Ban(1, 2)], [Row(2, "9.9.9.9", cutoff.AddSeconds(1))]);
        var justOutside = Snapshot([Ban(1, 2)], [Row(2, "9.9.9.9", cutoff.AddSeconds(-1))]);

        Assert.False(atCutoff.CheckPlayerOrAnyIp(1, "9.9.9.9", 1, 30, true, Now).IsBanned);    // used_at <= cutoff is deleted by SQL
        Assert.True(justInside.CheckPlayerOrAnyIp(1, "9.9.9.9", 1, 30, true, Now).IsBanned);
        Assert.False(justOutside.CheckPlayerOrAnyIp(1, "9.9.9.9", 1, 30, true, Now).IsBanned);
        Assert.True(atCutoff.CheckPlayerOrAnyIp(1, "9.9.9.9", 1, 0, true, Now).IsBanned);       // expiry disabled: kept forever
    }

    [Fact]
    public void TheOwnersLinkAgeDecidesNotTheCurrentPlayersOrAnotherOwners()
    {
        // 2 (banned) used the IP long ago; 3 (not banned) uses it now. Only 2's link age matters for 2's ban.
        var s = Snapshot([Ban(1, 2)], [Row(2, "9.9.9.9", Now.AddDays(-90)), Row(3, "9.9.9.9", Now.AddDays(-1))]);
        Assert.False(s.CheckPlayerOrAnyIp(1, "9.9.9.9", 1, 30, true, Now).IsBanned);
        Assert.Equal([3UL], s.GetAccountsByIp(IpHelper.IpToUint("9.9.9.9"), Now, 30).Select(a => a.SteamId));
    }

    [Fact]
    public void PruneRemovesStaleLinksFromBothDirectionsAndMatchesARebuild()
    {
        var rows = new List<IpHistoryRow>
        {
            Row(1, "1.1.1.1", Now.AddDays(-100)), Row(1, "2.2.2.2", Now.AddDays(-1)),
            Row(2, "1.1.1.1", Now.AddDays(-2)), Row(3, "3.3.3.3", Now.AddDays(-200)),
        };
        var index = IpHistoryIndex.Build(rows, "Unknown");
        index = index.With([Row(4, "1.1.1.1", Now.AddDays(-90))], "Unknown"); // goes through the overlay
        var cutoff = Now.AddDays(-30);

        var pruned = index.Prune(cutoff, index.AccountIds());
        var rebuilt = IpHistoryIndex.Build(rows.Append(Row(4, "1.1.1.1", Now.AddDays(-90))).Where(r => r.Used_at > cutoff).ToList(), "Unknown");

        Assert.Equal(rebuilt.AccountCount, pruned.AccountCount);
        Assert.Equal(rebuilt.AddressCount, pruned.AddressCount);
        Assert.False(pruned.TryGetIps(3, out _));   // forward direction: account gone
        Assert.False(pruned.TryGetIps(4, out _));
        Assert.False(pruned.TryGetAccounts(IpHelper.IpToUint("3.3.3.3"), out _)); // reverse direction: address gone
        Assert.True(pruned.TryGetAccounts(IpHelper.IpToUint("1.1.1.1"), out var owners));
        Assert.Equal([2UL], owners);
        Assert.Equal(rebuilt.Checksum(cutoff), pruned.Checksum(cutoff));
        Assert.Equal(rebuilt.Checksum(DateTime.MinValue), pruned.Checksum(DateTime.MinValue)); // nothing fresh was lost
        Assert.Equal(pruned.Checksum(DateTime.MinValue), pruned.Compact().Checksum(DateTime.MinValue));
        Assert.Equal(0, pruned.Compact().OverlayCount);
    }

    [Fact]
    public void PruneIsBoundedToTheGivenSliceOfAccounts()
    {
        var rows = Enumerable.Range(1, 50).Select(i => Row((ulong)i, $"5.5.5.{i}", Now.AddDays(-100))).ToList();
        var index = IpHistoryIndex.Build(rows, "Unknown");
        var pruned = index.Prune(Now.AddDays(-30), new ulong[] { 1, 2, 3 });
        Assert.Equal(47, pruned.AccountCount);
        Assert.True(pruned.TryGetIps(4, out _)); // outside the slice: untouched, no full scan
    }

    [Fact]
    public void PrunedBaseEntryIsNotResurrectedByALaterMerge()
    {
        var index = IpHistoryIndex.Build([Row(1, "1.1.1.1", Now.AddDays(-100))], "Unknown");
        var pruned = index.Prune(Now.AddDays(-30), new ulong[] { 1 });
        var renewed = pruned.With([Row(1, "1.1.1.1", Now)], "Unknown"); // same link used again
        Assert.True(renewed.TryGetIps(1, out var ips));
        Assert.Single(ips);
        Assert.True(renewed.TryGetAccounts(IpHelper.IpToUint("1.1.1.1"), out var owners));
        Assert.Equal([1UL], owners);
        Assert.Equal(1, renewed.AccountCount);
        Assert.Equal(1, renewed.AddressCount);
    }
}
