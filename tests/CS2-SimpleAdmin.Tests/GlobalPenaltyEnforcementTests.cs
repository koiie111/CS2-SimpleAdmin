using CS2_SimpleAdmin.Database;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;
using CS2_SimpleAdminApi;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>Row builders shared by the penalty-enforcement tests (plain SQL, so they run on SQLite and MySQL/MariaDB alike).</summary>
internal static class Net
{
    public static async Task<long> Ban(TestDatabase db, ulong steam, string? ip = null, int? serverId = 2, string status = "ACTIVE",
        int duration = 0, DateTime? ends = null, DateTime? created = null)
    {
        await using var c = await db.OpenAsync();
        var id = await c.ExecuteScalarAsync<long>(db.Provider.GetAddBanQuery(), new
        {
            playerSteamid = steam, playerName = "p", playerIp = ip, adminSteamid = 0, adminName = "Console", banReason = "test",
            duration, ends = ends ?? Time.ActualDateTime().AddMinutes(duration), created = created ?? Time.ActualDateTime(), serverid = serverId
        });
        if (status != "ACTIVE")
            await c.ExecuteAsync("UPDATE sa_bans SET status = @status WHERE id = @id", new { status, id });
        return id;
    }

    public static async Task<long> Mute(TestDatabase db, ulong steam, string type = "MUTE", int duration = 0, DateTime? ends = null,
        int? serverId = 2, string status = "ACTIVE", int? passed = null, DateTime? created = null)
    {
        await using var c = await db.OpenAsync();
        var id = await c.ExecuteScalarAsync<long>(db.Provider.GetAddMuteQuery(true), new
        {
            playerSteamid = steam, playerName = "p", adminSteamid = 0, adminName = "Console", muteReason = "r", duration,
            ends = ends ?? Time.ActualDateTime().AddMinutes(duration), created = created ?? Time.ActualDateTime(), type, serverid = serverId
        });
        if (status != "ACTIVE" || passed != null)
            await c.ExecuteAsync("UPDATE sa_mutes SET status = @status, passed = @passed WHERE id = @id", new { status, passed, id });
        return id;
    }

    public static async Task Exec(TestDatabase db, string sql, object? args = null)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync(sql, args);
    }

    /// <summary>Whole seconds: DATETIME columns round, the boundary tests must compare equal values.</summary>
    public static DateTime Now()
    {
        var n = Time.ActualDateTime();
        return new DateTime(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second, n.Kind);
    }
}

/// <summary>
/// Penalties are network-wide and the shared database is authoritative: where a row was issued (server_id: another server,
/// NULL, 0, stale), MultiServerMode, BanType, IPs and the freshness of the local cache do not change who is punished.
/// </summary>
public class GlobalPenaltyEnforcementTests
{
    public static IEnumerable<object[]> Engines() => TestDatabases.All();

    public static IEnumerable<object[]> EnginesAndLegacyScope()
    {
        foreach (var e in TestDatabases.All())
        {
            yield return [e[0], true];
            yield return [e[0], false];
        }
    }

    private static CS2_SimpleAdminConfig Config(string engine, bool multiServer = true, int banType = 1, bool multi = false, int timeMode = 1,
        params string[] ignoredIps) =>
        TestConfig.Use(c =>
        {
            c.MultiServerMode = multiServer;
            c.DatabaseConfig.DatabaseType = engine == "SQLite" ? "SQLite" : "MySQL";
            c.OtherSettings.BanType = banType;
            c.OtherSettings.CheckMultiAccountsByIp = multi;
            c.OtherSettings.TimeMode = timeMode;
            c.OtherSettings.IgnoredIps = ignoredIps.ToList();
        });

    private static readonly int?[] ServerIds = [1, 2, null, 0, 987654];

    // ---------------------------------------------------------------- 1. issued on A, valid on B

