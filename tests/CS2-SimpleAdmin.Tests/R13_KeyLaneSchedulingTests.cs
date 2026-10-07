using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdminApi;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// Fourth review, U1 and U2: per-player ordering under backpressure.
/// U1 – a refused admission must not detach later jobs from earlier accepted ones of the same key.
/// U2 – jobs waiting for their key's earlier job must not hold a worker slot, and the bound on accepted work must
/// include them.
/// The reviewer's five reproductions are ported with their assertions unchanged except where the mechanism they
/// poke at no longer exists (see the comments on <see cref="ARefusedAdmissionNeverTouchesTheLaneOfItsKey"/> and
/// <see cref="ABacklogOfOnePlayerMustNotOccupyAllWorkersAndHoldAnotherPlayerBack"/>); the rest extend them.
/// </summary>
public class R13_KeyLaneSchedulingTests
{
    private const ulong Key = 76561198000000042;

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static BoundedWorkQueue NewQueue(int capacity, int workers, CancellationTokenSource? cts = null) =>
        new("t", capacity, workers, (cts ?? new CancellationTokenSource()).Token);

    // =====================================================================================================
    // U1
    // =====================================================================================================

    /// <summary>
    /// Replaces the reviewer's <c>ReleasingARefusedTicketMustKeepItsPredecessorInTheOrder</c>, which drove the removed
    /// <c>KeyedSequencer.Acquire/Release</c> directly. The same sequence at the new boundary: A accepted and running,
    /// B refused, C accepted → C must still wait for A. Refusal is now decided before the lane is touched.
    /// </summary>
    [Fact]
    public async Task ARefusedAdmissionNeverTouchesTheLaneOfItsKey()
    {
        var queue = NewQueue(capacity: 1, workers: 2);
        var aStarted = Signal();
        var aGate = Signal();
        var cStarted = Signal();
        var blockerStarted = Signal();
        var blocker = Signal();
        Assert.True(queue.TryEnqueue("a", async _ => { aStarted.SetResult(); await aGate.Task; }, null, Key));
        await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(queue.TryEnqueue("blocker", async _ => { blockerStarted.SetResult(); await blocker.Task; }, null, Key + 1));
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); // both workers busy
        Assert.True(queue.TryEnqueue("filler", _ => Task.CompletedTask, null, Key + 2)); // takes the one slot
        // several refusals in a row, for the busy key and for another one
        for (var i = 0; i < 5; i++)
        {
            Assert.False(queue.TryEnqueue("b", _ => Task.CompletedTask, null, Key));
            Assert.False(queue.TryEnqueue("b2", _ => Task.CompletedTask, null, Key + 7));
        }

