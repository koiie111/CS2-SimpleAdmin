using System.Diagnostics;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdminApi;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// R1: a connect load that the database accepted but that failed must be retried (bounded, with backoff); applied
/// exactly once; never for a disconnected/replaced session; never as a duplicate of a load that is still running.
/// The engine-facing parts of the apply (controller lookup, voice flags, kick) are seams, the state logic is real.
/// </summary>
public class R1_ConnectLoadRetryTests
{
    private sealed class Harness : IDisposable
    {
        private readonly TestWorld _world;
        private readonly TestPlugin _plugin;
        public readonly List<(TimeSpan Delay, Action Callback)> Scheduled = [];
        public readonly List<PlayerSession> Applied = [];
        public readonly PlayerManager Manager = new();
        public readonly CS2_SimpleAdminConfig Config;

        public Harness(Database.IDatabaseProvider? provider = null, int dbCapacity = Runtime.DbQueueCapacity)
        {
            _world = new TestWorld(dbCapacity: dbCapacity);
            Config = TestConfig.Use(c => c.OtherSettings.CheckMultiAccountsByIp = false);
            _plugin = new TestPlugin(Config, provider ?? new OutageProvider());
            PlayerManager.RetryScheduler = (delay, callback) => Scheduled.Add((delay, callback));
            PlayerManager.ControllerAvailable = _ => true;
            PlayerManager.NativeEffects = (session, _, _, _) => Applied.Add(session);
            PlayerManager.KickBanned = _ => { };
        }

        public TestWorld World => _world;
        public TestPlugin Plugin => _plugin;

        public PlayerSession Connect(int slot = 1, ulong steam = 76561198000000001)
            => Runtime.Sessions.BeginOrGet(slot, steam, slot + 100, "probe", null, out _);

        /// <summary>Pumps game-thread work until the session leaves InFlight.</summary>
        public Task Settle(PlayerSession s) => _world.PumpUntil(() => s.LoadState != ConnectLoadState.InFlight);

        public void Dispose()
        {
            _plugin.Dispose();
            _world.Dispose();
        }
    }

    // ---- ported review probe (R1): the assertion is the same intent, expressed on the explicit state ----
    [Fact]
    public async Task FailedAcceptedConnectLoadMustBecomeRetryable()
    {
        using var h = new Harness();
        var session = h.Connect();
        h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());
        await h.Settle(session);

