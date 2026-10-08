using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;
using Dapper;
using MySqlConnector;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// Real SQL against SQLite and every reachable MySQL/MariaDB scratch server (schema created by the plugin's own
/// migrations). Covers the cache refresh protocol, the stats/history/online-mute queries and charset handling.
/// </summary>
public class DatabaseIntegrationTests
{
    public static IEnumerable<object[]> Engines() => TestDatabases.All();
    public static IEnumerable<object[]> MySqlEngines() => TestDatabases.MySqlOnly();

    private static CS2_SimpleAdminConfig Config(bool multiServer = true, bool multiAccounts = true, bool sqlite = false) =>
        TestConfig.Use(c =>
        {
            c.MultiServerMode = multiServer;
            c.OtherSettings.CheckMultiAccountsByIp = multiAccounts;
            c.OtherSettings.BanType = 1;
            if (sqlite) c.DatabaseConfig.DatabaseType = "SQLite";
        });

    private static async Task<int> InsertBan(TestDatabase db, ulong? steam, string? ip, int serverId = 1, string status = "ACTIVE",
        int duration = 0, string name = "player")
    {
        await using var c = await db.OpenAsync();
        var id = await c.ExecuteScalarAsync<int>(db.Provider.GetAddBanQuery(), new
        {
            playerSteamid = steam, playerName = name, playerIp = ip, adminSteamid = 0, adminName = "Console",
            banReason = "test", duration, ends = DateTime.Now.AddMinutes(duration), created = DateTime.Now, serverid = serverId
        });
        if (status != "ACTIVE")
            await c.ExecuteAsync("UPDATE sa_bans SET status = @status WHERE id = @id", new { status, id });
        return id;
    }

    /// <summary>MySQL bumps updated_at itself (ON UPDATE); SQLite needs it set, as the plugin's own queries do.</summary>
    private static string Touch(TestDatabase db) => db.IsSqlite ? ", updated_at = CURRENT_TIMESTAMP" : "";

    [Theory, MemberData(nameof(Engines))]
    public async Task MigrationsApplyAndAreIdempotent(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        await db.MigrateAsync(); // second run: nothing left to apply, no error
        await using var c = await db.OpenAsync();
        var last = await c.ExecuteScalarAsync<string>("SELECT version FROM sa_migrations ORDER BY id DESC LIMIT 1");
        Assert.StartsWith("01", last);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task FullBuildThenRefreshAppliesInsertsColumnChangesAndDeletions(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(sqlite: db.IsSqlite);
        var keep = await InsertBan(db, 76561198000000001, null);
        var change = await InsertBan(db, 76561198000000002, null);
        var delete = await InsertBan(db, 76561198000000003, null);
        await InsertBan(db, 76561198000000004, null, status: "EXPIRED");

        var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, CancellationToken.None);
        Assert.Equal(3, cache.Snapshot.ActiveCount); // only ACTIVE rows are kept

        await using (var c = await db.OpenAsync())
        {
            // F10: an IP/SteamID change with an unchanged ACTIVE status used to be ignored
            await c.ExecuteAsync($"UPDATE sa_bans SET player_ip = '3.3.3.3'{Touch(db)} WHERE id = @change", new { change });
            // F10: a pure deletion (no other change in the window) was never noticed
            await c.ExecuteAsync("DELETE FROM sa_bans WHERE id = @delete", new { delete });
        }

        var added = await InsertBan(db, 76561198000000005, "4.4.4.4");
        await cache.RefreshCacheAsync(config, CancellationToken.None);

        var s = cache.Snapshot;
        Assert.True(s.ActiveBans.ContainsKey(keep));
        Assert.Equal("3.3.3.3", s.ActiveBans[change].PlayerIp);
        Assert.False(s.ActiveBans.ContainsKey(delete));
        Assert.True(s.ActiveBans.ContainsKey(added));
        Assert.True(s.CheckPlayer(1, "3.3.3.3", 1, 0, DateTime.Now).IsBanned);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task StatusChangeInvisibleToUpdatedAtIsCaughtByTheChecksum(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(sqlite: db.IsSqlite);
        var id = await InsertBan(db, 76561198000000011, null);
        var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, CancellationToken.None);

        await using (var c = await db.OpenAsync())
        {
            // A writer that leaves updated_at in the past (old site code on SQLite, manual edit with explicit value)
            await c.ExecuteAsync("UPDATE sa_bans SET status = 'UNBANNED', updated_at = @old WHERE id = @id",
                new { id, old = new DateTime(2020, 1, 1) });
        }

        await cache.RefreshCacheAsync(config, CancellationToken.None);
        Assert.False(cache.Snapshot.ActiveBans.ContainsKey(id));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task ThousandsOfBansChangedAtOnceArePaged(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(sqlite: db.IsSqlite);
        var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, CancellationToken.None);

        await using (var c = await db.OpenAsync())
        {
            using var tx = c.BeginTransaction();
            for (var i = 0; i < 2500; i++)
                await c.ExecuteAsync(db.Provider.GetAddBanQuery(), new
                {
                    playerSteamid = 76561198100000000UL + (ulong)i, playerName = "p", playerIp = (string?)null, adminSteamid = 0,
                    adminName = "Console", banReason = "bulk", duration = 0, ends = DateTime.Now, created = DateTime.Now, serverid = 1
                }, tx);
            tx.Commit();
        }

        await cache.RefreshCacheAsync(config, CancellationToken.None);
        Assert.Equal(2500, cache.Snapshot.ActiveCount);
    }

