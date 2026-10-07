using System.Diagnostics;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// R5: crediting online time to timed mutes (TimeMode 0) is idempotent. The credit is a planned compare-and-set; a
/// retry with the same <see cref="OnlineCredit"/> after a partial failure, a rolled-back or an acknowledged-but-unseen
/// commit, a cancellation or a lost game-thread apply never applies the same minutes twice. A new mute does not
/// receive time from before it existed.
/// </summary>
public class R5_OnlineCreditTests
{
    public static IEnumerable<object[]> Engines() => TestDatabases.All();

    private const ulong Steam = 76561198000000001;
    private static readonly DateTime End = new(2030, 1, 1, 0, 0, 0);

    private static OnlineCredit Credit(ulong steam, int minutes, DateTime? windowEnd = null)
    {
        var end = windowEnd ?? DateTime.Now;
        return new OnlineCredit(steam, minutes, minutes * OnlineCredit.TicksPerMinute, end.AddMinutes(-minutes), end);
    }

    private static async Task<int> AddMute(TestDatabase db, ulong steam, int duration = 60, DateTime? created = null, string type = "MUTE")
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<int>(db.Provider.GetAddMuteQuery(true), new
        {
            playerSteamid = steam, playerName = "p", adminSteamid = 0, adminName = "Console", muteReason = "r",
            duration, ends = End, created = created ?? DateTime.Now.AddHours(-2), type, serverid = 1
        });
    }

    private static async Task<int> Passed(TestDatabase db, int muteId)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<int>("SELECT COALESCE(passed, 0) FROM sa_mutes WHERE id = @muteId", new { muteId });
    }

    private static CS2_SimpleAdminConfig UseConfig(TestDatabase db) =>
        TestConfig.Use(c => { c.MultiServerMode = true; if (db.IsSqlite) c.DatabaseConfig.DatabaseType = "SQLite"; });

    // ---- ported review probe (R5): the SELECT after the UPDATE fails, then the same credit is retried ----
    [Theory, MemberData(nameof(Engines))]
    public async Task RetriedOnlineCreditMustNotApplySuccessfulUpdateTwice(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var mute = await AddMute(db, Steam);
        var provider = new FlakyQueriesProvider(db.Provider) { FailExpiredRead = true };
        var manager = new MuteManager(provider);
        var credit = Credit(Steam, 1); // the SAME object is retried, as the maintenance pass does

        await Assert.ThrowsAnyAsync<Exception>(() => manager.CheckOnlineModeMutesAsync([credit], true, null, default));
        provider.FailExpiredRead = false;
        await manager.CheckOnlineModeMutesAsync([credit], true, null, default);

        Assert.Equal(1, await Passed(db, mute)); // was 2 before the fix
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task FailureBetweenPlanAndApplyThenRetryCreditsOnce(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var mute = await AddMute(db, Steam);
        var manager = new MuteManager(db.Provider);
        var credit = Credit(Steam, 3);
        var fail = true;
        manager.FaultHook = point => fail && point == "after-plan" ? throw new IOException("lost connection") : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => manager.CheckOnlineModeMutesAsync([credit], true, null, default));
        Assert.Equal(0, await Passed(db, mute));
        Assert.NotNull(credit.Plan); // the plan survives; the retry reuses its pre-image

        fail = false;
        await manager.CheckOnlineModeMutesAsync([credit], true, null, default);
        Assert.Equal(3, await Passed(db, mute));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task FailureBeforeCommitRollsBackAndTheRetryAppliesOnce(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var mute = await AddMute(db, Steam);
        var manager = new MuteManager(db.Provider);
        var credit = Credit(Steam, 2);
        var fail = true;
        manager.FaultHook = point => fail && point == "before-commit" ? throw new IOException("died before commit") : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => manager.CheckOnlineModeMutesAsync([credit], true, null, default));
        Assert.Equal(0, await Passed(db, mute)); // transaction rolled back
        Assert.False(credit.Applied);

        fail = false;
        await manager.CheckOnlineModeMutesAsync([credit], true, null, default);
        Assert.Equal(2, await Passed(db, mute));
        Assert.True(credit.Applied);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task AmbiguousCommitAcknowledgementLostRetryDoesNotCreditAgain(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var mute = await AddMute(db, Steam);
        var manager = new MuteManager(db.Provider);
        var credit = Credit(Steam, 1);
        var fail = true;
        // the database committed, but the plugin never learns about it
        manager.FaultHook = point => fail && point == "after-commit" ? throw new IOException("connection reset after commit") : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => manager.CheckOnlineModeMutesAsync([credit], true, null, default));
        Assert.Equal(1, await Passed(db, mute)); // it IS in the database
        Assert.False(credit.Applied);            // but the plugin does not know

        fail = false;
        await manager.CheckOnlineModeMutesAsync([credit], true, null, default);
        await manager.CheckOnlineModeMutesAsync([credit], true, null, default); // and again, for good measure
        Assert.Equal(1, await Passed(db, mute));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task SeveralBatchesWithAFailureInTheMiddleCreditEveryPlayerExactlyOnce(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var muteIds = new List<int>();
        var credits = new List<OnlineCredit>();
        for (var i = 0; i < MuteManager.OnlineBatchSize * 2 + 10; i++)
        {
            var steam = Steam + (ulong)i;
            muteIds.Add(await AddMute(db, steam));
            credits.Add(Credit(steam, 1));
        }

        var manager = new MuteManager(db.Provider);
        var commits = 0;
        var failOnSecondCommit = true;
        manager.FaultHook = point =>
        {
            if (point == "after-commit" && ++commits == 2 && failOnSecondCommit) throw new IOException("batch 2 lost");
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<IOException>(() => manager.CheckOnlineModeMutesAsync(credits, true, null, default));
        Assert.Equal(MuteManager.OnlineBatchSize, credits.Count(c => c.Applied)); // batch 1 committed and acknowledged, batch 2 lost

        failOnSecondCommit = false;
        await manager.CheckOnlineModeMutesAsync(credits, true, null, default);
        Assert.All(credits, c => Assert.True(c.Applied));
        foreach (var id in muteIds) Assert.Equal(1, await Passed(db, id));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task CancellationAfterCommitThenRetryWithAFreshTokenCreditsOnce(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var mute = await AddMute(db, Steam);
        var manager = new MuteManager(db.Provider);
        var credit = Credit(Steam, 1);
        using var cts = new CancellationTokenSource();
        manager.FaultHook = point =>
        {
            if (point == "after-commit") cts.Cancel(); // unload hits right after the commit
            return Task.CompletedTask;
        };

        // the commit is in; the next awaited database call observes the cancellation
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.CheckOnlineModeMutesAsync([credit], true, null, cts.Token));
        Assert.Equal(1, await Passed(db, mute));

        manager.FaultHook = null;
        await manager.CheckOnlineModeMutesAsync([credit], true, null, default);
        Assert.Equal(1, await Passed(db, mute));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task CancellationBeforeAnyWorkChangesNothingAndTheRetryApplies(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var mute = await AddMute(db, Steam);
        var manager = new MuteManager(db.Provider);
        var credit = Credit(Steam, 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.CheckOnlineModeMutesAsync([credit], true, null, cts.Token));
        Assert.Equal(0, await Passed(db, mute));
        await manager.CheckOnlineModeMutesAsync([credit], true, null, default);
        Assert.Equal(1, await Passed(db, mute));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task ACreditWhoseDatabaseWorkIsDoneButWhoseApplyWasLostIsNotAppliedAgainByTheNextPass(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var mute = await AddMute(db, Steam);
        var manager = new MuteManager(db.Provider);
        var session = new PlayerSession(1, 1, Steam, 1, "p", null);
        var nowTicks = session.StartedTimestamp + 3 * OnlineCredit.TicksPerMinute + 5;

        // pass 1: three minutes accrued, database credited, the game-thread fold never ran
        var pass1 = PeriodicMaintenance.ComputeCredits([session], nowTicks, DateTime.Now);
        var credit = Assert.Single(pass1).Credit;
        await manager.CheckOnlineModeMutesAsync([credit], true, null, default);
        Assert.True(credit.Applied);
        Assert.Equal(0, session.CreditedTicks);

        // pass 2: the session still owes the SAME credit, not a new window
        var pass2 = PeriodicMaintenance.ComputeCredits([session], nowTicks + OnlineCredit.TicksPerMinute, DateTime.Now);
        Assert.Same(credit, Assert.Single(pass2).Credit);
        await manager.CheckOnlineModeMutesAsync([credit], true, null, default);
        Assert.Equal(3, await Passed(db, mute)); // 3 minutes once, not 3 + 4

        // fold (game thread), then the next window starts where this one ended
        session.CreditedTicks += credit.Ticks;
        session.PendingCredit = null;
        var pass3 = PeriodicMaintenance.ComputeCredits([session], nowTicks + OnlineCredit.TicksPerMinute, DateTime.Now);
        var next = Assert.Single(pass3).Credit;
        Assert.NotSame(credit, next);
        Assert.Equal(1, next.Minutes);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task MuteIssuedInsideTheWindowOnlyReceivesTheTimeAfterItsCreation(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var end = DateTime.Now;
        var old = await AddMute(db, Steam, created: end.AddHours(-1));                  // existed for the whole window
        var midway = await AddMute(db, Steam, created: end.AddMinutes(-1).AddSeconds(-30)); // issued 1.5 min before the end
        var after = await AddMute(db, Steam, created: end.AddSeconds(30));               // issued after the window
        var manager = new MuteManager(db.Provider);

        await manager.CheckOnlineModeMutesAsync([Credit(Steam, 5, end)], true, null, default);

        Assert.Equal(5, await Passed(db, old));
        Assert.Equal(1, await Passed(db, midway)); // floor(1.5) whole minutes, never the 3.5 earlier ones
        Assert.Equal(0, await Passed(db, after));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task AValueChangedByAnotherWriterIsNeverOverwritten(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var mute = await AddMute(db, Steam);
        var manager = new MuteManager(db.Provider);
        var credit = Credit(Steam, 2);
        var other = true;
        manager.FaultHook = async point =>
        {
            if (point != "after-plan" || !other) return;
            await using var c = await db.OpenAsync();
            await c.ExecuteAsync("UPDATE sa_mutes SET passed = 40 WHERE id = @mute", new { mute }); // e.g. the site
        };

        await manager.CheckOnlineModeMutesAsync([credit], true, null, default);
        Assert.Equal(40, await Passed(db, mute)); // under-crediting is the safe direction
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task UsedUpMutesAreReportedAndNothingIsCreditedPastTheDuration(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        UseConfig(db);
        var mute = await AddMute(db, Steam, duration: 1);
        var manager = new MuteManager(db.Provider);

        var expired = await manager.CheckOnlineModeMutesAsync([Credit(Steam, 1)], true, null, default);
        var row = Assert.Single(expired);
        Assert.Equal((long)Steam, row.SteamId);
        Assert.Equal(End, row.Ends);
        Assert.Equal(1, await Passed(db, mute));

        // an already used-up mute is not credited again, but is still reported
        var again = await manager.CheckOnlineModeMutesAsync([Credit(Steam, 1)], true, null, default);
        Assert.Single(again);
        Assert.Equal(1, await Passed(db, mute));
    }

    [Fact]
    public void ComputeCreditsWindowsNeverOverlapOrRepeat()
    {
        var session = new PlayerSession(1, 1, Steam, 1, "p", null);
        var now = DateTime.Now;
        var t = session.StartedTimestamp;
        var m = OnlineCredit.TicksPerMinute;

        Assert.Empty(PeriodicMaintenance.ComputeCredits([session], t + m - 1, now)); // not a whole minute yet

        var first = Assert.Single(PeriodicMaintenance.ComputeCredits([session], t + 2 * m + 100, now)).Credit;
        Assert.Equal(2, first.Minutes);
        Assert.Equal(2 * m, first.Ticks);
        Assert.Equal(TimeSpan.FromMinutes(2), first.WindowEnd - first.WindowStart);

        // while it is outstanding nothing new is computed, however much time passes
        Assert.Same(first, Assert.Single(PeriodicMaintenance.ComputeCredits([session], t + 9 * m, now)).Credit);

        session.CreditedTicks += first.Ticks;
        session.PendingCredit = null;
        var second = Assert.Single(PeriodicMaintenance.ComputeCredits([session], t + 9 * m, now)).Credit;
        Assert.Equal(7, second.Minutes); // 9 - 2: continues exactly after the first window
    }

    [Fact]
    public void EligibleMinutesRulesForAMuteCreatedInsideTheWindow()
    {
        var end = new DateTime(2026, 5, 1, 12, 0, 0);
        var credit = new OnlineCredit(Steam, 10, 10 * OnlineCredit.TicksPerMinute, end.AddMinutes(-10), end);
        Assert.Equal(10, credit.EligibleMinutes(null));
        Assert.Equal(10, credit.EligibleMinutes(end.AddMinutes(-60)));
        Assert.Equal(10, credit.EligibleMinutes(end.AddMinutes(-10)));      // created exactly at the start
        Assert.Equal(4, credit.EligibleMinutes(end.AddMinutes(-4)));
        Assert.Equal(3, credit.EligibleMinutes(end.AddMinutes(-3).AddSeconds(-59))); // 3.98 min → 3
        Assert.Equal(0, credit.EligibleMinutes(end.AddSeconds(-30)));
        Assert.Equal(0, credit.EligibleMinutes(end));
        Assert.Equal(0, credit.EligibleMinutes(end.AddMinutes(5)));
    }

    [Fact]
    public async Task SetBasedStatementCountDoesNotGrowWithPlayers()
    {
        await using var db = await TestDatabases.CreateAsync("SQLite");
        UseConfig(db);
        for (var i = 0; i < 100; i++) await AddMute(db, Steam + (ulong)i);
        var statements = 0;
        var provider = new CountingApplyProvider(db.Provider, () => Interlocked.Increment(ref statements));
        var credits = Enumerable.Range(0, 100).Select(i => Credit(Steam + (ulong)i, 1)).ToList();

        await new MuteManager(provider).CheckOnlineModeMutesAsync(credits, true, null, default);
        Assert.Equal(2, statements); // ceil(100 / 64) compare-and-set statements, not 100
    }

    private sealed class CountingApplyProvider(Database.IDatabaseProvider inner, Action onApply) : FakeProviderBase
    {
        public override Task<System.Data.Common.DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) =>
            inner.CreateConnectionAsync(cancellationToken);
        public override string GetOnlineCreditPlanQuery(bool multiServer) => inner.GetOnlineCreditPlanQuery(multiServer);
        public override string GetExpiredOnlineMutesBatchQuery(bool multiServer) => inner.GetExpiredOnlineMutesBatchQuery(multiServer);
        public override string GetApplyOnlineCreditQuery(IReadOnlyList<OnlineCreditStep> steps)
        {
            onApply();
            return inner.GetApplyOnlineCreditQuery(steps);
        }
    }
}
