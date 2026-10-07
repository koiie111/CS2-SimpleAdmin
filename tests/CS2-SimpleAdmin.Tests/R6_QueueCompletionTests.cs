using CS2_SimpleAdmin.Infrastructure;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// R6: every accepted typed job reaches a final state exactly once on every path (discard before start, cancellation
/// before/after dequeue, during SQL, rejection, enqueue racing the end of the lifetime). Unload never blocks.
/// </summary>
public class R6_QueueCompletionTests
{
    private static async Task Within(Task task, string what, int ms = 3000)
    {
        var finished = await Task.WhenAny(task, Task.Delay(ms));
        Assert.True(ReferenceEquals(finished, task), what);
    }

    private static async Task<(BoundedWorkQueue Queue, CancellationTokenSource Lifetime, TaskCompletionSource Release)> BusyQueue(
        int capacity = 8)
    {
        var cts = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("probe", capacity, 1, cts.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryEnqueue("block", async _ => { started.SetResult(); await release.Task; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        return (queue, cts, release);
    }

    // ---- ported review probe (R6) ----
    [Fact]
    public async Task QueuedTypedJobMustCompleteWhenLifetimeEnds()
    {
        using var cts = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("probe", 8, 1, cts.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryEnqueue("block", async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = queue.TryEnqueue<int>("typed", _ => Task.FromResult(42))!;
        cts.Cancel();
        queue.Complete();
        await TestWorld.WaitUntil(() => queue.Pending == 0);
        Assert.True(queued.IsCompleted, "Typed Task<T> remains incomplete after its job was discarded on unload");
        Assert.True(queued.IsCanceled);
    }

    [Fact]
    public async Task CompleteDoesNotBlockWhileAJobIsRunning()
    {
        var (queue, cts, release) = await BusyQueue();
        var queued = queue.TryEnqueue<int>("typed", _ => Task.FromResult(1))!;
        cts.Cancel();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        queue.Complete(); // what Unload calls on the game thread
        Assert.True(sw.ElapsedMilliseconds < 200, $"Complete() took {sw.ElapsedMilliseconds} ms");
        await Within(queued, "discarded job must complete without waiting for the running one");
        Assert.True(queued.IsCanceled);
        release.SetResult();
    }

    [Fact]
    public async Task CancelBeforeDequeueCompletesAtOnceAndNeverRuns()
    {
        var (queue, cts, release) = await BusyQueue();
        using var _ = cts;
        using var own = new CancellationTokenSource();
        var ran = 0;
        var queued = queue.TryEnqueue<int>("typed", _ => { Interlocked.Increment(ref ran); return Task.FromResult(1); }, own.Token)!;
        own.Cancel();
        await Within(queued, "caller cancellation must complete the task while the job is still queued");
        Assert.True(queued.IsCanceled);

        release.SetResult();
        await TestWorld.WaitUntil(() => queue.Pending == 0);
        Assert.Equal(0, Volatile.Read(ref ran));

        // The worker survived and keeps serving the queue
        Assert.Equal(7, await queue.TryEnqueue<int>("next", _ => Task.FromResult(7))!);
    }

    [Fact]
    public async Task CancelAfterDequeueFlowsToTheWorkAndTheWorkerSurvives()
    {
        using var lifetime = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("probe", 8, 1, lifetime.Token);
        using var own = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = queue.TryEnqueue<int>("typed", async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct); // honours the linked token
            return 1;
        }, own.Token)!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        own.Cancel();
        await Within(job, "running job must observe its own cancellation");
        Assert.True(job.IsCanceled);
        Assert.Equal(3, await queue.TryEnqueue<int>("after", _ => Task.FromResult(3))!);
    }

    [Fact]
    public async Task CancelDuringSqlThatIgnoresTheTokenCompletesOnceAndLateResultIsDropped()
    {
        using var lifetime = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("probe", 8, 1, lifetime.Token);
        using var own = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = queue.TryEnqueue<int>("legacy-sql", async _ => // like a SQL call that takes no token
        {
            started.SetResult();
            await gate.Task;
            return 99;
        }, own.Token)!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        own.Cancel();
        await Within(job, "awaiting code must be released by the cancellation");
        Assert.True(job.IsCanceled);

        gate.SetResult(); // the late result must not flip a completed task
        await TestWorld.WaitUntil(() => queue.Pending == 0);
        Assert.True(job.IsCanceled);
    }

    [Fact]
    public async Task RejectedWorkReturnsNullAndNeverAHangingTask()
    {
        var (queue, cts, release) = await BusyQueue(capacity: 1);
        using var _ = cts;
        var accepted = queue.TryEnqueue<int>("fills-the-queue", _ => Task.FromResult(1));
        var rejected = queue.TryEnqueue<int>("rejected", _ => Task.FromResult(2));
        Assert.NotNull(accepted);
        Assert.Null(rejected);
        Assert.Equal(1, queue.Rejected);
        release.SetResult();
        Assert.Equal(1, await accepted!);
    }

    [Fact]
    public async Task EnqueueAfterStopIsRejected()
    {
        using var cts = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("probe", 8, 1, cts.Token);
        cts.Cancel();
        Assert.Null(queue.TryEnqueue<int>("late", _ => Task.FromResult(1)));
        Assert.False(queue.TryEnqueue("late", _ => Task.CompletedTask));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task JobFailureAndSuccessCompleteTheirTaskExactlyOnce()
    {
        using var cts = new CancellationTokenSource();
        var queue = new BoundedWorkQueue("probe", 8, 1, cts.Token);
        var ok = queue.TryEnqueue<int>("ok", _ => Task.FromResult(5))!;
        var bad = queue.TryEnqueue<int>("bad", _ => throw new InvalidOperationException("boom"))!;
        Assert.Equal(5, await ok);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad);
        Assert.Equal(1, Interlocked.Read(ref queue.Failed));
    }

    [Fact]
    public async Task RaceBetweenDequeueAndCancellationNeverLeavesATaskPending()
    {
        for (var round = 0; round < 150; round++)
        {
            using var cts = new CancellationTokenSource();
            var queue = new BoundedWorkQueue("race", 64, 2, cts.Token);
            var tasks = new List<Task<int>>();
            for (var i = 0; i < 40; i++)
            {
                var t = queue.TryEnqueue<int>("job", async ct => { await Task.Yield(); return 1; });
                if (t != null) tasks.Add(t);
            }

            if (round % 3 == 0) await Task.Yield();
            cts.Cancel();
            queue.Complete();
            var all = Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
            await Within(all, $"round {round}: an accepted task never completed");
            Assert.All(tasks, t => Assert.True(t.IsCompleted));
        }
    }

    [Fact]
    public async Task EnqueueRacingTheEndOfTheLifetimeNeverStrandsATask()
    {
        for (var round = 0; round < 100; round++)
        {
            using var cts = new CancellationTokenSource();
            var queue = new BoundedWorkQueue("race", 256, 2, cts.Token);
            var tasks = new System.Collections.Concurrent.ConcurrentBag<Task<int>>();
            var producer = Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    var t = queue.TryEnqueue<int>("job", async _ => { await Task.Yield(); return 1; });
                    if (t != null) tasks.Add(t);
                }
            });
            var stopper = Task.Run(() =>
            {
                cts.Cancel();
                queue.Complete();
            });
            await Task.WhenAll(producer, stopper);
            var all = Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
            await Within(all, $"round {round}: a task accepted while stopping never completed");
        }
    }
}
