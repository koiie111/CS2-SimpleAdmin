using System.Diagnostics;
using CS2_SimpleAdmin.Infrastructure;

namespace CS2_SimpleAdmin.Tests;

public class GameDispatcherTests
{
    [Fact]
    public void OnePumpPerUpdate_ItemCountBounded_LeftoversContinueNextUpdate()
    {
        var world = new FakeWorldUpdates();
        var dispatcher = new GameDispatcher(world.Schedule, capacity: 1000, maxItemsPerUpdate: 10, budgetMilliseconds: 1000);
        var ran = 0;
        for (var i = 0; i < 35; i++) Assert.True(dispatcher.TryPost(() => ran++));

        Assert.Equal(1, world.Scheduled); // not one NextWorldUpdate per item
        world.RunOneUpdate();
        Assert.Equal(10, ran);
        world.RunOneUpdate();
        world.RunOneUpdate();
        world.RunOneUpdate();
        Assert.Equal(35, ran);
        Assert.Equal(0, world.Pending);
    }

    [Fact]
    public void TimeBudgetSplitsSlowItemsOverUpdates()
    {
        var world = new FakeWorldUpdates();
        var dispatcher = new GameDispatcher(world.Schedule, capacity: 100, maxItemsPerUpdate: 100, budgetMilliseconds: 2);
        var ran = 0;
        for (var i = 0; i < 10; i++)
            dispatcher.TryPost(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalMilliseconds < 1) { }
                ran++;
            });

        world.RunOneUpdate();
        Assert.InRange(ran, 1, 3); // ~2 ms budget, 1 ms items
    }

    [Fact]
    public async Task TryPostRejectsWhenFull_PostAsyncWaitsForSpace()
    {
        var world = new FakeWorldUpdates();
        var dispatcher = new GameDispatcher(world.Schedule, capacity: 2, maxItemsPerUpdate: 10);
        Assert.True(dispatcher.TryPost(() => { }));
        Assert.True(dispatcher.TryPost(() => { }));
        Assert.False(dispatcher.TryPost(() => { }));

        var waiting = dispatcher.PostAsync(() => { });
        Assert.False(waiting.IsCompleted); // back-pressure, not growth
        // Frees space: the waiting item is queued by a thread-pool continuation and schedules its own pump. Pump world
        // updates until it ran instead of assuming that continuation already happened after two updates (a race on slow machines).
        await world.PumpUntil(() => waiting.IsCompleted, 5_000);
        await waiting;
    }

    [Fact]
    public async Task StopCancelsPendingAndRunsNothingAfterwards()
    {
        var world = new FakeWorldUpdates();
        var dispatcher = new GameDispatcher(world.Schedule);
        var ran = false;
        var pending = dispatcher.PostAsync(() => ran = true);
        dispatcher.Stop();
        world.RunOneUpdate();
        Assert.False(ran);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(dispatcher.TryPost(() => ran = true));
    }

    [Fact]
    public void FailingItemDoesNotStopTheRest()
    {
        var world = new FakeWorldUpdates();
        var dispatcher = new GameDispatcher(world.Schedule, budgetMilliseconds: 1000); // first-call logging JIT must not hit the budget
        var ran = 0;
        dispatcher.TryPost(() => throw new InvalidOperationException("boom"));
        dispatcher.TryPost(() => ran++);
        world.RunOneUpdate();
        Assert.Equal(1, ran);
    }
}

public class BoundedWorkQueueTests
{
    [Fact]
    public async Task EnqueueReturnsImmediatelyWhileTheDatabaseIsSlow_AndQueueIsBounded()
    {
        using var lifetime = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("test", capacity: 8, workers: 2, lifetime.Token);
        var gate = new TaskCompletionSource();
        var callerThread = Environment.CurrentManagedThreadId;
        var ranOnCaller = false;

        var sw = Stopwatch.StartNew();
        var accepted = 0;
        for (var i = 0; i < 20; i++)
        {
            if (queue.TryEnqueue("slow-db", async _ =>
                {
                    if (Environment.CurrentManagedThreadId == callerThread) ranOnCaller = true;
                    await gate.Task; // a DB that does not answer
                }))
                accepted++;
        }

        sw.Stop();
        // 2 running + up to 8 queued; the rest are refused instead of growing the backlog
        Assert.InRange(accepted, 8, 10);
        Assert.Equal(20 - accepted, queue.Rejected);
        Assert.True(sw.ElapsedMilliseconds < 200, $"enqueue took {sw.ElapsedMilliseconds} ms");
        await Task.Delay(50);
        Assert.Equal(2, queue.Running);
        Assert.False(ranOnCaller);

        gate.SetResult();
        await WaitUntil(() => queue.Pending == 0);
        Assert.Equal(accepted, queue.Completed);
    }

