using System.Reflection;
using System.Runtime.Loader;
using CS2_SimpleAdmin.Infrastructure;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// R7: a result is bound to the lifetime (generation, token, dispatcher) and server context captured when the work was
/// accepted, not to whatever is current when the worker finally wants to post.
/// <para>
/// Two different situations, tested separately and not to be confused:
/// (1) same-assembly <c>Runtime.Restart</c>/<c>ResetForTests</c>: the statics are shared, so old work is fenced by its
/// captured <see cref="RuntimeContext"/>; (2) normal CounterStrikeSharp hot reload: a new AssemblyLoadContext with its
/// own copy of the statics (see <see cref="SeparateAssemblyLoadContextHasIndependentRuntimeStatics"/>).
/// </para>
/// </summary>
public class R7_LifetimeBindingTests
{
    // ---- ported review probe (R7): same-assembly restart ----
    [Fact]
    public async Task OldLifetimeMustNotPostIntoNewLifetime()
    {
        var oldWorld = new FakeWorldUpdates();
        Runtime.ResetForTests(true, new GameDispatcher(oldWorld.Schedule));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;
        var job = Runtime.TryQueueDb<int>("old-write", async _ =>
        {
            started.SetResult();
            await gate.Task; // like a legacy SQL call with no lifetime token
            return await Runtime.OnGameThread(() => { applied = true; return 42; });
        })!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newWorld = new FakeWorldUpdates();
        Runtime.ResetForTests(true, new GameDispatcher(newWorld.Schedule));
        try
        {
            gate.SetResult();
            await newWorld.PumpUntil(() => job.IsCompleted, 2000);
            Assert.False(applied, "Old worker action was applied by the new lifetime dispatcher");
            Assert.True(job.IsCanceled || job.IsFaulted, "the stale job must end cancelled, not succeed");
            Assert.Equal(0, newWorld.Scheduled); // nothing was even handed to the new dispatcher
        }
        finally { Runtime.Stop(); }
    }

    [Fact]
    public async Task ActionQueuedBeforeRestartIsDroppedNotRunByTheNewLifetime()
    {
        using var world = new TestWorld();
        var ran = false;
        var posted = Runtime.OnGameThread(() => ran = true); // queued on the old dispatcher, not pumped yet
        var newWorld = new FakeWorldUpdates();
        Runtime.ResetForTests(true, new GameDispatcher(newWorld.Schedule));
        newWorld.RunOneUpdate();
        world.World.RunOneUpdate();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => posted);
        Assert.False(ran);
        Runtime.Stop();
    }

    [Fact]
    public async Task ServerIdIsCapturedWhenWorkIsAcceptedNotWhenItRuns()
    {
        using var world = new TestWorld(serverId: 5);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int? seenByJob = null;
        var job = Runtime.TryQueueDb<bool>("server-scoped", async _ =>
        {
            started.SetResult();
            await gate.Task;
            seenByJob = CS2_SimpleAdmin.ServerId;
            return true;
        })!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        CS2_SimpleAdmin.GlobalServerId = 9; // the global changes while the job is waiting
        gate.SetResult();
        await job;
        Assert.Equal(5, seenByJob);
        Assert.Equal(9, CS2_SimpleAdmin.ServerId); // outside a job the current value is what you get
    }

    [Fact]
    public async Task WorkAcceptedBeforeTheServerWasKnownKeepsNoServerId()
    {
        using var world = new TestWorld(serverId: null);
        int? seen = -1;
        var job = Runtime.TryQueueDb<bool>("global-work", _ =>
        {
            seen = CS2_SimpleAdmin.ServerId;
            return Task.FromResult(true);
        })!;
        CS2_SimpleAdmin.GlobalServerId = 4;
        await job;
        Assert.Null(seen);
    }

    private sealed class GatedProvider(TaskCompletionSource gate) : FakeProviderBase
    {
        public override async Task<(bool Success, string? Exception)> CheckConnectionAsync()
        {
            await gate.Task;
            return (true, null);
        }
    }

    [Fact]
    public async Task SlowStartupOfAnOldLifetimeCannotChangeTheStateOfTheNewOne()
    {
        using var world = new TestWorld(state: PluginState.Starting, serverId: null);
        var oldContext = Runtime.Context;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var init = CS2_SimpleAdmin.InitializeDatabaseAsync(new GatedProvider(gate), CancellationToken.None, context: oldContext);

        using var newWorld = new TestWorld(state: PluginState.Starting, serverId: null); // restart
        gate.SetResult();
        await init;

        Assert.Equal(PluginState.Starting, Runtime.State); // the old startup did not mark the new lifetime DatabaseReady
        Assert.False(Runtime.TrySetState(oldContext, PluginState.Failed, "late failure"));
        Assert.Equal(PluginState.Starting, Runtime.State);
        Assert.Null(Runtime.LastError);
    }

    [Fact]
    public void StaleStartupCannotPublishItsServerId()
    {
        using var world = new TestWorld(state: PluginState.Starting, serverId: null);
        var oldContext = Runtime.Context;
        using var newWorld = new TestWorld(state: PluginState.Starting, serverId: null);
        Assert.False(Runtime.TrySetServerId(oldContext, 77));
        Assert.Null(CS2_SimpleAdmin.GlobalServerId);
        Assert.True(Runtime.TrySetServerId(Runtime.Context, 12));
        Assert.Equal(12, CS2_SimpleAdmin.GlobalServerId);
    }

    [Fact]
    public async Task StaleContextRefusesQueueAndPostWithoutTouchingTheNewLifetime()
    {
        using var world = new TestWorld();
        var oldContext = Runtime.Context;
        using var newWorld = new TestWorld();
        var before = Runtime.Db!.Pending;

        Assert.Null(oldContext.TryQueueDb<int>("late", _ => Task.FromResult(1)));
        Assert.Equal(before, Runtime.Db.Pending);
        Assert.False(oldContext.TryPost(() => throw new InvalidOperationException("must not run")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldContext.PostAsync(() => { }));
    }

    [Fact]
    public void RestartResolvesAServerIdAgain()
    {
        using var world = new TestWorld(serverId: 3);
        Runtime.Stop();
        Runtime.Restart();
        Assert.Null(CS2_SimpleAdmin.GlobalServerId); // a new lifetime must not inherit the previous server id
        Runtime.Stop();
    }

    [Fact]
    public void SeparateAssemblyLoadContextHasIndependentRuntimeStatics()
    {
        // Normal CounterStrikeSharp hot reload loads the plugin into its own AssemblyLoadContext. That is a different
        // mechanism from Runtime.Restart(): there the statics are not shared at all. This only proves the isolation of the
        // statics; the real CSS hot-reload sequence (events, timers, CSS-owned caches) still needs the live test.
        var path = typeof(CS2_SimpleAdmin).Assembly.Location;
        var alc = new AssemblyLoadContext("hot-reload-copy", isCollectible: true);
        try
        {
            var copy = alc.LoadFromAssemblyPath(path);
            var copyRuntime = copy.GetType("CS2_SimpleAdmin.Infrastructure.Runtime")!;
            Assert.NotSame(typeof(Runtime), copyRuntime);

            var copyContext = copyRuntime.GetProperty("Context", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            Assert.NotSame(Runtime.Context, copyContext);

            using var world = new TestWorld();
            var mine = Runtime.Context;
            copyRuntime.GetMethod("Stop", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            Assert.True(mine.IsCurrent, "stopping the other copy's runtime must not affect this one");
        }
        finally
        {
            alc.Unload();
        }
    }
}