    [Theory, MemberData(nameof(EnginesAndLegacyScope))]
    public async Task ABanFromAnotherServerOrWithAnyServerIdBansHere(string engine, bool multiServerMode)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine, multiServerMode);
        var cache = new CacheManager();
        var now = Net.Now();
        var steam = 76561198100000000UL;
        foreach (var serverId in ServerIds)
        {
            steam++;
            await Net.Ban(db, steam, serverId: serverId);
            var result = await BanDecider.DecideAsync(db.Provider, steam, now, default);
            Assert.True(result.IsBanned, $"server_id={serverId?.ToString() ?? "NULL"} MultiServerMode={multiServerMode}");
            Assert.Equal(BanMatch.SteamId, result.Match);
        }
    }

    [Theory, MemberData(nameof(EnginesAndLegacyScope))]
    public async Task MutesGagsAndSilencesFromAnyServerAreActiveHere(string engine, bool multiServerMode)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        Config(engine, multiServerMode);
        var mutes = new MuteManager(db.Provider);
        var steam = 76561198100001000UL;
        foreach (var serverId in ServerIds)
        {
            steam++;
            await Net.Mute(db, steam, "GAG", serverId: serverId);
            await Net.Mute(db, steam, "MUTE", duration: 30, serverId: serverId);
            await Net.Mute(db, steam, "SILENCE", duration: 30, serverId: serverId);
            var rows = await mutes.GetActiveMutesAsync(steam, 1, Time.ActualDateTime(), default);
            Assert.Equal(["GAG", "MUTE", "SILENCE"], rows.Select(r => r.Type).OrderBy(t => t));
            Assert.All(rows, r => Assert.True(r.Id > 0));
        }
    }

    [Theory, MemberData(nameof(EnginesAndLegacyScope))]
    public async Task WarnCountsHistoryAndRemovalIgnoreTheServer(string engine, bool multiServerMode)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        Config(engine, multiServerMode);
        CS2_SimpleAdmin.DatabaseProvider = db.Provider;
        var steam = 76561198100002001UL;
        foreach (var serverId in ServerIds)
            await Net.Exec(db, "INSERT INTO sa_warns (player_steamid, player_name, admin_steamid, admin_name, reason, duration, ends, created, server_id) VALUES (@steam, 'p', 0, 'Console', 'w', 0, @now, @now, @serverId)",
                new { steam, now = Time.ActualDateTime(), serverId });
        var warns = new WarnManager(db.Provider);

        Assert.Equal(5, await warns.GetPlayerWarnsCount(steam));
        Assert.Equal(5, (await warns.GetPlayerWarnsPageAsync(steam, 1, 8, default)).Total);
        var stats = await new MuteManager(db.Provider).GetPlayerPenaltyStatsAsync(steam, default);
        Assert.Equal(5, stats.TotalWarns);

        await warns.UnwarnPlayer(steam.ToString()); // "last warn" is the last of any server
        Assert.Equal(4, await warns.GetPlayerWarnsCount(steam));
        Assert.Equal(5, (await PlayerManager.GetPenaltyHistoryPage(steam, "warns", 1, 50, default)).Total);

        await Net.Exec(db, "UPDATE sa_warns SET duration = 5, ends = @past", new { past = Time.ActualDateTime().AddMinutes(-10) });
        await warns.ExpireOldWarns(); // expiry is not limited to this server's rows
        Assert.Equal(0, await warns.GetPlayerWarnsCount(steam));
    }

    [Theory, MemberData(nameof(EnginesAndLegacyScope))]
    public async Task UnbanUnmuteAndExpiryReachRowsOfEveryServer(string engine, bool multiServerMode)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine, multiServerMode);
        using var plugin = new TestPlugin(config, db.Provider);
        var bans = new BanManager(db.Provider);
        var mutes = new MuteManager(db.Provider);
        var steam = 76561198100003001UL;
        var otherServerBan = await Net.Ban(db, steam, serverId: 99);
        var nullServerMute = await Net.Mute(db, steam, "GAG", serverId: null);

        Assert.Equal(UnmuteOutcome.Removed, await mutes.UnmutePlayer(steam.ToString(), "0", "r", 0));
        await using var c = await db.OpenAsync();
        Assert.Equal("UNMUTED", await c.ExecuteScalarAsync<string>("SELECT status FROM sa_mutes WHERE id = @id", new { id = nullServerMute }));

        // css_unban finds the ban of another server (UnbanPlayer also drives the engine, so its SQL is checked here)
        Assert.Contains(otherServerBan, await c.QueryAsync<long>(db.Provider.GetUnbanRetrieveBansQuery(), new { pattern = steam.ToString() }));

        // timed rows that ended expire whichever server wrote them
        var endedBan = await Net.Ban(db, steam + 1, serverId: 77, duration: 5, ends: Time.ActualDateTime().AddMinutes(-1));
        var endedMute = await Net.Mute(db, steam + 1, "MUTE", duration: 5, ends: Time.ActualDateTime().AddMinutes(-1), serverId: 0);
        await bans.ExpireOldBans();
        await mutes.ExpireOldMutes();
        Assert.Equal("EXPIRED", await c.ExecuteScalarAsync<string>("SELECT status FROM sa_bans WHERE id = @id", new { id = endedBan }));
        Assert.Equal("EXPIRED", await c.ExecuteScalarAsync<string>("SELECT status FROM sa_mutes WHERE id = @id", new { id = endedMute }));
    }

    [Theory, MemberData(nameof(EnginesAndLegacyScope))]
    public async Task TheBanCacheHoldsBansOfEveryServer(string engine, bool multiServerMode)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine, multiServerMode);
        var steam = 76561198100004000UL;
        foreach (var serverId in ServerIds) await Net.Ban(db, ++steam, serverId: serverId);

        var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, default);
        Assert.Equal(ServerIds.Length, cache.Snapshot.ActiveCount);

        var late = await Net.Ban(db, ++steam, serverId: 4242);
        await Net.Exec(db, "UPDATE sa_bans SET player_name = 'x' WHERE id = @late", new { late });
        await cache.RefreshCacheAsync(config, default);
        Assert.True(cache.Snapshot.ActiveBans.ContainsKey((int)late));
    }

    // ---------------------------------------------------------------- 2. SteamID bans: independent of BanType / IP / IgnoredIps

    [Theory, MemberData(nameof(Engines))]
    public async Task ASteamBanHoldsForEveryBanTypeIpAndIgnoredIpSetting(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var now = Net.Now();
        var steam = 76561198100005000UL;
        await Net.Ban(db, steam, ip: "1.1.1.1");
        foreach (var banType in new[] { 0, 1, 2 })
        foreach (var multi in new[] { false, true })
        foreach (var playerIp in new[] { "1.1.1.1", "8.8.8.8", null })
        foreach (var ignored in new[] { Array.Empty<string>(), ["8.8.8.8", "1.1.1.1"] })
        {
            var config = Config(engine, banType: banType, multi: multi, ignoredIps: ignored);
            var cache = new CacheManager();
            await cache.InitializeCacheAsync(config, default);
            var r = await BanDecider.DecideAsync(db.Provider, steam, now, default);
            Assert.True(r.IsBanned, $"BanType={banType} multi={multi} ip={playerIp} ignored={ignored.Length}");
            Assert.Equal(BanMatch.SteamId, r.Match);
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task BanLifecycleRulesPermanentTimedBoundaryRemovedExpiredAndSeveralRows(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine);
        var cache = new CacheManager();
        var now = Net.Now();
        async Task<bool> Banned(ulong s) => (await BanDecider.DecideAsync(db.Provider, s, now, default)).IsBanned;

        const ulong baseId = 76561198100006000;
        await Net.Ban(db, baseId + 1, duration: 0, ends: now.AddYears(-5));                    // permanent: the fictitious ends must not matter
        await Net.Ban(db, baseId + 2, duration: 60, ends: now.AddSeconds(1));                  // one second left
        await Net.Ban(db, baseId + 3, duration: 60, ends: now);                                // ends == now: over (same rule as the expiry job)
        await Net.Ban(db, baseId + 4, duration: 60, ends: now.AddSeconds(-1));                 // over, not yet marked EXPIRED
        await Net.Ban(db, baseId + 5, status: "UNBANNED");
        await Net.Ban(db, baseId + 6, status: "EXPIRED");
        await Net.Ban(db, baseId + 7, status: "UNBANNED");                                     // several rows: lifted ones + ...
        await Net.Ban(db, baseId + 7, duration: 60, ends: now.AddSeconds(-30));                // ... an elapsed one + ...
        await Net.Ban(db, baseId + 7, duration: 10, ends: now.AddMinutes(10));                 // ... a running one

        Assert.True(await Banned(baseId + 1));
        Assert.True(await Banned(baseId + 2));
        Assert.False(await Banned(baseId + 3));
        Assert.False(await Banned(baseId + 4));
        Assert.False(await Banned(baseId + 5));
        Assert.False(await Banned(baseId + 6));
        Assert.True(await Banned(baseId + 7));
        Assert.False(await Banned(baseId + 8));                                                // never banned
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task IpAddressesNeverBanAnyone(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var now = Net.Now();
        const ulong banned = 76561198100007001, innocent = 76561198100007002;
        var banId = await Net.Ban(db, banned, ip: "5.5.5.5");
        // two active bans on the same address, one of them on a row that has no SteamID-link to the newcomer at all
        await Net.Ban(db, banned + 100, ip: "5.5.5.5");

        // whatever BanType / CheckMultiAccountsByIp / IgnoredIps say, and whatever the (fresh or stale) cache holds
        foreach (var banType in new[] { 0, 1, 2 })
        foreach (var multi in new[] { false, true })
        {
            var config = Config(engine, banType: banType, multi: multi);
            var cache = new CacheManager();
            await cache.InitializeCacheAsync(config, default);
            Assert.False((await BanDecider.DecideAsync(db.Provider, innocent, now, default)).IsBanned, $"BanType={banType} multi={multi}");
        }

        // a ban row whose IP column changes after caching does not move the ban to another player either
        await Net.Exec(db, "UPDATE sa_bans SET player_ip = '9.9.9.9' WHERE id = @banId", new { banId });
        Assert.False((await BanDecider.DecideAsync(db.Provider, innocent, now, default)).IsBanned);
        Assert.True((await BanDecider.DecideAsync(db.Provider, banned, now, default)).IsBanned);   // its own SteamID, always

        // the periodic batch read follows the same rule
        var set = await BanDecider.FindBannedAsync(db.Provider, [banned, innocent, banned + 100, 0], now, default);
        Assert.Equal(new HashSet<ulong> { banned, banned + 100 }, set);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task OnlinePlayersAreCheckedInBoundedBatchesAgainstTheLiveTable(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var now = Net.Now();
        const ulong first = 76561198100020000;
        var players = Enumerable.Range(0, BanDecider.BatchSize * 2 + 3).Select(i => first + (ulong)i).ToList();
        await Net.Ban(db, players[0]);                                           // first batch
        await Net.Ban(db, players[BanDecider.BatchSize + 1], duration: 10, ends: now.AddMinutes(5)); // second batch, running
        await Net.Ban(db, players[^1]);                                          // third batch
        await Net.Ban(db, players[5], status: "UNBANNED");                       // lifted elsewhere
        await Net.Ban(db, players[6], duration: 10, ends: now.AddSeconds(-1));   // elapsed, not yet marked EXPIRED

        var banned = await BanDecider.FindBannedAsync(db.Provider, players, now, default);
        Assert.Equal(new HashSet<ulong> { players[0], players[BanDecider.BatchSize + 1], players[^1] }, banned);
        Assert.Empty(await BanDecider.FindBannedAsync(db.Provider, [], now, default));
        await Assert.ThrowsAnyAsync<Exception>(() => BanDecider.FindBannedAsync(new OutageProvider(), players, now, default));
    }

    // ---------------------------------------------------------------- 3. fresh ban between the last refresh and the connection

    [Theory, MemberData(nameof(Engines))]
    public async Task ABanInsertedAfterTheLastRefreshIsNotMissedAtConnect(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine);
        var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, default);                // refresh at T0 ...
        const ulong steam = 76561198100008001;
        await Net.Ban(db, steam, serverId: 2);                            // ... ban written on server A at T0 + 1 s ...
        var now = Net.Now();

        Assert.False(cache.CheckBan(config, steam, "7.7.7.7", now).IsBanned);   // the local cache cannot know (the old connect path)
        var decision = await BanDecider.DecideAsync(db.Provider, steam, now, default);
        Assert.True(decision.IsBanned);                                   // ... and server B refuses the connection at T0 + 2 s
        Assert.Equal(BanMatch.SteamId, decision.Match);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task ALiftedBanIsNotAFalseBanWhateverTheCacheHolds(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine);
        const ulong steam = 76561198100009001;
        var id = await Net.Ban(db, steam, serverId: 2);
        var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, default);
        Assert.True(cache.CheckBan(config, steam, null, Net.Now()).IsBanned);

        await Net.Exec(db, "UPDATE sa_bans SET status = 'UNBANNED' WHERE id = @id", new { id }); // lifted on the other server
        Assert.False((await BanDecider.DecideAsync(db.Provider, steam, Net.Now(), default)).IsBanned);
    }

    // ---------------------------------------------------------------- read failures are not "no penalty"

    [Theory, MemberData(nameof(Engines))]
    public async Task AFailedReadIsAnExceptionNeverAnEmptyAnswer(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine);
        var flaky = new FlakyQueriesProvider(db.Provider);
        var cache = new CacheManager();
        const ulong steam = 76561198100010001;
        await Net.Ban(db, steam);
        await Net.Mute(db, steam, "GAG");

        flaky.FailSteamBans = true;
        await Assert.ThrowsAnyAsync<Exception>(() => BanDecider.DecideAsync(flaky, steam, Net.Now(), default));
        flaky.FailMutes = true;
        await Assert.ThrowsAnyAsync<Exception>(() => new MuteManager(flaky).GetActiveMutesAsync(steam, 1, Time.ActualDateTime(), default));
        await Assert.ThrowsAnyAsync<Exception>(() => new MuteManager(new OutageProvider()).GetActiveMutesBatchAsync([steam], 1, Time.ActualDateTime(), default));
        await Assert.ThrowsAnyAsync<Exception>(() => BanDecider.DecideAsync(new OutageProvider(), steam, Net.Now(), default));

        // SteamID 0 (authorization not finished) cannot be verified either
        await Assert.ThrowsAsync<InvalidOperationException>(() => BanDecider.DecideAsync(db.Provider, 0, Net.Now(), default));

        // and the same reads succeed once the database is fine: "nothing" is an answer only when the read worked
        flaky.FailSteamBans = flaky.FailMutes = false;
        Assert.True((await BanDecider.DecideAsync(flaky, steam, Net.Now(), default)).IsBanned);
        Assert.Single(await new MuteManager(flaky).GetActiveMutesAsync(steam, 1, Time.ActualDateTime(), default));
        Assert.Empty(await new MuteManager(flaky).GetActiveMutesAsync(steam + 1, 1, Time.ActualDateTime(), default)); // a successful empty answer
    }

    // ---------------------------------------------------------------- 6. TimeMode 0 / 1, passed = NULL, permanent rows

    [Theory, MemberData(nameof(Engines))]
    public async Task ActivityRulesForBothTimeModesPassedNullAndPermanentRows(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var mutes = new MuteManager(db.Provider);
        var now = Net.Now();
        const ulong s = 76561198100011000;
        await Net.Mute(db, s + 1, "MUTE", duration: 0, ends: now.AddYears(-3));                          // permanent, fictitious past ends
        await Net.Mute(db, s + 2, "GAG", duration: 60, ends: now.AddMinutes(30));                        // running, passed NULL
        await Net.Mute(db, s + 3, "GAG", duration: 60, ends: now.AddMinutes(-1));                        // real time over, passed NULL
        await Net.Mute(db, s + 4, "GAG", duration: 60, ends: now.AddMinutes(30), passed: 60);            // online time used up
        await Net.Mute(db, s + 5, "SILENCE", duration: 60, ends: now.AddMinutes(30), status: "UNMUTED");
        await Net.Mute(db, s + 6, "SILENCE", duration: 60, ends: now.AddMinutes(30), status: "EXPIRED");

        async Task<int> Active(ulong id, int timeMode) => (await mutes.GetActiveMutesAsync(id, timeMode, now, default)).Count;

        foreach (var timeMode in new[] { 0, 1 })
        {
            Assert.Equal(1, await Active(s + 1, timeMode));      // permanent never expires, whatever its ends says
            Assert.Equal(1, await Active(s + 2, timeMode));
            Assert.Equal(0, await Active(s + 5, timeMode));      // lifted
            Assert.Equal(0, await Active(s + 6, timeMode));
        }

        Assert.Equal(0, await Active(s + 3, 1));                 // TimeMode 1: ends passed
        Assert.Equal(1, await Active(s + 3, 0));                 // TimeMode 0: only online time counts, passed NULL = 0 minutes used
        Assert.Equal(1, await Active(s + 4, 1));                 // TimeMode 1: ends in the future
        Assert.Equal(0, await Active(s + 4, 0));                 // TimeMode 0: used up
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task OnlineCreditOfAMuteFromAnotherServerIsAppliedExactlyOnce(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var config = Config(engine, multiServer: false, timeMode: 0);
        var mutes = new MuteManager(db.Provider);
        const ulong steam = 76561198100012001;
        // C#-side comparison of a read-back column with the credit window: same clock and kind as the other online-credit tests
        var id = await Net.Mute(db, steam, "MUTE", duration: 60, serverId: 2, created: DateTime.Now.AddHours(-3));

        var start = DateTime.Now.AddMinutes(-10);
        var credit = new OnlineCredit(steam, 10, 10 * OnlineCredit.TicksPerMinute, start, start.AddMinutes(10));
        await mutes.CheckOnlineModeMutesAsync([credit], default);
        await mutes.CheckOnlineModeMutesAsync([credit], default); // retried with the same credit object (lost acknowledgement)

        await using var c = await db.OpenAsync();
        Assert.Equal(10, await c.ExecuteScalarAsync<int>("SELECT passed FROM sa_mutes WHERE id = @id", new { id }));
    }
}