    /// <summary>
    /// F10: the IP delta was "ORDER BY used_at DESC LIMIT 300" followed by moving the watermark, so with more than 300
    /// changes in one window the rest were lost. 5000 rows sharing one timestamp exercise keyset paging on ties.
    /// </summary>
    [Theory, MemberData(nameof(Engines))]
    public async Task MoreThan300IpChangesWithIdenticalTimestampsAreAllRead(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(sqlite: db.IsSqlite);
        var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, CancellationToken.None);

        await using (var c = await db.OpenAsync())
        {
            var dbNow = PlayerManager.ToDateTime(await c.ExecuteScalarAsync<object>("SELECT CURRENT_TIMESTAMP"))!.Value;
            using var tx = c.BeginTransaction();
            for (var i = 0; i < 5000; i++)
                await c.ExecuteAsync("INSERT INTO sa_players_ips (steamid, name, address, used_at) VALUES (@s, 'n', @a, @t)",
                    new { s = 76561198200000000L + i, a = (uint)(167772160 + i % 700), t = dbNow }, tx);
            tx.Commit();
        }

        await cache.RefreshCacheAsync(config, CancellationToken.None);
        if (cache.Snapshot.IpAccountCount < 5000) // a pass reads a bounded number of pages; the next one continues
            await cache.RefreshCacheAsync(config, CancellationToken.None);
        Assert.Equal(5000, cache.Snapshot.IpAccountCount);
        Assert.Equal(700, cache.Snapshot.IpAddressCount);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task ForceReloadKeepsServingTheOldSnapshotUntilTheNewOneIsReady(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(sqlite: db.IsSqlite);
        var id = await InsertBan(db, 76561198000000021, null);
        var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, CancellationToken.None);
        var before = cache.Snapshot;

