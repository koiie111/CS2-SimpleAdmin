using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;
using CS2_SimpleAdminApi;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// Second review round (N1–N6). The six reproduction probes of the reviewer are ported unchanged in intent (their
/// assertions are not weakened; only the startup delegate gained the explicit context parameter that N2 asks for);
/// the rest of the file covers the corners the fixes must hold.
/// </summary>
public class R10_RecheckN1N6Tests
{
    // =====================================================================================================
    // N1 – IP checksum arithmetic
    // =====================================================================================================

    private const long RealSteamBase = 76561198000000000L;
    private const long LargestSteamId64 = 76561202255233023L; // account id 0xFFFFFFFF in the public universe

    // ---- ported review probe (N1) ----
    [Fact]
    public async Task IpReconciliationMustHandle121RealSteamIds()
    {
        await using var db = await TestDatabases.CreateAsync("SQLite");
        var config = TestConfig.Use(c =>
        {
            c.DatabaseConfig.DatabaseType = "SQLite"; c.MultiServerMode = true;
            c.OtherSettings.CheckMultiAccountsByIp = true; c.OtherSettings.ExpireOldIpBans = 30;
        });
        await using (var c = await db.OpenAsync())
            for (var i = 0; i < 121; i++)
                await c.ExecuteAsync("INSERT INTO sa_players_ips(steamid,address,name,used_at) VALUES (@steam,@ip,'probe',@now)",
                    new { steam = RealSteamBase + i, ip = 167772161L + i, now = DateTime.UtcNow });
        using var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, null, default);
        for (var i = 0; i < CacheManager.IpChecksumEveryRefreshes + 1; i++)
            await cache.RefreshCacheAsync(config, null, default);
    }

    private static async Task InsertIps(TestDatabase db, int players, int ipsPerPlayer, long steamBase = RealSteamBase)
    {
        await using var c = await db.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        for (var p = 0; p < players; p++)
            for (var k = 0; k < ipsPerPlayer; k++)
                await c.ExecuteAsync("INSERT INTO sa_players_ips(steamid,address,name,used_at) VALUES (@steam,@ip,'n',@now)",
                    new { steam = steamBase + p, ip = 167772161L + p * 7L + k, now = DateTime.UtcNow }, tx);
        await tx.CommitAsync();
    }

    private static async Task<(long, long, long)> SqlChecksum(TestDatabase db, DateTime cutoff)
    {
        await using var c = await db.OpenAsync();
        await using var reader = await c.ExecuteReaderAsync(CacheManager.IpChecksumSql, new { cutoff });
        Assert.True(await reader.ReadAsync());
        return (Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2)));
    }

    /// <summary>SQL and managed checksums must be identical on every engine, across the 2^63 boundary of a plain sum.</summary>
    [Theory, MemberData(nameof(Engines))]
    public async Task SqlAndManagedChecksumAgreeForRealSteamIdCounts(string engine)
    {
        TestDatabases.SkipIfNoServer(engine);
        foreach (var players in new[] { 120, 121, 1000 })
        {
            await using var db = await TestDatabases.CreateAsync(engine);
            await InsertIps(db, players, 1);
            var config = TestConfig.Use(c =>
            {
                c.DatabaseConfig.DatabaseType = db.IsSqlite ? "SQLite" : "MySQL"; c.MultiServerMode = true;
                c.OtherSettings.CheckMultiAccountsByIp = true; c.OtherSettings.ExpireOldIpBans = 30;
            });
            using var cache = new CacheManager();
            await cache.InitializeCacheAsync(config, null, default);
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var sql = await SqlChecksum(db, cutoff);
            Assert.Equal(players, sql.Item1);
            Assert.Equal(sql, cache.Snapshot.IpHistory.Checksum(cutoff));
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task SeveralIpsPerPlayerAndTheLargestSteamIdStayExact(string engine)
    {
        TestDatabases.SkipIfNoServer(engine);
        await using var db = await TestDatabases.CreateAsync(engine);
        await InsertIps(db, 150, 4); // 600 rows, 4 per account
        await InsertIps(db, 150, 2, LargestSteamId64 - 200); // the top of the SteamID64 range
        var config = TestConfig.Use(c =>
        {
            c.DatabaseConfig.DatabaseType = db.IsSqlite ? "SQLite" : "MySQL"; c.MultiServerMode = true;
            c.OtherSettings.CheckMultiAccountsByIp = true; c.OtherSettings.ExpireOldIpBans = 30;
        });
        using var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, null, default);
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var sql = await SqlChecksum(db, cutoff);
        Assert.Equal(900, sql.Item1);
        Assert.Equal(sql, cache.Snapshot.IpHistory.Checksum(cutoff));
    }

    /// <summary>The bound itself: the worst possible term is below 2^32, so 2^31-1 rows cannot reach Int64.MaxValue.</summary>
    [Fact]
    public void ChecksumTermsAreBoundedSoTheSumCannotOverflow()
    {
        // Each of the two sums has its own bound: steam % M < 2^31 and an UInt32 address < 2^32, up to 2^31-1 rows
        const long maxRows = int.MaxValue;
        Assert.True((decimal)(IpHistoryIndex.SteamModulus - 1) * maxRows < long.MaxValue);
        Assert.True((decimal)uint.MaxValue * maxRows < long.MaxValue);
        // and the real range really is reduced: 121 raw SteamID64 values overflow Int64 (what the old arithmetic summed)
        Assert.Throws<OverflowException>(() => { long raw = 0; for (var i = 0; i < 121; i++) raw = checked(raw + LargestSteamId64); });
    }

    /// <summary>An external DELETE is still detected (the checksum was not removed) and repaired by a rebuild.</summary>
    [Theory, MemberData(nameof(Engines))]
    public async Task ExternalDeleteOfRealSteamIdRowsIsDetectedAndRebuilt(string engine)
    {
        TestDatabases.SkipIfNoServer(engine);
        await using var db = await TestDatabases.CreateAsync(engine);
        await InsertIps(db, 1000, 1);
        var config = TestConfig.Use(c =>
        {
            c.DatabaseConfig.DatabaseType = db.IsSqlite ? "SQLite" : "MySQL"; c.MultiServerMode = true;
            c.OtherSettings.CheckMultiAccountsByIp = true; c.OtherSettings.ExpireOldIpBans = 30;
        });
        using var cache = new CacheManager();
        await cache.InitializeCacheAsync(config, null, default);
        for (var i = 0; i < CacheManager.IpChecksumEveryRefreshes * 2; i++) await cache.RefreshCacheAsync(config, null, default);
        Assert.Equal(0, cache.IpRebuilds); // nothing differs: no false rebuild

        const string deletedIp = "10.0.0.1"; // 167772161 = player 0
        Assert.NotEmpty(cache.GetAccountsByIp(deletedIp, DateTime.UtcNow, 30));
        await using (var c = await db.OpenAsync())
            await c.ExecuteAsync("DELETE FROM sa_players_ips WHERE steamid = @s", new { s = RealSteamBase });

        for (var i = 0; i < CacheManager.IpChecksumEveryRefreshes * 2; i++) await cache.RefreshCacheAsync(config, null, default);
        Assert.Equal(1, cache.IpRebuilds);
        Assert.Empty(cache.GetAccountsByIp(deletedIp, DateTime.UtcNow, 30));
    }

    /// <summary>Renewal outside the plugin (used_at moves forward) is picked up by the delta, and refresh == rebuild afterwards.</summary>
    [Theory, MemberData(nameof(Engines))]
    public async Task ExternalRenewalKeepsRefreshAndRebuildIdentical(string engine)
    {
        TestDatabases.SkipIfNoServer(engine);
        await using var db = await TestDatabases.CreateAsync(engine);
        await InsertIps(db, 300, 2);
        var config = TestConfig.Use(c =>
        {
            c.DatabaseConfig.DatabaseType = db.IsSqlite ? "SQLite" : "MySQL"; c.MultiServerMode = true;
            c.OtherSettings.CheckMultiAccountsByIp = true; c.OtherSettings.ExpireOldIpBans = 30;
        });
        using var refreshed = new CacheManager();
        await refreshed.InitializeCacheAsync(config, null, default);
        await using (var c = await db.OpenAsync())
        {
            await c.ExecuteAsync("UPDATE sa_players_ips SET used_at = @t WHERE steamid < @s", new { t = DateTime.UtcNow.AddMinutes(1), s = RealSteamBase + 100 });
            await c.ExecuteAsync("DELETE FROM sa_players_ips WHERE steamid >= @s", new { s = RealSteamBase + 290 });
            await c.ExecuteAsync("INSERT INTO sa_players_ips(steamid,address,name,used_at) VALUES (@s, 3232235777, 'new', @t)", new { s = RealSteamBase + 5000, t = DateTime.UtcNow });
        }

        for (var i = 0; i < CacheManager.IpChecksumEveryRefreshes * 2 + 1; i++) await refreshed.RefreshCacheAsync(config, null, default);
        using var rebuilt = new CacheManager();
        await rebuilt.InitializeCacheAsync(config, null, default);
        var cutoff = DateTime.UtcNow.AddDays(-30);
        Assert.Equal(rebuilt.Snapshot.IpHistory.Checksum(cutoff), refreshed.Snapshot.IpHistory.Checksum(cutoff));
        Assert.Equal(await SqlChecksum(db, cutoff), refreshed.Snapshot.IpHistory.Checksum(cutoff));
    }

    public static IEnumerable<object[]> Engines() => TestDatabases.All();

    // =====================================================================================================
    // N2 – generation escape
    // =====================================================================================================

    // ---- ported review probe (N2, nested worker) ----
    [Fact]
    public async Task OldWorkerMustNotRebindNestedWorkToNewLifetime()
    {
        using var oldWorld = new TestWorld();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;
        var oldJob = Runtime.TryQueueDb<int>("old", async _ =>
        {
            started.SetResult(); await gate.Task;
            var child = Runtime.TryQueueDb<int>("old-child", async _ =>
            {
                await Runtime.OnGameThread(() => { applied = true; });
                return 1;
            });
            return child == null ? 0 : await child;
        })!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var newWorld = new TestWorld();
        gate.SetResult();
        await newWorld.PumpUntil(() => oldJob.IsCompleted);
        Assert.False(applied, "Nested work started by the old worker was rebound to the new lifetime");
    }

    // ---- ported review probe (N2, stale startup) ----
    [Fact]
    public async Task StaleStartupMustNotInvokeReloadForNewContext()
    {
        using var oldWorld = new TestWorld();
        var oldContext = Runtime.Context;
        using var newWorld = new TestWorld();
        var called = false;
        try
        {
            await ServerManager.ReloadAdminsWithRetriesAsync(_ =>
            {
                called = true; return Task.FromResult(AdminReloadResult.Success);
            }, oldContext, []);
        }
        catch (OperationCanceledException) { }
        Assert.False(called, "Stale startup invoked its reload delegate after lifetime replacement");
    }

    /// <summary>An old-lifetime worker waits at a gate; when released it tries to queue more work and reports whether that was accepted.</summary>
    private static async Task<(Task<bool> Accepted, TaskCompletionSource Gate)> StartOldWorkerThatQueuesLater(Func<bool> nested)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.NotNull(Runtime.TryQueueDb<bool>("old", async _ =>
        {
            started.SetResult(); await gate.Task;
            var result = nested();
            accepted.SetResult(result);
            return result;
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        return (accepted.Task, gate);
    }

    [Fact]
    public async Task OldWorkerMustNotQueueHttpWorkIntoTheNewLifetime()
    {
        using var oldWorld = new TestWorld();
        var ran = false;
        var (job, gate) = await StartOldWorkerThatQueuesLater(() =>
            Runtime.Http!.TryEnqueue("old-http", _ => { ran = true; return Task.CompletedTask; }));
        using var newWorld = new TestWorld();
        gate.SetResult();
        Assert.False(await job.WaitAsync(TimeSpan.FromSeconds(2)), "the stale producer's HTTP job was accepted by the new queue");
        await Task.Delay(50);
        Assert.False(ran);
    }

    [Fact]
    public async Task OldWorkerMustNotQueueFireAndForgetDbWorkIntoTheNewLifetime()
    {
        using var oldWorld = new TestWorld();
        var ran = false;
        var (job, gate) = await StartOldWorkerThatQueuesLater(() =>
            Runtime.TryQueueDb("old-void", _ => { ran = true; return Task.CompletedTask; }));
        using var newWorld = new TestWorld();
        gate.SetResult();
        Assert.False(await job.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(50);
        Assert.False(ran);
    }

    [Fact]
    public void ExplicitContextOfAStaleLifetimeIsRefused()
    {
        using var oldWorld = new TestWorld();
        var stale = new WorkContext(Runtime.Context, 1, CallerRef.Console);
        using var newWorld = new TestWorld();
        Assert.False(Runtime.TryQueueDb("explicit", _ => Task.CompletedTask, stale));
        Assert.False(Runtime.Context.IsCurrent && !Runtime.Context.IsCurrent); // sanity: the new context is current
        Assert.True(Runtime.TryQueueDb("explicit", _ => Task.CompletedTask, new WorkContext(Runtime.Context, 1, CallerRef.Console)));
    }

    [Fact]
    public void RuntimeContextOfAStaleLifetimeCannotQueueIntoTheNewOne()
    {
        using var oldWorld = new TestWorld();
        var oldContext = Runtime.Context;
        using var newWorld = new TestWorld();
        Assert.Null(oldContext.TryQueueDb<int>("startup-step", _ => Task.FromResult(1)));
        Assert.NotNull(Runtime.Context.TryQueueDb<int>("startup-step", _ => Task.FromResult(1)));
    }

    [Fact]
    public async Task NothingCanBeQueuedByAnyOverloadAfterStop()
    {
        using var world = new TestWorld();
        var ctx = new WorkContext(Runtime.Context, 1, CallerRef.Console);
        Runtime.Stop();
        Assert.False(Runtime.TryQueueDb("a", _ => Task.CompletedTask));
        Assert.False(Runtime.TryQueueDb("b", _ => Task.CompletedTask, ctx));
        Assert.Null(Runtime.TryQueueDb<int>("c", _ => Task.FromResult(1)));
        Assert.Null(Runtime.Context.TryQueueDb<int>("d", _ => Task.FromResult(1)));
        await Task.Yield();
    }

    [Fact]
    public async Task NestedWorkOfTheCurrentLifetimeInheritsItsContextAndStillRuns()
    {
        using var world = new TestWorld(sqlite: false, serverId: 1); // 4 workers: the parent awaits its child
        var caller = new CallerRef(false, 3, 76561198000000003, 9);
        var parentContext = new WorkContext(Runtime.Context, 7, caller);
        WorkContext? seen = null;
        var applied = false;
        Assert.True(Runtime.TryQueueDb("parent", async _ =>
        {
            var child = Runtime.TryQueueDb<int>("child", async _ =>
            {
                seen = WorkContext.Current;
                await Runtime.OnGameThread(() => { applied = true; });
                return 1;
            });
            Assert.NotNull(child);
            await child!;
        }, parentContext));
        await world.PumpUntil(() => applied);
        Assert.NotNull(seen);
        Assert.Same(parentContext.Runtime, seen!.Runtime);
        Assert.Equal(7, seen.ServerId); // inherited, not re-read from the current global
        Assert.Equal(caller, seen.Caller);
    }

    [Fact]
    public async Task StartupPassesItsOwnContextToTheReload()
    {
        using var world = new TestWorld();
        var context = Runtime.Context;
        RuntimeContext? received = null;
        await ServerManager.ReloadAdminsWithRetriesAsync(c => { received = c; return Task.FromResult(AdminReloadResult.Success); }, context, []);
        Assert.Same(context, received);
    }

    [Fact]
    public async Task StartupDoesNotContinueAfterTheLifetimeEndedDuringTheReload()
    {
        using var oldWorld = new TestWorld();
        var context = Runtime.Context;
        var calls = 0;
        TestWorld? newWorld = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServerManager.ReloadAdminsWithRetriesAsync(_ =>
            {
                calls++;
                newWorld = new TestWorld(); // the lifetime is replaced while the reload is "running"
                return Task.FromResult(AdminReloadResult.Failed); // would normally be retried
            }, context, [TimeSpan.Zero, TimeSpan.Zero]));
            Assert.Equal(1, calls); // no second attempt for a stale startup
        }
        finally { newWorld?.Dispose(); }
    }

    // =====================================================================================================
    // N3 – awaiter cancellation of running work
    // =====================================================================================================

    // ---- ported review probe (N3) ----
    [Fact]
    public async Task UnloadMustCancelAwaiterOfRunningLegacySql()
    {
        using var lifetime = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("probe", 8, 1, lifetime.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = queue.TryEnqueue<int>("legacy-sql", async _ =>
        {
            started.SetResult(); await gate.Task; return 99;
        })!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            lifetime.Cancel(); queue.Complete();
            await Task.WhenAny(job, Task.Delay(200));
            Assert.True(job.IsCanceled, "Lifetime cancellation leaves a running typed awaiter unfinished when SQL ignores the token");
        }
        finally { gate.TrySetResult(); await TestWorld.WaitUntil(() => queue.Pending == 0); }
    }

    [Fact]
    public async Task RunningSqlKeepsItsConcurrencySlotUntilItReallyReturns_AndItsLateResultIsIgnored()
    {
        using var lifetime = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("probe", 8, 1, lifetime.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = queue.TryEnqueue<int>("legacy-sql", async _ => { started.SetResult(); await gate.Task; return 99; })!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lifetime.Cancel(); queue.Complete();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.WaitAsync(TimeSpan.FromSeconds(2)));
        // the awaiter is free, the SQL is not: the slot is still taken
        Assert.Equal(1, queue.Running);
        Assert.Equal(1, queue.Pending);

        gate.SetResult();
        await TestWorld.WaitUntil(() => queue.Pending == 0 && queue.Running == 0);
        Assert.True(job.IsCanceled, "the late result must not change an already cancelled task");
    }

    [Fact]
    public async Task QueuedJobAwaiterIsReleasedByLifetimeAndByOwnToken()
    {
        using var lifetime = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("probe", 8, 1, lifetime.Token);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = queue.TryEnqueue<int>("blocker", async _ => { blockerStarted.SetResult(); await gate.Task; return 0; })!;
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var own = new CancellationTokenSource();
        var byOwn = queue.TryEnqueue<int>("own", _ => Task.FromResult(1), own.Token)!;
        var byLifetime = queue.TryEnqueue<int>("lifetime", _ => Task.FromResult(2))!;
        own.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => byOwn.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(byLifetime.IsCompleted);

        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => byLifetime.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocker.WaitAsync(TimeSpan.FromSeconds(2)));
        gate.SetResult();
        await TestWorld.WaitUntil(() => queue.Pending == 0);
    }

    [Fact]
    public async Task RunningJobWithItsOwnTokenIsReleasedAtOnceWhenThatTokenIsCancelled()
    {
        using var lifetime = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("probe", 8, 1, lifetime.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var own = new CancellationTokenSource();
        var job = queue.TryEnqueue<int>("sql", async _ => { started.SetResult(); await gate.Task; return 5; }, own.Token)!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        own.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.WaitAsync(TimeSpan.FromSeconds(2)));
        gate.SetResult();
        await TestWorld.WaitUntil(() => queue.Pending == 0);
        Assert.True(job.IsCanceled);
        lifetime.Cancel();
    }

    [Fact]
    public async Task SuccessCancelRaceEndsInExactlyOneStableState()
    {
        for (var i = 0; i < 300; i++)
        {
            using var lifetime = new CancellationTokenSource();
            var queue = new BoundedWorkQueue("race", 8, 2, lifetime.Token);
            var job = queue.TryEnqueue<int>("quick", _ => Task.FromResult(5))!;
            var cancel = Task.Run(() => { lifetime.Cancel(); queue.Complete(); });
            var finished = await Task.WhenAny(job, Task.Delay(2000));
            Assert.Same(job, finished); // never hangs
            await cancel;
            var status = job.Status;
            Assert.True(status is TaskStatus.RanToCompletion or TaskStatus.Canceled, $"unexpected {status}");
            if (status == TaskStatus.RanToCompletion) Assert.Equal(5, await job);
            await Task.Delay(1);
            Assert.Equal(status, job.Status); // a late completion never flips a final state
        }
    }

    // =====================================================================================================
    // N4 – banned connect with a temporarily unavailable controller
    // =====================================================================================================

    private sealed class BanFixture : IAsyncDisposable
    {
        public required TestDatabase Db;
        public required TestWorld World;
        public required TestPlugin Plugin;
        public required PlayerSession Session;
        public int Kicks;
        public int Retries;

        public static async Task<BanFixture> Create()
        {
            var db = await TestDatabases.CreateAsync("SQLite");
            var world = new TestWorld();
            var config = TestConfig.Use(c =>
            {
                c.DatabaseConfig.DatabaseType = "SQLite"; c.MultiServerMode = true;
                c.OtherSettings.CheckMultiAccountsByIp = false; c.OtherSettings.BanType = 0;
            });
            var plugin = new TestPlugin(config, db.Provider);
            var session = Runtime.Sessions.BeginOrGet(1, 76561198000000001, 1, "probe", null, out _);
            await using (var c = await db.OpenAsync())
                await c.ExecuteAsync("INSERT INTO sa_bans(player_steamid,admin_steamid,admin_name,reason,duration,ends,created,status) VALUES (76561198000000001,0,'Console','probe',0,@now,@now,'ACTIVE')", new { now = DateTime.UtcNow });
            await plugin.Plugin.CacheManager!.InitializeCacheAsync(config, null, default);
            var fixture = new BanFixture { Db = db, World = world, Plugin = plugin, Session = session };
            PlayerManager.KickBanned = _ => Interlocked.Increment(ref fixture.Kicks);
            PlayerManager.RetryScheduler = (_, _) => Interlocked.Increment(ref fixture.Retries);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            PlayerManager.KickBanned = static session =>
            {
                if (PlayerManager.ResolveController(session) is { } player)
                    Helper.KickPlayer(player, CounterStrikeSharp.API.ValveConstants.Protobuf.NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED);
            };
            Plugin.Dispose();
            World.Dispose();
            await Db.DisposeAsync();
        }
    }

    // ---- ported review probe (N4) ----
    [Fact]
    public async Task BannedConnectWithTemporarilyUnavailableControllerMustBeRetryable()
    {
        await using var db = await TestDatabases.CreateAsync("SQLite");
        using var world = new TestWorld();
        var config = TestConfig.Use(c => { c.DatabaseConfig.DatabaseType = "SQLite"; c.MultiServerMode = true; c.OtherSettings.CheckMultiAccountsByIp = false; c.OtherSettings.BanType = 0; });
        using var plugin = new TestPlugin(config, db.Provider);
        var session = Runtime.Sessions.BeginOrGet(1, 76561198000000001, 1, "probe", null, out _);
        await using (var c = await db.OpenAsync())
            await c.ExecuteAsync("INSERT INTO sa_bans(player_steamid,admin_steamid,admin_name,reason,duration,ends,created,status) VALUES (76561198000000001,0,'Console','probe',0,@now,@now,'ACTIVE')", new { now = DateTime.UtcNow });
        await plugin.Plugin.CacheManager!.InitializeCacheAsync(config, null, default);
        Assert.True(plugin.Plugin.CacheManager.CheckBan(config, session.SteamId, null, DateTime.UtcNow).IsBanned);
        PlayerManager.ControllerAvailable = _ => false;
        PlayerManager.RetryScheduler = (_, _) => { };
        CS2_SimpleAdmin.PlayerManager.QueueLoad(session, long.MaxValue);
        await world.PumpUntil(() => Runtime.Db!.Pending == 0);
        Assert.Equal(ConnectLoadState.RetryWait, session.LoadState);
    }

    [Fact]
    public async Task BannedPlayerIsKickedExactlyOnceWhenTheControllerComesBack()
    {
        await using var f = await BanFixture.Create();
        var available = false;
        PlayerManager.ControllerAvailable = _ => available;

        CS2_SimpleAdmin.PlayerManager.QueueLoad(f.Session, long.MaxValue);
        await f.World.PumpUntil(() => Runtime.Db!.Pending == 0 && f.Session.LoadState == ConnectLoadState.RetryWait);
        Assert.Equal(0, f.Kicks);
        Assert.Equal(1, f.Retries); // a retry was scheduled, with backoff

        available = true; // the controller is resolvable now; the retry timer fires
        CS2_SimpleAdmin.PlayerManager.QueueLoad(f.Session, long.MaxValue);
        await f.World.PumpUntil(() => f.Kicks > 0 && Runtime.Db!.Pending == 0);
        Assert.Equal(1, f.Kicks);
        Assert.Equal(ConnectLoadState.Loaded, f.Session.LoadState);

        CS2_SimpleAdmin.PlayerManager.QueueLoad(f.Session, long.MaxValue); // already finished: nothing starts again
        CS2_SimpleAdmin.PlayerManager.LoadPendingSessions();
        await Task.Delay(50);
        await f.World.PumpUntil(() => Runtime.Db!.Pending == 0);
        Assert.Equal(1, f.Kicks);
    }

    [Fact]
    public async Task ADisconnectedBannedSessionIsNeverRevived()
    {
        await using var f = await BanFixture.Create();
        PlayerManager.ControllerAvailable = _ => false;
        Runtime.Sessions.End(f.Session.Slot); // the player left while the ban lookup was running

        var queued = Runtime.TryQueueDb<bool>("connect-load-direct", ct =>
            Task.FromResult(true)); // keep the queue honest; the real load below is started through the session API
        Assert.NotNull(queued);
        CS2_SimpleAdmin.PlayerManager.QueueLoad(f.Session, long.MaxValue); // refused: the session is not current any more
        await f.World.PumpUntil(() => Runtime.Db!.Pending == 0);
        Assert.Equal(0, f.Retries);
        Assert.Equal(0, f.Kicks);
    }

    [Fact]
    public async Task SessionEndedWhileTheBanResultWasInFlightIsNotRetried()
    {
        await using var f = await BanFixture.Create();
        // The controller check ends the session first: this is the "slot reused" outcome, not "temporarily unavailable"
        PlayerManager.ControllerAvailable = _ =>
        {
            Runtime.Sessions.End(f.Session.Slot);
            return false;
        };
        CS2_SimpleAdmin.PlayerManager.QueueLoad(f.Session, long.MaxValue);
        await f.World.PumpUntil(() => Runtime.Db!.Pending == 0);
        await f.World.PumpUntil(() => f.World.World.Pending == 0, 200).ContinueWith(_ => { });
        Assert.Equal(0, f.Retries);
        Assert.Equal(0, f.Kicks);
    }

    // =====================================================================================================
    // N5 – the JSON pair commit
    // =====================================================================================================

    // ---- ported review probe (N5, Windows file sharing) ----
    [SkippableFact]
    public async Task FailedSecondAdminFileReplacementMustKeepBothOriginals()
    {
        Skip.If(!OperatingSystem.IsWindows(), "This reproduction uses Windows file-sharing semantics.");
        var dir = Path.Combine(Path.GetTempPath(), "sa_review_pair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var groups = Path.Combine(dir, "groups.json");
        var admins = Path.Combine(dir, "admins.json");
        try
        {
            await File.WriteAllTextAsync(groups, "old-groups");
            await File.WriteAllTextAsync(admins, "old-admins");
            var prepared = new PermissionManager.PreparedAdminReload("new-groups", true, "new-admins", true, []);
            // Windows denies replacing the locked second target; both temp writes still succeed.
            using (var locked = new FileStream(admins, FileMode.Open, FileAccess.Read, FileShare.None))
                await Assert.ThrowsAnyAsync<IOException>(() => new PermissionManager(null).CommitAdminFilesAsync(prepared, dir));
            Assert.Equal("old-groups", await File.ReadAllTextAsync(groups));
            Assert.Equal("old-admins", await File.ReadAllTextAsync(admins));
            Assert.Equal(["admins.json", "groups.json"], Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).Where(f => f != "admin-pair.lock").Order().ToArray()); // no litter (the lock file is permanent by design)
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class PairDir : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "sa_pair_" + Guid.NewGuid().ToString("N"));
        public string Groups => Path.Combine(Dir, "groups.json");
        public string Admins => Path.Combine(Dir, "admins.json");
        private readonly Func<Action<string, string>> _original = () => PermissionManager.ReplaceFile;
        private readonly Action<string, string> _saved;

        public PairDir(bool withOriginals = true)
        {
            Directory.CreateDirectory(Dir);
            _saved = _original();
            if (!withOriginals) return;
            File.WriteAllText(Groups, "old-groups");
            File.WriteAllText(Admins, "old-admins");
        }

        // admin-pair.lock is the pair's permanent lock file (kept on purpose: deleting a lock file is racy); it is not litter
        public string[] Files => Directory.GetFiles(Dir).Select(f => Path.GetFileName(f)!).Where(f => f != "admin-pair.lock").Order().ToArray();

        public void Dispose()
        {
            PermissionManager.ReplaceFile = _saved;
            try { Directory.Delete(Dir, true); } catch { /* best effort */ }
        }
    }

    private static PermissionManager.PreparedAdminReload NewPair() => new("new-groups", true, "new-admins", true, []);

    [Fact]
    public async Task FailureBetweenTheTwoReplacementsRestoresTheFirstFile()
    {
        using var d = new PairDir();
        var calls = 0;
        PermissionManager.ReplaceFile = (source, target) =>
        {
            if (++calls == 2) throw new IOException("injected failure of the second replacement");
            File.Move(source, target, true);
        };
        var ex = await Assert.ThrowsAsync<IOException>(() => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir));
        Assert.Contains("injected", ex.Message);
        Assert.Equal("old-groups", await File.ReadAllTextAsync(d.Groups)); // the first replacement was undone
        Assert.Equal("old-admins", await File.ReadAllTextAsync(d.Admins));
        Assert.Equal(["admins.json", "groups.json"], d.Files); // no temp, backup or journal left behind
    }

    [Fact]
    public async Task FailureOfTheFirstReplacementChangesNothing()
    {
        using var d = new PairDir();
        PermissionManager.ReplaceFile = (_, _) => throw new IOException("injected failure of the first replacement");
        await Assert.ThrowsAsync<IOException>(() => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir));
        Assert.Equal("old-groups", await File.ReadAllTextAsync(d.Groups));
        Assert.Equal("old-admins", await File.ReadAllTextAsync(d.Admins));
        Assert.Equal(["admins.json", "groups.json"], d.Files);
    }

    [Fact]
    public async Task FirstEverCommitWithoutOriginalsIsRolledBackToNothing()
    {
        using var d = new PairDir(withOriginals: false);
        var calls = 0;
        PermissionManager.ReplaceFile = (source, target) =>
        {
            if (++calls == 2) throw new IOException("injected");
            File.Move(source, target, true);
        };
        await Assert.ThrowsAsync<IOException>(() => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir));
        Assert.Empty(d.Files); // groups.json written by the first replacement did not survive
    }

    [Fact]
    public async Task SuccessfulCommitHasBothFilesFromTheSameVersionAndNoLitter()
    {
        using var d = new PairDir();
        await new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir);
        Assert.Equal("new-groups", await File.ReadAllTextAsync(d.Groups));
        Assert.Equal("new-admins", await File.ReadAllTextAsync(d.Admins));
        Assert.Equal(["admins.json", "groups.json"], d.Files);
    }

    [Fact]
    public async Task CancellationBeforeTheReplacementsLeavesTheOriginals()
    {
        using var d = new PairDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir, cts.Token));
        Assert.Equal("old-groups", await File.ReadAllTextAsync(d.Groups));
        Assert.Equal("old-admins", await File.ReadAllTextAsync(d.Admins));
        Assert.Equal(["admins.json", "groups.json"], d.Files);
    }

    [Fact]
    public async Task CancellationDuringThePairDoesNotInterruptTheReplacementsHalfWay()
    {
        using var d = new PairDir();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        PermissionManager.ReplaceFile = (source, target) =>
        {
            if (++calls == 1) cts.Cancel(); // lifetime ends right after the first replacement
            File.Move(source, target, true);
        };
        await new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir, cts.Token);
        Assert.Equal(2, calls);
        Assert.Equal("new-groups", await File.ReadAllTextAsync(d.Groups)); // both replaced: one version
        Assert.Equal("new-admins", await File.ReadAllTextAsync(d.Admins));
    }

    [Fact]
    public async Task AnInterruptedEarlierCommitIsRepairedBeforeTheNextOne()
    {
        using var d = new PairDir();
        // State a crash between the replacements leaves: groups already new, admins old, backups + journal present
        await File.WriteAllTextAsync(d.Groups, "half-new-groups");
        await File.WriteAllTextAsync(d.Groups + ".bak", "old-groups");
        await File.WriteAllTextAsync(d.Admins + ".bak", "old-admins");
        await File.WriteAllTextAsync(Path.Combine(d.Dir, "admin-pair.journal"), "1\n1");

        // The next commit fails too: afterwards the pair must be the repaired ORIGINAL pair, not the half-new one
        PermissionManager.ReplaceFile = (_, _) => throw new IOException("injected");
        await Assert.ThrowsAsync<IOException>(() => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir));
        Assert.Equal("old-groups", await File.ReadAllTextAsync(d.Groups));
        Assert.Equal("old-admins", await File.ReadAllTextAsync(d.Admins));
        Assert.Equal(["admins.json", "groups.json"], d.Files);
    }

    [Fact]
    public async Task UnrestorablePairIsReportedAsInconsistentAndRepairedByTheNextCommit()
    {
        using var d = new PairDir();
        var calls = 0;
        PermissionManager.ReplaceFile = (source, target) =>
        {
            if (++calls == 2)
            {
                // Break the restore too: a directory sits where groups.json has to be copied back
                File.Delete(d.Groups);
                Directory.CreateDirectory(d.Groups);
                throw new IOException("injected");
            }

            File.Move(source, target, true);
        };
        await Assert.ThrowsAsync<PermissionManager.AdminFilesInconsistentException>(
            () => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir));
        Assert.True(File.Exists(Path.Combine(d.Dir, "admin-pair.journal"))); // kept for the repair

        Directory.Delete(d.Groups); // the obstacle is gone
        PermissionManager.ReplaceFile = static (source, target) => File.Move(source, target, true);
        await new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir);
        Assert.Equal("new-groups", await File.ReadAllTextAsync(d.Groups));
        Assert.Equal("new-admins", await File.ReadAllTextAsync(d.Admins));
        Assert.Equal(["admins.json", "groups.json"], d.Files);
    }

    // =====================================================================================================
    // N6 – side effects only after accepted work and a successful SQL
    // =====================================================================================================

    private sealed class RemovalFixture : IDisposable
    {
        public int SqlCalls;
        public int VoiceResets;
        public bool TargetStillThere = true;
        public UnmuteOutcome Outcome = UnmuteOutcome.Removed;
        public readonly List<string> Console = [];
        private readonly Action<string> _printToConsole = CallerRef.PrintToConsole;

        public RemovalFixture()
        {
            PenaltyRemoval.UnmuteSql = (_, _, _, _) => { Interlocked.Increment(ref SqlCalls); return Task.FromResult(Outcome); };
            PenaltyRemoval.IsTargetCurrent = _ => TargetStillThere;
            PenaltyRemoval.ResetVoice = _ => VoiceResets++;
            CallerRef.PrintToConsole = message => Console.Add(message);
            PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 60);
            PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Gag, DateTime.Now.AddHours(1), 60);
        }

        public PenaltyRemoval.Target Target { get; } = new(5, 11, 76561198000000005);

        public bool Muted => PlayerPenaltyManager.IsPenalized(Target.Slot, PenaltyType.Mute, out _);
        public bool Gagged => PlayerPenaltyManager.IsPenalized(Target.Slot, PenaltyType.Gag, out _);

        public bool Queue(int type = 1, bool decrement = false) =>
            PenaltyRemoval.TryQueue(CallerRef.Console, "Console", "76561198000000005", "r", type, Target, decrement);

        public void Dispose()
        {
            CallerRef.PrintToConsole = _printToConsole;
            PenaltyRemoval.RestoreDefaults();
        }
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("DatabaseReady")]
    public void RefusalBecauseThePluginIsNotReadyChangesNothing(string stateName)
    {
        using var world = new TestWorld(state: Enum.Parse<PluginState>(stateName), serverId: 1);
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        using var f = new RemovalFixture();
        Assert.False(f.Queue());
        world.World.RunOneUpdate();
        Assert.True(f.Muted, "the player was unmuted although the operation was refused");
        Assert.True(f.Gagged);
        Assert.Equal(0, f.SqlCalls);
        Assert.Equal(0, f.VoiceResets);
        Assert.Contains(f.Console, m => m.Contains("NOT saved")); // the admin was told
    }

    [Fact]
    public void RefusalBecauseNoServerIdIsResolvedChangesNothing()
    {
        using var world = new TestWorld(state: PluginState.Ready, serverId: null);
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        using var f = new RemovalFixture();
        Assert.False(f.Queue());
        Assert.True(f.Muted);
        Assert.Equal(0, f.SqlCalls);
    }

    [Fact]
    public async Task RefusalBecauseTheQueueIsFullChangesNothing()
    {
        using var world = new TestWorld(dbCapacity: 1);
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        using var f = new RemovalFixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Runtime.TryQueueDb("blocker", async _ => { started.SetResult(); await gate.Task; }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Runtime.TryQueueDb("filler", _ => Task.CompletedTask)); // occupies the single slot
        try
        {
            Assert.False(f.Queue());
            Assert.True(f.Muted);
            Assert.True(f.Gagged);
            Assert.Equal(0, f.VoiceResets);
            Assert.Contains(f.Console, m => m.Contains("NOT saved"));
        }
        finally { gate.SetResult(); }
    }

    [Fact]
    public async Task AcceptedAndSuccessfulSqlRemovesThePenaltyOnTheGameThreadAfterwards()
    {
        using var world = new TestWorld();
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        using var f = new RemovalFixture();
        var release = new TaskCompletionSource<UnmuteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        PenaltyRemoval.UnmuteSql = (_, _, _, _) => { Interlocked.Increment(ref f.SqlCalls); return release.Task; };

        Assert.True(f.Queue());
        await TestWorld.WaitUntil(() => f.SqlCalls == 1);
        world.World.RunOneUpdate();
        Assert.True(f.Muted, "nothing may change while the SQL is still running (and nothing waits for it on the game thread)");
        Assert.Equal(0, f.VoiceResets);

        release.SetResult(UnmuteOutcome.Removed);
        await world.PumpUntil(() => !f.Muted);
        Assert.False(f.Muted);
        Assert.True(f.Gagged, "only the requested penalty type is removed");
        Assert.Equal(1, f.VoiceResets);
    }

    [Fact]
    public async Task GagRemovalDoesNotTouchTheVoiceFlags()
    {
        using var world = new TestWorld();
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        using var f = new RemovalFixture();
        Assert.True(f.Queue(type: 0));
        await world.PumpUntil(() => !f.Gagged);
        Assert.True(f.Muted);
        Assert.Equal(0, f.VoiceResets);
    }

    [Fact]
    public async Task NothingActiveInSqlStillClearsTheStaleInMemoryPenalty()
    {
        using var world = new TestWorld();
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        using var f = new RemovalFixture { Outcome = UnmuteOutcome.NothingActive };
        Assert.True(f.Queue());
        await world.PumpUntil(() => !f.Muted);
    }

    [Fact]
    public async Task FailedSqlKeepsThePenaltyAndTellsTheAdmin()
    {
        using var world = new TestWorld();
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        using var f = new RemovalFixture { Outcome = UnmuteOutcome.Failed };
        Assert.True(f.Queue());
        await world.PumpUntil(() => f.Console.Count > 0);
        Assert.True(f.Muted, "the SQL failed: the player must stay muted");
        Assert.True(f.Gagged);
        Assert.Equal(0, f.VoiceResets);
        Assert.Contains(f.Console, m => m.Contains("could NOT be saved"));
    }

    [Fact]
    public async Task ASlotThatNowHoldsAnotherConnectionIsLeftAlone()
    {
        using var world = new TestWorld();
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        using var f = new RemovalFixture { TargetStillThere = false };
        Assert.True(f.Queue());
        await TestWorld.WaitUntil(() => f.SqlCalls == 1);
        await world.PumpUntil(() => Runtime.Db!.Pending == 0 && world.World.Pending == 0);
        Assert.True(f.Muted, "the newcomer in that slot must keep its own penalties");
        Assert.Equal(0, f.VoiceResets);
    }

    [Fact]
    public async Task NameMatchedRemovalLowersTheCounterOnlyAfterSuccess()
    {
        using var world = new TestWorld();
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        using var f = new RemovalFixture();
        var info = new PlayerInfo(11, 5, new SteamID(76561198000000005), "p", null, 0, 2, 1, 0, 0) { IsLoaded = true };
        CS2_SimpleAdmin.PlayersInfo[76561198000000005] = info;

        f.Outcome = UnmuteOutcome.Failed;
        Assert.True(f.Queue(decrement: true));
        await world.PumpUntil(() => f.Console.Count > 0);
        Assert.Equal(2, info.TotalMutes);

        f.Outcome = UnmuteOutcome.Removed;
        Assert.True(f.Queue(decrement: true));
        await world.PumpUntil(() => info.TotalMutes == 1);
        Assert.Equal(1, info.TotalGags); // only the matching counter
    }

    // ---- the real SQL with the distinguishable outcome (SQLite + every reachable MySQL/MariaDB) ----

    [Theory, MemberData(nameof(Engines))]
    public async Task UnmutePlayerDistinguishesRemovedNothingActiveAndFailed(string engine)
    {
        TestDatabases.SkipIfNoServer(engine);
        await using var db = await TestDatabases.CreateAsync(engine);
        using var world = new TestWorld();
        var config = TestConfig.Use(c => { c.DatabaseConfig.DatabaseType = db.IsSqlite ? "SQLite" : "MySQL"; c.MultiServerMode = true; });
        using var plugin = new TestPlugin(config, db.Provider);
        await using (var c = await db.OpenAsync())
            await c.ExecuteAsync("INSERT INTO sa_mutes (player_steamid, player_name, admin_steamid, admin_name, reason, duration, ends, created, type, server_id) VALUES ('76561198000000042', 'p', 0, 'Console', 'm', 0, @t, @t, 'MUTE', 1)", new { t = DateTime.UtcNow });

        var mutes = plugin.Plugin.MuteManager;
        Assert.Equal(UnmuteOutcome.NothingActive, await mutes.UnmutePlayer("76561198000000042", "Console", "r", 0)); // GAG: none
        Assert.Equal(UnmuteOutcome.Removed, await mutes.UnmutePlayer("76561198000000042", "Console", "r", 1));
        Assert.Equal(UnmuteOutcome.NothingActive, await mutes.UnmutePlayer("76561198000000042", "Console", "r", 1)); // already removed
        await using (var c = await db.OpenAsync())
            Assert.Equal("UNMUTED", await c.ExecuteScalarAsync<string>("SELECT status FROM sa_mutes WHERE player_steamid = '76561198000000042'"));

        var outage = new MuteManager(new OutageProvider());
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        Assert.Equal(UnmuteOutcome.Failed, await outage.UnmutePlayer("76561198000000042", "Console", "r", 1));
    }

    [Fact]
    public async Task RealSqlFailureThroughTheCommandPathKeepsThePlayerMuted()
    {
        await using var db = await TestDatabases.CreateAsync("SQLite");
        using var world = new TestWorld();
        var config = TestConfig.Use(c => { c.DatabaseConfig.DatabaseType = "SQLite"; c.MultiServerMode = true; });
        using var plugin = new TestPlugin(config, new OutageProvider());
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        using var f = new RemovalFixture();
        PenaltyRemoval.UnmuteSql = static (p, a, r, t) => CS2_SimpleAdmin.Instance.MuteManager.UnmutePlayer(p, a, r, t); // the real thing
        Assert.True(f.Queue());
        await world.PumpUntil(() => f.Console.Any(m => m.Contains("could NOT be saved")));
        Assert.True(f.Muted);
        Assert.Equal(0, f.VoiceResets);
    }
}