/// <summary>Guard: no query that reads or changes penalties may filter by the server again.</summary>
public class PenaltyQueriesAreNetworkWideTests
{
    public static IEnumerable<object[]> Providers()
    {
        yield return [new SqliteDatabaseProvider(Path.Combine(Path.GetTempPath(), "sa_unused_guard.sqlite"))];
        yield return [new MySqlDatabaseProvider("Server=127.0.0.1")];
    }

    [Theory, MemberData(nameof(Providers))]
    public void NoPenaltyQueryMentionsTheServer(IDatabaseProvider p)
    {
        var queries = new Dictionary<string, string>
        {
            ["UnbanRetrieve"] = p.GetUnbanRetrieveBansQuery(),
            ["ExpireBans"] = p.GetExpireBansQuery(),
            ["ExpireIpBans"] = p.GetExpireIpBansQuery(),
            ["ActiveSteamBans"] = p.GetActiveSteamBansQuery(),
            ["ActiveSteamBansBatch"] = p.GetActiveSteamBansBatchQuery(),
            ["RetrieveMutes"] = p.GetRetrieveMutesQuery(),
            ["MuteStats"] = p.GetMuteStatsQuery(),
            ["WarnsMenuPage"] = p.GetWarnsMenuPageQuery(),
            ["WarnsMenuCount"] = p.GetWarnsMenuCountQuery(),
            ["OnlineCreditPlan"] = p.GetOnlineCreditPlanQuery(),
            ["ExpiredOnline"] = p.GetExpiredOnlineMutesBatchQuery(),
            ["PenaltyStats"] = p.GetPlayerPenaltyStatsQuery(),
            ["History"] = p.GetPenaltyHistoryQuery(),
            ["UnwarnById"] = p.GetUnwarnByIdQuery(),
            ["UnwarnLast"] = p.GetUnwarnLastQuery(),
            ["ExpireWarns"] = p.GetExpireWarnsQuery()
        };
        foreach (var timeMode in new[] { 0, 1 })
        {
            queries[$"IsMuted{timeMode}"] = p.GetIsMutedQuery(timeMode);
            queries[$"ActiveMutes{timeMode}"] = p.GetActiveMutesBatchQuery(timeMode);
            queries[$"ExpireMutes{timeMode}"] = p.GetExpireMutesQuery(timeMode);
        }

        foreach (var active in new[] { true, false })
        {
            queries[$"Warns{active}"] = p.GetPlayerWarnsQuery(active);
            queries[$"WarnsCount{active}"] = p.GetPlayerWarnsCountQuery(active);
        }

        foreach (var type in new string?[] { null, "bans", "gags", "mutes", "silences", "warns" })
        {
            queries[$"HistoryPage:{type}"] = p.GetPenaltyHistoryPageQuery(type);
            queries[$"HistoryCount:{type}"] = p.GetPenaltyHistoryCountQuery(type);
        }

        foreach (var (name, sql) in queries)
        {
            Assert.DoesNotContain("server_id", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@serverid", sql, StringComparison.OrdinalIgnoreCase);
            Assert.True(sql.Length > 10, name);
        }

        foreach (var sql in new[]
                 {
                     CacheManager.ActiveBansPageSql, CacheManager.ChangedBansPageSql, CacheManager.ActiveChecksumSql,
                     CacheManager.ActiveIdsSql
                 })
            Assert.DoesNotContain("server_id", sql, StringComparison.OrdinalIgnoreCase);
    }
}