    [Fact]
    public async Task UnloadCancelsQueuedWorkWithoutWaiting()
    {
        using var lifetime = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("test", capacity: 10, workers: 1, lifetime.Token);
        var started = new TaskCompletionSource();
        var ran = 0;
        queue.TryEnqueue("long", async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        for (var i = 0; i < 5; i++) queue.TryEnqueue("queued", _ => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        await started.Task;

        var sw = Stopwatch.StartNew();
        lifetime.Cancel(); // what Unload does
        queue.Complete();
        Assert.True(sw.ElapsedMilliseconds < 50);
        await WaitUntil(() => queue.Pending == 0);
        Assert.Equal(0, ran);
        Assert.False(queue.TryEnqueue("after-unload", _ => Task.CompletedTask));
    }

    [Fact]
    public async Task SingleWorkerSerialisesJobs()
    {
        using var lifetime = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("sqlite", capacity: 100, workers: 1, lifetime.Token);
        var concurrent = 0;
        var maxConcurrent = 0;
        for (var i = 0; i < 30; i++)
            queue.TryEnqueue("job", async _ =>
            {
                var c = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, c);
                await Task.Delay(1);
                Interlocked.Decrement(ref concurrent);
            });
        await WaitUntil(() => queue.Pending == 0);
        Assert.Equal(1, maxConcurrent);
    }

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
}

public class PlayerSessionsTests
{
    [Fact]
    public void ConnectAndConnectFullShareOneSession()
    {
        var sessions = new PlayerSessions();
        var a = sessions.BeginOrGet(3, 765611980000001, 12, "a", "1.2.3.4", out var created1);
        var b = sessions.BeginOrGet(3, 765611980000001, 12, "a", "1.2.3.4", out var created2);
        Assert.Same(a, b);
        Assert.True(created1);
        Assert.False(created2);
    }

    [Fact]
    public void ResultForAnOldConnectionIsStale()
    {
        var sessions = new PlayerSessions();
        var first = sessions.BeginOrGet(3, 1, 12, "a", null, out _);

        sessions.End(3); // disconnect while the DB query runs
        Assert.False(sessions.IsCurrent(first));

        var other = sessions.BeginOrGet(3, 2, 13, "b", null, out _); // slot reused by another account
        Assert.False(sessions.IsCurrent(first));
        Assert.True(sessions.IsCurrent(other));

        var rejoin = sessions.BeginOrGet(3, 2, 14, "b", null, out var created); // same account, new userid
        Assert.True(created);
        Assert.False(sessions.IsCurrent(other));
        Assert.True(sessions.IsCurrent(rejoin));

        sessions.Clear(); // map change / unload
        Assert.False(sessions.IsCurrent(rejoin));
    }
}

public class InitializationTests
{
    private sealed class FlakyProvider(int failures, bool migrationFails) : FakeProviderBase
    {
        public int Attempts;
        public int Migrations;

        public override Task<(bool Success, string? Exception)> CheckConnectionAsync()
        {
            Attempts++;
            return Task.FromResult(Attempts > failures ? (true, (string?)null) : (false, (string?)"refused"));
        }

        public override Task DatabaseMigrationAsync()
        {
            Migrations++;
            return migrationFails ? Task.FromException(new Database.MigrationFailedException("016", new Exception("syntax"))) : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ConnectIsRetriedBoundedThenMigrates()
    {
        Runtime.ResetForTests(true, new GameDispatcher(new FakeWorldUpdates().Schedule));
        Runtime.State = PluginState.Starting;
        var provider = new FlakyProvider(2, false);
        var ok = await CS2_SimpleAdmin.InitializeDatabaseAsync(provider, CancellationToken.None, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        Assert.True(ok);
        Assert.Equal(3, provider.Attempts);
        Assert.Equal(1, provider.Migrations);
        Assert.Equal(PluginState.DatabaseReady, Runtime.State);
    }

    [Fact]
    public async Task UnreachableDatabaseEndsInFailedStateInsteadOfThrowing()
    {
        Runtime.ResetForTests(true, new GameDispatcher(new FakeWorldUpdates().Schedule));
        Runtime.State = PluginState.Starting;
        var provider = new FlakyProvider(100, false);
        var ok = await CS2_SimpleAdmin.InitializeDatabaseAsync(provider, CancellationToken.None, [TimeSpan.Zero, TimeSpan.Zero]);
        Assert.False(ok);
        Assert.Equal(3, provider.Attempts); // bounded
        Assert.Equal(0, provider.Migrations);
        Assert.Equal(PluginState.Failed, Runtime.State);
    }

    [Fact]
    public async Task FailedMigrationIsNotReportedAsReady()
    {
        Runtime.ResetForTests(true, new GameDispatcher(new FakeWorldUpdates().Schedule));
        Runtime.State = PluginState.Starting;
        var ok = await CS2_SimpleAdmin.InitializeDatabaseAsync(new FlakyProvider(0, true), CancellationToken.None, []);
        Assert.False(ok);
        Assert.Equal(PluginState.Failed, Runtime.State);
        Assert.Contains("016", Runtime.LastError);
    }
}