        Assert.Equal(1, queue.Queued);
        Assert.Equal(3, queue.ActiveKeys); // a, blocker, filler: refusals created no lane
        blocker.SetResult();
        await TestWorld.WaitUntil(() => queue.Queued == 0);
        Assert.True(queue.TryEnqueue("c", _ => { cStarted.SetResult(); return Task.CompletedTask; }, null, Key));
        await Task.WhenAny(cStarted.Task, Task.Delay(300));
        Assert.False(cStarted.Task.IsCompleted, "C overtook A after refusals");
        aGate.SetResult();
        await cStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await TestWorld.WaitUntil(() => queue.Pending == 0 && queue.ActiveKeys == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullQueueRefusalMustNotLetLaterSqlOvertakeEarlierSql(bool typedRefusal)
    {
        using var world = new TestWorld(sqlite: false, dbCapacity: 1);
        var queue = Runtime.Db!;
        var ctx = new WorkContext(Runtime.Context, 1);
        var firstStarted = Signal();
        var firstGate = Signal();
        var otherGates = Enumerable.Range(0, 3).Select(_ => Signal()).ToArray();
        var laterStarted = Signal();
        try
        {
            Assert.True(Runtime.TryQueueDbOrdered("first-slow-sql", async _ =>
            {
                firstStarted.SetResult();
                await firstGate.Task;
            }, ctx, Key));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // Occupy the three other workers, then fill the single waiting slot.
            foreach (var gate in otherGates)
            {
                var started = Signal();
                Assert.True(Runtime.TryQueueDb("other-slow-sql", async _ => { started.SetResult(); await gate.Task; }));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }

            Assert.True(Runtime.TryQueueDb("filler", _ => Task.CompletedTask));
            if (typedRefusal)
                Assert.Null(Runtime.TryQueueDbOrdered<int>("refused", _ => Task.FromResult(0), Key));
            else
                Assert.False(Runtime.TryQueueDbOrdered("refused", _ => Task.CompletedTask, ctx, Key));

            otherGates[0].SetResult();
            await TestWorld.WaitUntil(() => queue.Pending == 3); // filler drained, first + two blockers still run
            Assert.True(Runtime.TryQueueDbOrdered("later-sql", _ =>
            {
                laterStarted.SetResult();
                return Task.CompletedTask;
            }, ctx, Key));
            await Task.WhenAny(laterStarted.Task, Task.Delay(300));
            Assert.False(laterStarted.Task.IsCompleted,
                "Later SQL ran while earlier SQL of the same SteamID was still blocked, after a queue-full rejection");
            firstGate.SetResult();
            await laterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            firstGate.TrySetResult();
            foreach (var gate in otherGates) gate.TrySetResult();
            await TestWorld.WaitUntil(() => queue.Pending == 0);
        }
    }

    [Fact]
    public async Task RefusalMustNotLeaveAnActiveDatabaseMuteAfterTheLaterUnmuteClearedMemory()
    {
        await using var db = await TestDatabases.CreateAsync("SQLite");
        // Real SQLite for data assertions; the 4-worker scheduler models production's MySQL queue.
        using var world = new TestWorld(sqlite: false, dbCapacity: 1);
        var config = TestConfig.Use(c => { c.DatabaseConfig.DatabaseType = "SQLite"; c.MultiServerMode = true; });
        using var plugin = new TestPlugin(config, db.Provider);
        var queue = Runtime.Db!;
        var target = new PenaltyRemoval.Target(5, 11, Key);
        var firstStarted = Signal();
        var firstGate = Signal();
        var otherGates = Enumerable.Range(0, 3).Select(_ => Signal()).ToArray();
        var removeSqlFinished = Signal();
        PenaltyRemoval.IsTargetCurrent = _ => true;
        PenaltyRemoval.ResetVoice = _ => { };
        PenaltyRemoval.UnmuteSql = async (pattern, admin, reason, type) =>
        {
            var result = await plugin.Plugin.MuteManager.UnmutePlayer(pattern, admin, reason, type);
            removeSqlFinished.TrySetResult();
            return result;
        };
        try
        {
            Assert.True(CS2_SimpleAdmin.TryQueuePenaltyWork(CallerRef.Console, null, "first-mute", async _ =>
            {
                firstStarted.SetResult();
                await firstGate.Task;
                await using var c = await db.OpenAsync();
                await c.ExecuteAsync("INSERT INTO sa_mutes(player_steamid,player_name,admin_steamid,admin_name,reason,duration,ends,created,type,server_id) VALUES (@steam,'p',0,'Console','first',0,@t,@t,'MUTE',1)",
                    new { steam = Key.ToString(), t = DateTime.UtcNow });
            }, orderKey: Key));
            PlayerPenaltyManager.AddPenalty(target.Slot, PenaltyType.Mute, DateTime.Now, 0);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            foreach (var gate in otherGates)
            {
                var started = Signal();
                Assert.True(Runtime.TryQueueDb("other", async _ => { started.SetResult(); await gate.Task; }));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }

            Assert.True(Runtime.TryQueueDb("filler", _ => Task.CompletedTask));
            Assert.False(PenaltyRemoval.TryQueue(CallerRef.Console, "0", Key.ToString(), "refused", 1, target, false));
            otherGates[0].SetResult();
            await TestWorld.WaitUntil(() => queue.Pending == 3);
            Assert.True(PenaltyRemoval.TryQueue(CallerRef.Console, "0", Key.ToString(), "later unmute", 1, target, false));
            await Task.WhenAny(removeSqlFinished.Task, Task.Delay(300));
            Assert.False(removeSqlFinished.Task.IsCompleted, "the later unmute's SQL ran before the earlier INSERT");
            firstGate.TrySetResult();
            // 3 gated jobs are left once the INSERT and the removal are done... the two unrelated ones still run
            await world.PumpUntil(() => queue.Pending == 2);
            Assert.False(PlayerPenaltyManager.IsPenalized(target.Slot, PenaltyType.Mute, out _));
            await using var c = await db.OpenAsync();
            var active = await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sa_mutes WHERE reason='first' AND status='ACTIVE'");
            Assert.True(active == 0, $"After earlier mute + refused removal + accepted removal: memory is unmuted, but real SQLite has {active} ACTIVE row(s)");
        }
        finally
        {
            firstGate.TrySetResult();
            foreach (var gate in otherGates) gate.TrySetResult();
            await TestWorld.WaitUntil(() => queue.Pending == 0);
            PenaltyRemoval.RestoreDefaults();
        }
    }

    [Fact]
    public async Task AcceptedRefusedAcceptedWithAnExistingSuccessorKeepsTheOrder()
    {
        // A running, B waiting (successor already accepted), then queue full: X refused; space appears: C accepted.
        var queue = NewQueue(capacity: 2, workers: 3);
        var log = new List<string>();
        void Log(string s) { lock (log) log.Add(s); }
        var aGate = Signal();
        Assert.True(queue.TryEnqueue("a", async _ => { Log("a"); await aGate.Task; }, null, Key));
        await TestWorld.WaitUntil(() => { lock (log) return log.Contains("a"); });
        Assert.True(queue.TryEnqueue("b", _ => { Log("b"); return Task.CompletedTask; }, null, Key));
        Assert.True(queue.TryEnqueue("b2", _ => { Log("b2"); return Task.CompletedTask; }, null, Key));
        Assert.False(queue.TryEnqueue("x", _ => { Log("x"); return Task.CompletedTask; }, null, Key));
        Assert.False(queue.TryEnqueue("x2", _ => { Log("x2"); return Task.CompletedTask; }, null, Key));
        Assert.Equal(2, queue.Waiting);
        Assert.Equal(1, queue.Running); // two successors, one slot used
        aGate.SetResult();
        await TestWorld.WaitUntil(() => queue.Pending == 0);
        Assert.True(queue.TryEnqueue("c", _ => { Log("c"); return Task.CompletedTask; }, null, Key));
        await TestWorld.WaitUntil(() => queue.Pending == 0);
        lock (log) Assert.Equal(new[] { "a", "b", "b2", "c" }, log);
    }

    /// <summary>Predecessor completions race with admissions that are sometimes refused: strict per-key serial FIFO.</summary>
    [Fact]
    public async Task ConcurrentCompletionAndRefusalNeverBreakTheOrderOrOverlap()
    {
        const int keys = 6, perProducer = 4000;
        var queue = NewQueue(capacity: 3, workers: 4);
        var inside = new int[keys];
        var lastSeq = new long[keys];
        var violations = new List<string>();
        var accepted = new long[keys];
        var executed = 0L;

        async Task Producer(int k)
        {
            var seq = 0L;
            for (var i = 0; i < perProducer; i++)
            {
                var my = Interlocked.Increment(ref seq);
                // acceptance order == my order for this key: only one producer per key and the call is synchronous
                var ok = queue.TryEnqueue("j", async _ =>
                {
                    if (Interlocked.Increment(ref inside[k]) != 1)
                        lock (violations) violations.Add($"key {k} overlap at {my}");
                    if (my <= Volatile.Read(ref lastSeq[k]))
                        lock (violations) violations.Add($"key {k}: {my} ran after {lastSeq[k]}");
                    Volatile.Write(ref lastSeq[k], my);
                    if ((my & 7) == 0) await Task.Yield();
                    Interlocked.Decrement(ref inside[k]);
                    Interlocked.Increment(ref executed);
                }, null, (ulong)k);
                if (ok) Interlocked.Increment(ref accepted[k]);
                if ((i & 31) == 0) await Task.Yield();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, keys).Select(k => Task.Run(() => Producer(k))));
        await TestWorld.WaitUntil(() => queue.Pending == 0, 20_000);
        Assert.Empty(violations);
        Assert.Equal(accepted.Sum(), Interlocked.Read(ref executed));
        Assert.True(accepted.Sum() > 0 && accepted.Sum() < keys * perProducer, "the scenario must contain both accepted and refused jobs");
        Assert.Equal(0, queue.ActiveKeys);
        Assert.Equal(0, queue.Queued);
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public async Task RefusalWhileTheRuntimeStopsLeavesNothingBehind()
    {
        var world = new TestWorld(sqlite: false);
        var ctx = new WorkContext(Runtime.Context, 1);
        var gate = Signal();
        var started = Signal();
        Assert.True(Runtime.TryQueueDbOrdered("head", async _ => { started.SetResult(); await gate.Task; }, ctx, Key));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var typed = Runtime.TryQueueDbOrdered<int>("waiting", _ => Task.FromResult(1), Key);
        Assert.NotNull(typed);
        world.Dispose(); // Stop: lanes dropped, waiting job completed as cancelled
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => typed!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(Runtime.TryQueueDbOrdered("late", _ => Task.CompletedTask, ctx, Key));
        Assert.Null(Runtime.TryQueueDbOrdered<int>("late", _ => Task.FromResult(1), Key));
        gate.SetResult();
    }

    // =====================================================================================================
    // U2
    // =====================================================================================================

    /// <summary>
    /// The reviewer's test. Its mid-test wait <c>queue.Running == queue.Workers</c> asserted the defect itself (every
    /// worker slot taken by a job that only waits); with the fix exactly one slot is used, so the wait now asserts
    /// <c>Running == 1</c> and <c>Pending == 4</c>. Everything that checks the property (the other player's job runs
    /// while the first is still gated) is unchanged.
    /// </summary>
    [Fact]
    public async Task ABacklogOfOnePlayerMustNotOccupyAllWorkersAndHoldAnotherPlayerBack()
    {
        using var world = new TestWorld(sqlite: false);
        var queue = Runtime.Db!;
        var ctx = new WorkContext(Runtime.Context, 1);
        var firstGate = Signal();
        var firstStarted = Signal();
        var unrelated = Signal();
        try
        {
            Assert.True(Runtime.TryQueueDbOrdered("one-slow-player", async _ =>
            {
                firstStarted.SetResult();
                await firstGate.Task;
            }, ctx, Key));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (var i = 0; i < 3; i++)
                Assert.True(Runtime.TryQueueDbOrdered("same-player-followup", _ => Task.CompletedTask, ctx, Key));
            await TestWorld.WaitUntil(() => queue.Pending == 4);
            Assert.Equal(1, queue.Running);
            Assert.Equal(3, queue.Waiting);
            Assert.True(Runtime.TryQueueDbOrdered("another-player", _ =>
            {
                unrelated.SetResult();
                return Task.CompletedTask;
            }, ctx, Key + 1));
            await Task.WhenAny(unrelated.Task, Task.Delay(300));
            Assert.True(unrelated.Task.IsCompleted,
                "One player's slow SQL plus three waiting successors occupied all 4 workers and delayed another player's SQL");
        }
        finally
        {
            firstGate.TrySetResult();
            await TestWorld.WaitUntil(() => queue.Pending == 0);
        }
    }

    [Theory]
    [InlineData(1)] // SQLite: one worker
    [InlineData(4)] // MySQL: four
    public async Task ManyWaitingSuccessorsOfOneKeyLeaveEveryOtherKeyServed(int workers)
    {
        var queue = NewQueue(capacity: 256, workers);
        var gate = Signal();
        var headStarted = Signal();
        Assert.True(queue.TryEnqueue("head", async _ => { headStarted.SetResult(); await gate.Task; }, null, Key));
        await headStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var i = 0; i < Math.Max(8, workers * 2); i++)
            Assert.True(queue.TryEnqueue("succ", _ => Task.CompletedTask, null, Key));
        if (workers == 1)
        {
            // one worker: it is busy with the head, so nothing else can run until the head is released... but the
            // successors must not be what keeps it busy afterwards: others interleave ahead of them (fairness test).
            Assert.Equal(1, queue.Running);
            gate.SetResult();
            await TestWorld.WaitUntil(() => queue.Pending == 0);
            return;
        }

        Assert.Equal(1, queue.Running);
        var served = new List<Task>();
        for (var k = 1; k <= 5; k++)
        {
            var done = Signal();
            served.Add(done.Task);
            Assert.True(queue.TryEnqueue("other", _ => { done.SetResult(); return Task.CompletedTask; }, null, Key + (ulong)k));
        }

        await Task.WhenAll(served).WaitAsync(TimeSpan.FromSeconds(2)); // while the head is still gated
        Assert.Equal(1, queue.Running);
        gate.SetResult();
        await TestWorld.WaitUntil(() => queue.Pending == 0 && queue.ActiveKeys == 0);
    }

    [Fact]
    public async Task ReadyKeysAreServedInTurnAHotKeyDoesNotJumpTheLine()
    {
        var queue = NewQueue(capacity: 64, workers: 1);
        var log = new List<string>();
        void Log(string s) { lock (log) log.Add(s); }
        var gate = Signal();
        Assert.True(queue.TryEnqueue("h0", async _ => { Log("h0"); await gate.Task; }, null, Key));
        await TestWorld.WaitUntil(() => { lock (log) return log.Count == 1; });
        for (var i = 1; i <= 3; i++)
        {
            var n = i;
            Assert.True(queue.TryEnqueue("h", _ => { Log($"h{n}"); return Task.CompletedTask; }, null, Key));
        }

        Assert.True(queue.TryEnqueue("y", _ => { Log("y"); return Task.CompletedTask; }, null, Key + 1));
        Assert.True(queue.TryEnqueue("z", _ => { Log("z"); return Task.CompletedTask; }, null, Key + 2));
        gate.SetResult();
        await TestWorld.WaitUntil(() => queue.Pending == 0);
        // h1 re-enters behind the keys that were already ready; the hot key then alternates with nobody else waiting
        lock (log) Assert.Equal(new[] { "h0", "y", "z", "h1", "h2", "h3" }, log);
    }

    [Fact]
    public async Task TotalCapacityCoversWaitingJobsToo()
    {
        var queue = NewQueue(capacity: 5, workers: 4);
        var gate = Signal();
        var started = Signal();
        Assert.True(queue.TryEnqueue("head", async _ => { started.SetResult(); await gate.Task; }, null, Key));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 5; i++) Assert.True(queue.TryEnqueue("w", _ => Task.CompletedTask, null, Key));
        Assert.Equal(5, queue.Waiting);
        Assert.Equal(5, queue.Queued);
        Assert.False(queue.TryEnqueue("w6", _ => Task.CompletedTask, null, Key)); // same key: refused, memory stays bounded
        Assert.False(queue.TryEnqueue("o", _ => Task.CompletedTask, null, Key + 1)); // other key: also refused, the bound is shared
        Assert.False(queue.TryEnqueue("plain", _ => Task.CompletedTask));
        gate.SetResult();
        await TestWorld.WaitUntil(() => queue.Pending == 0 && queue.Queued == 0);
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public async Task AFailedHeadStillLetsTheSuccessorsRunAndTheLaneIsRemoved()
    {
        var queue = NewQueue(capacity: 16, workers: 2);
        var gate = Signal();
        var started = Signal();
        Assert.True(queue.TryEnqueue("head", async _ => { started.SetResult(); await gate.Task; throw new InvalidOperationException("sql failed"); }, null, Key));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var next = queue.TryEnqueue<int>("next", _ => Task.FromResult(7), default, null, Key);
        Assert.NotNull(next);
        gate.SetResult();
        Assert.Equal(7, await next!.WaitAsync(TimeSpan.FromSeconds(2)));
        await TestWorld.WaitUntil(() => queue.Pending == 0 && queue.ActiveKeys == 0);
        Assert.Equal(1, queue.Failed);
    }

    [Fact]
    public async Task ACancelledWaitingJobPassesTheLaneOn()
    {
        var queue = NewQueue(capacity: 16, workers: 2);
        var gate = Signal();
        var started = Signal();
        using var cancel = new CancellationTokenSource();
        Assert.True(queue.TryEnqueue("head", async _ => { started.SetResult(); await gate.Task; }, null, Key));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var middle = queue.TryEnqueue<int>("middle", _ => Task.FromResult(1), cancel.Token, null, Key);
        var last = queue.TryEnqueue<int>("last", _ => Task.FromResult(2), default, null, Key);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middle!.WaitAsync(TimeSpan.FromSeconds(2)));
        gate.SetResult();
        Assert.Equal(2, await last!.WaitAsync(TimeSpan.FromSeconds(2)));
        await TestWorld.WaitUntil(() => queue.Pending == 0 && queue.ActiveKeys == 0);
    }