        var reload = cache.ForceReInitializeCacheAsync(config, CancellationToken.None);
        // Readers during the rebuild see a complete snapshot (old or new), never an empty/cleared one
        Assert.True(cache.Snapshot.ActiveBans.ContainsKey(id));
        await reload;
        Assert.NotSame(before, cache.Snapshot);
        Assert.True(cache.Snapshot.ActiveBans.ContainsKey(id));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task ConnectStatsCountHistoryNotActiveCacheEntries(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        Config(multiServer: false, sqlite: db.IsSqlite);
        const ulong steam = 76561198000000031;
        await InsertBan(db, steam, null, status: "EXPIRED");
        await InsertBan(db, steam, null, status: "UNBANNED");
        await InsertBan(db, steam, null, serverId: 2, status: "EXPIRED");
        await using (var c = await db.OpenAsync())
        {
            foreach (var (type, server) in new[] { ("GAG", 1), ("GAG", 1), ("MUTE", 1), ("SILENCE", 2) })
                await c.ExecuteAsync(db.Provider.GetAddMuteQuery(true), new
                {
                    playerSteamid = steam, playerName = "p", adminSteamid = 0, adminName = "Console", muteReason = "r",
                    duration = 5, ends = DateTime.Now.AddMinutes(5), created = DateTime.Now, type, serverid = server
                });
            await c.ExecuteAsync(db.Provider.GetAddWarnQuery(true), new
            {
                playerSteamid = steam, playerName = "p", adminSteamid = 0, adminName = "Console", warnReason = "w",
                duration = 5, ends = DateTime.Now.AddMinutes(5), created = DateTime.Now, serverid = 1
            });
        }

        var mutes = new MuteManager(db.Provider);
        // Penalties are network-wide: the SILENCE and the EXPIRED ban issued on server 2 count as well
        var all = await mutes.GetPlayerPenaltyStatsAsync(steam, CancellationToken.None);
        Assert.Equal((3, 1, 2, 1, 1), ((int)all.TotalBans, (int)all.TotalMutes, (int)all.TotalGags, (int)all.TotalSilences, (int)all.TotalWarns));

        // F12: GetPlayerMutes used the unmute query (needs @pattern/@muteType), threw, and returned zeros
        await using (var c = await db.OpenAsync())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => c.QuerySingleAsync<(int, int, int)>(
                db.Provider.GetRetrieveMutesQuery(), new { PlayerSteamID = steam }));
        }

        CS2_SimpleAdmin.GlobalServerId = 1;
        var stats = await mutes.GetPlayerMutes(new CS2_SimpleAdminApi.PlayerInfo(1, 1,
            new CounterStrikeSharp.API.Modules.Entities.SteamID(steam), "p", null));
        CS2_SimpleAdmin.GlobalServerId = null;
        Assert.Equal((1, 2, 1), stats); // every server, even with the legacy MultiServerMode=false
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task HistoryPagesAreStableWithTiedTimestampsAndFilteredInSql(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        Config(sqlite: db.IsSqlite);
        CS2_SimpleAdmin.DatabaseProvider = db.Provider;
        const ulong steam = 76561198000000041;
        var created = new DateTime(2026, 1, 1, 12, 0, 0);
        await using (var c = await db.OpenAsync())
        {
            for (var i = 0; i < 40; i++)
            {
                await c.ExecuteAsync("INSERT INTO sa_bans (player_steamid, player_name, admin_steamid, admin_name, reason, duration, ends, created, server_id) VALUES (@steam, 'p', 0, 'Console', 'b', 0, @created, @created, 1)", new { steam, created });
                await c.ExecuteAsync("INSERT INTO sa_mutes (player_steamid, player_name, admin_steamid, admin_name, reason, duration, ends, created, type, server_id) VALUES (@steam, 'p', 0, 'Console', 'm', 0, @created, @created, @type, 1)", new { steam, created, type = i % 2 == 0 ? "GAG" : "MUTE" });
                await c.ExecuteAsync("INSERT INTO sa_warns (player_steamid, player_name, admin_steamid, admin_name, reason, duration, ends, created, server_id) VALUES (@steam, 'p', 0, 'Console', 'w', 0, @created, @created, 1)", new { steam, created });
            }
        }

        var seen = new HashSet<(string, int)>();
        var pages = 0;
        for (var page = 1; ; page++)
        {
            var result = await PlayerManager.GetPenaltyHistoryPage(steam, null, page, 50, CancellationToken.None);
            Assert.Equal(120, result.Total);
            foreach (var row in result.Rows) Assert.True(seen.Add((row.Type, row.Id)), $"duplicate {row.Type}#{row.Id}");
            pages++;
            if (page >= result.Pages) break;
        }

        Assert.Equal(3, pages);
        Assert.Equal(120, seen.Count);

        var gags = await PlayerManager.GetPenaltyHistoryPage(steam, "gags", 1, 100, CancellationToken.None);
        Assert.Equal(20, gags.Total);
        Assert.All(gags.Rows, r => Assert.Equal("GAG", r.Type));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task OnlineTimeMutesAreCreditedSetBased(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        Config(sqlite: db.IsSqlite);
        var players = Enumerable.Range(0, 100).Select(i => 76561198300000000UL + (ulong)i).ToList();
        await using (var c = await db.OpenAsync())
        {
            foreach (var steam in players)
                await c.ExecuteAsync(db.Provider.GetAddMuteQuery(true), new
                {
                    playerSteamid = steam, playerName = "p", adminSteamid = 0, adminName = "Console", muteReason = "r",
                    duration = steam % 2 == 0 ? 1 : 10, ends = new DateTime(2030, 1, 1, 0, 0, 0), created = DateTime.Now.AddHours(-1), type = "MUTE", serverid = 1
                });
        }

        var updatesBefore = await ComUpdate(db);
        var mutes = new MuteManager(db.Provider);
        var windowEnd = DateTime.Now;
        var credits = players.Select(p => new OnlineCredit(p, 1, OnlineCredit.TicksPerMinute, windowEnd.AddMinutes(-1), windowEnd)).ToList();
        var expired = await mutes.CheckOnlineModeMutesAsync(credits, CancellationToken.None);
        var updates = await ComUpdate(db) - updatesBefore;

        Assert.Equal(50, expired.Count); // duration 1 reached after one credited minute
        Assert.All(expired, e => Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0), e.Ends));
        if (!db.IsSqlite) Assert.Equal(2, updates); // ceil(100/64) compare-and-set statements; the original code issued 100 (+100 SELECTs)

        await using var check = await db.OpenAsync();
        Assert.Equal(100, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sa_mutes WHERE passed = 1"));
    }

    private static async Task<long> ComUpdate(TestDatabase db)
    {
        if (db.IsSqlite) return -1;
        await using var c = new MySqlConnection(db.ServerConnectionString);
        await c.OpenAsync();
        var row = await c.QuerySingleAsync<(string, string)>("SHOW GLOBAL STATUS LIKE 'Com_update'");
        return long.Parse(row.Item2);
    }

    [SkippableFact]
    public void RequiredMySqlServersAreReachable()
    {
        Skip.IfNot(TestDatabases.MySqlRequired, "SA_REQUIRE_MYSQL is not set: SQLite-only run (does not verify MySQL/MariaDB)");
        TestDatabases.RequiredServersAreReachable();
    }

    [SkippableTheory, MemberData(nameof(MySqlEngines))]
    public async Task CyrillicRoundTripsAndCollationSurvivesPoolReset(string engine)
    {
        TestDatabases.SkipIfNoServer(engine);
        await using var db = await TestDatabases.CreateAsync(engine);
        var id = await InsertBan(db, 76561198000000051, null, name: "Вася Пупкин ёЁ");
        for (var i = 0; i < 3; i++) // the 2nd/3rd checkout reuse a pooled (reset) connection
        {
            await using var c = await db.OpenAsync();
            Assert.Equal("utf8mb4_general_ci", await c.ExecuteScalarAsync<string>("SELECT @@collation_connection"));
            Assert.Equal("Вася Пупкин ёЁ", await c.ExecuteScalarAsync<string>("SELECT player_name FROM sa_bans WHERE id = @id", new { id }));
            Assert.Equal(1, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sa_bans WHERE player_name = 'вася пупкин ЕЕ'"));
        }
    }

    [Fact]
    public void SqliteProviderAsyncIsSynchronous()
    {
        // F16: System.Data.SQLite does not override the ADO.NET async methods → base implementations run synchronously.
        // This is why SQLite gets a single dedicated DB worker and is never called from a game callback.
        var type = typeof(System.Data.SQLite.SQLiteCommand);
        var method = type.GetMethod("ExecuteDbDataReaderAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.Equal(typeof(System.Data.Common.DbCommand), method!.DeclaringType);
        Assert.Equal(typeof(System.Data.Common.DbConnection),
            typeof(System.Data.SQLite.SQLiteConnection).GetMethod("OpenAsync", [typeof(CancellationToken)])!.DeclaringType);
    }
}