        Assert.Equal(ConnectLoadState.RetryWait, session.LoadState); // not permanently "queued"
        Assert.Equal(1, session.LoadAttempts);
        var retry = Assert.Single(h.Scheduled);
        Assert.InRange(retry.Delay.TotalSeconds, 2, 3);
    }

    private static async Task<(Harness H, FlakyQueriesProvider Provider, TestDatabase Db)> WithDatabase(int dbCapacity = Runtime.DbQueueCapacity)
    {
        var db = await TestDatabases.CreateAsync("SQLite");
        var provider = new FlakyQueriesProvider(db.Provider);
        var h = new Harness(provider: provider, dbCapacity: dbCapacity);
        await using (var c = await db.OpenAsync())
            await c.ExecuteAsync(db.Provider.GetAddMuteQuery(true), new
            {
                playerSteamid = 76561198000000001UL, playerName = "p", adminSteamid = 0, adminName = "Console", muteReason = "r",
                duration = 60, ends = DateTime.Now.AddHours(1), created = DateTime.Now, type = "MUTE", serverid = 1
            });
        return (h, provider, db);
    }

    [Fact]
    public async Task StatsFailureThenRecoveryAppliesExactlyOnce()
    {
        var (h, provider, db) = await WithDatabase();
        using var _h = h;
        await using var _db = db;
        provider.FailStats = true;
        var session = h.Connect();

        h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());
        await h.Settle(session);
        Assert.Equal(ConnectLoadState.RetryWait, session.LoadState);
        Assert.Empty(h.Applied);

        provider.FailStats = false; // database recovers
        h.Scheduled[^1].Callback();  // the backoff timer fires
        await h.Settle(session);

        Assert.Equal(ConnectLoadState.Loaded, session.LoadState);
        Assert.Single(h.Applied);
        Assert.Equal(2, session.LoadAttempts);
        Assert.Single(PlayerPenaltyManager.GetPlayerPenalties(session.Slot, PenaltyType.Mute)); // one mute, applied once
    }

    [Fact]
    public async Task ReadMutesFailureThenRecoveryAppliesExactlyOnce()
    {
        var (h, provider, db) = await WithDatabase();
        using var _h = h;
        await using var _db = db;
        provider.FailMutes = true; // stats succeed, the active-mutes read fails
        var session = h.Connect();

        h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());
        await h.Settle(session);
        Assert.Equal(ConnectLoadState.RetryWait, session.LoadState);
        Assert.Empty(PlayerPenaltyManager.GetPlayerPenalties(session.Slot, PenaltyType.Mute)); // nothing half-applied
        Assert.False(CS2_SimpleAdmin.PlayersInfo.ContainsKey(session.SteamId));

        provider.FailMutes = false;
        h.Scheduled[^1].Callback();
        await h.Settle(session);

        Assert.Equal(ConnectLoadState.Loaded, session.LoadState);
        Assert.Single(h.Applied);
        Assert.Single(PlayerPenaltyManager.GetPlayerPenalties(session.Slot, PenaltyType.Mute));
        Assert.True(CS2_SimpleAdmin.PlayersInfo.TryGetValue(session.SteamId, out var info) && info.IsLoaded);
    }

    [Fact]
    public async Task QueueFullDoesNotConsumeAnAttemptAndIsRetried()
    {
        var (h, provider, db) = await WithDatabase(dbCapacity: 1);
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Runtime.TryQueueDb("blocker", async _ => { started.SetResult(); await release.Task; });
        await started.Task;
        Assert.True(Runtime.TryQueueDb("filler", _ => Task.CompletedTask)); // the single slot is now taken

        h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());
        Assert.Equal(ConnectLoadState.RetryWait, session.LoadState);
        Assert.Equal(0, session.LoadAttempts); // nothing was tried
        var retry = Assert.Single(h.Scheduled);
        Assert.Equal(LoadRetryPolicy.QueueFullDelay, retry.Delay);

        release.SetResult();
        await TestWorld.WaitUntil(() => Runtime.Db!.Pending == 0);
        retry.Callback();
        await h.Settle(session);
        Assert.Equal(ConnectLoadState.Loaded, session.LoadState);
        Assert.Equal(1, session.LoadAttempts);
    }

    [Fact]
    public async Task ConnectAndFullConnectDoNotStartTwoLoads()
    {
        var (h, provider, db) = await WithDatabase();
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        var now = Stopwatch.GetTimestamp();

        h.Manager.QueueLoad(session, now);
        h.Manager.QueueLoad(session, now);     // player_connect_full
        h.Manager.LoadPendingSessions();       // periodic pass while the first is still running
        await h.Settle(session);

        Assert.Equal(1, session.LoadAttempts);
        Assert.Single(h.Applied);
        h.Manager.LoadPendingSessions();       // loaded sessions are left alone
        Assert.Equal(1, session.LoadAttempts);
    }

    [Fact]
    public async Task DisconnectedSessionIsNeverRevivedByARetry()
    {
        using var h = new Harness();
        var session = h.Connect();
        h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());
        await h.Settle(session);
        var outage = (OutageProvider)CS2_SimpleAdmin.DatabaseProvider!;
        var connections = outage.Attempts;

        Runtime.Sessions.End(session.Slot); // the player disconnects while a retry is pending
        h.Scheduled[^1].Callback();         // timer fires later
        h.Manager.LoadPendingSessions();
        await Task.Delay(50);

        Assert.Equal(connections, outage.Attempts);
        Assert.Equal(1, session.LoadAttempts);
    }

    [Fact]
    public async Task SlotReuseByAnotherAccountIsNotLoadedAsTheOldOne()
    {
        using var h = new Harness();
        var first = h.Connect(slot: 2, steam: 76561198000000002);
        h.Manager.QueueLoad(first, Stopwatch.GetTimestamp());
        await h.Settle(first);
        var second = Runtime.Sessions.BeginOrGet(2, 76561198000000003, 999, "new", null, out _); // slot reused

        h.Scheduled[^1].Callback(); // retry of the first connection
        await Task.Delay(30);
        Assert.Equal(1, first.LoadAttempts);
        Assert.Equal(ConnectLoadState.Pending, second.LoadState);
    }

    [Fact]
    public async Task UnavailableControllerAtApplyTimeIsRetriedNotLost()
    {
        var (h, provider, db) = await WithDatabase();
        using var _h = h;
        await using var _db = db;
        var available = false;
        PlayerManager.ControllerAvailable = _ => available;
        var session = h.Connect();

        h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());
        await h.Settle(session);
        Assert.Equal(ConnectLoadState.RetryWait, session.LoadState);
        Assert.Empty(h.Applied);

        available = true;
        h.Scheduled[^1].Callback();
        await h.Settle(session);
        Assert.Equal(ConnectLoadState.Loaded, session.LoadState);
        Assert.Single(h.Applied);
    }

    [Fact]
    public void LateResultOfAnOlderAttemptCannotMoveTheState()
    {
        var session = new PlayerSession(1, 1, 1, 1, "x", null);
        var t0 = Stopwatch.GetTimestamp();
        var first = session.TryBeginLoad(t0);
        Assert.Equal(1, first);
        Assert.True(session.FailLoad(first, t0, out _));
        Assert.Equal(0, session.TryBeginLoad(t0));                 // backoff not elapsed
        var second = session.TryBeginLoad(long.MaxValue);          // timer fired
        Assert.Equal(2, second);

        Assert.False(session.FailLoad(first, t0, out _));          // late failure of attempt 1
        Assert.False(session.CompleteLoad(first));                 // late success of attempt 1
        Assert.Equal(ConnectLoadState.InFlight, session.LoadState);
        Assert.True(session.CompleteLoad(second));
        Assert.False(session.FailLoad(second, t0, out _));         // cannot fail after completion
        Assert.Equal(ConnectLoadState.Loaded, session.LoadState);
    }

    [Fact]
    public void BackoffIsBoundedAndSlowerThanEveryFrame()
    {
        var previous = TimeSpan.Zero;
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            var delay = LoadRetryPolicy.Delay(attempt, 76561198000000000UL + (ulong)attempt);
            Assert.InRange(delay.TotalSeconds, 2, 75);
            if (attempt <= 5) Assert.True(delay >= previous || attempt == 1, "backoff must not shrink while ramping up");
            previous = delay;
        }
    }

    [Fact]
    public async Task RetryTimerOfAnOldLifetimeDoesNothing()
    {
        using var h = new Harness();
        var session = h.Connect();
        h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());
        await h.Settle(session);
        var outage = (OutageProvider)CS2_SimpleAdmin.DatabaseProvider!;
        var connections = outage.Attempts;

        var timer = h.Scheduled[^1].Callback;
        Runtime.ResetForTests(true, new GameDispatcher(h.World.World.Schedule)); // restart: new lifetime
        timer();
        await Task.Delay(30);
        Assert.Equal(connections, outage.Attempts);
    }
}