    [Fact]
    public async Task UnloadCompletesEveryWaitingAwaiterAndKeepsTheCountersHonest()
    {
        var cts = new CancellationTokenSource();
        var queue = NewQueue(capacity: 16, workers: 2, cts);
        var gate = Signal();
        var started = Signal();
        Assert.True(queue.TryEnqueue("head", async ct => { started.SetResult(); await gate.Task; ct.ThrowIfCancellationRequested(); }, null, Key));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var waiting = Enumerable.Range(0, 5).Select(_ => queue.TryEnqueue<int>("w", _ => Task.FromResult(1), default, null, Key)!).ToArray();
        cts.Cancel();
        queue.Complete();
        foreach (var task in waiting)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, queue.Queued);
        Assert.Equal(0, queue.Waiting);
        Assert.Equal(0, queue.ActiveKeys);
        gate.SetResult(); // the running head ends later: its slot is released honestly, nothing is promoted
        await TestWorld.WaitUntil(() => queue.Pending == 0 && queue.Running == 0);
        Assert.False(queue.TryEnqueue("late", _ => Task.CompletedTask, null, Key));
    }

    [Fact]
    public async Task RestartDoesNotInheritLanesOfTheDeadLifetime()
    {
        using var world = new TestWorld(sqlite: false);
        var gate = Signal();
        var started = Signal();
        Assert.True(Runtime.TryQueueDbOrdered("stuck", async _ => { started.SetResult(); await gate.Task; },
            new WorkContext(Runtime.Context, 1), Key));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Runtime.TryQueueDbOrdered("behind", _ => Task.CompletedTask, new WorkContext(Runtime.Context, 1), Key));

        using var next = new TestWorld(sqlite: false);
        Assert.Equal(0, Runtime.Db!.ActiveKeys);
        var ran = Signal();
        Assert.True(Runtime.TryQueueDbOrdered("fresh", _ => { ran.SetResult(); return Task.CompletedTask; },
            new WorkContext(Runtime.Context, 1), Key));
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gate.SetResult();
    }

    [Fact]
    public async Task TypedConnectLoadsAmongOtherKeysAreNotHeldByAStuckKey()
    {
        using var world = new TestWorld(sqlite: false);
        var queue = Runtime.Db!;
        var gate = Signal();
        var started = Signal();
        Assert.True(Runtime.TryQueueDbOrdered("slow", async _ => { started.SetResult(); await gate.Task; },
            new WorkContext(Runtime.Context, 1), Key));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var sameKey = Enumerable.Range(0, 6).Select(_ => Runtime.TryQueueDbOrdered<bool>("connect-load", _ => Task.FromResult(true), Key)!).ToArray();
        var others = Enumerable.Range(1, 20).Select(i => Runtime.TryQueueDbOrdered<bool>("connect-load", _ => Task.FromResult(true), Key + (ulong)i)!).ToArray();
        await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(sameKey, t => Assert.False(t.IsCompleted));
        Assert.Equal(1, queue.Running);
        gate.SetResult();
        await Task.WhenAll(sameKey).WaitAsync(TimeSpan.FromSeconds(2));
    }
}
