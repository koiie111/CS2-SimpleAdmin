using System.Diagnostics;
using System.Threading.Channels;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Fixed number of background workers reading from a bounded channel.
/// <list type="bullet">
/// <item>The number of queued jobs is capped (<see cref="Capacity"/>); <see cref="TryEnqueue"/> returns false
/// instead of growing the queue. Callers of mandatory writes (bans, mutes) must report that refusal.</item>
/// <item>At most <see cref="Workers"/> jobs run at once. For SQLite (whose System.Data.SQLite "Async" API is
/// synchronous) a single worker serialises all database access off the game thread.</item>
/// <item>Enqueueing never runs any part of the job on the caller's (game) thread.</item>
/// <item>Jobs get the lifetime token; when the plugin unloads, queued jobs are skipped and workers exit.</item>
/// <item><b>Completion contract:</b> every job that was accepted (<see cref="TryEnqueue{T}"/> returned a task)
/// reaches a final state exactly once: success, fault, or cancellation. A job that is discarded before it starts
/// (unload, caller cancellation) is completed as cancelled by whoever discards it, so awaiting code never hangs.</item>
/// <item>Each job carries the <see cref="WorkContext"/> captured when it was accepted; the worker exposes it
/// as <see cref="WorkContext.Current"/> while the job runs, so results can only be applied by the lifetime
/// (and for the server) the work was accepted for.</item>
/// </list>
/// </summary>
internal sealed class BoundedWorkQueue
{
    private sealed class Job(string operation, Func<CancellationToken, Task> work, Action discard, WorkContext? context,
        CancellationToken callerToken, long enqueued)
    {
        public readonly string Operation = operation;
        public readonly Func<CancellationToken, Task> Work = work;

        /// <summary>Completes the job's result as cancelled; idempotent (the TCS ignores a second completion).</summary>
        public readonly Action Discard = discard;

        public readonly WorkContext? Context = context;
        public readonly CancellationToken CallerToken = callerToken;
        public readonly long Enqueued = enqueued;
    }

    private readonly Channel<Job> _channel;
    private readonly CancellationToken _lifetime;
    private readonly string _name;
    private readonly Func<WorkContext?>? _captureContext;
    private readonly LatencyHistogram? _waitHistogram;
    private int _pending;
    private int _running;

    public BoundedWorkQueue(string name, int capacity, int workers, CancellationToken lifetime,
        LatencyHistogram? waitHistogram = null, Func<WorkContext?>? captureContext = null)
    {
        _name = name;
        Capacity = capacity;
        Workers = workers;
        _lifetime = lifetime;
        _waitHistogram = waitHistogram;
        _captureContext = captureContext;
        _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full
            // Several parties may drain (workers, Complete, the enqueue-after-stop race), so never single-reader
            SingleReader = false,
            SingleWriter = false
        });

        for (var i = 0; i < workers; i++)
            _ = Task.Run(WorkerLoop);
    }

    public int Capacity { get; }
    public int Workers { get; }
    public int Pending => Volatile.Read(ref _pending);
    public int Running => Volatile.Read(ref _running);

    public long Completed;
    public long Failed;
    public long Rejected;
    public long Discarded;

    /// <summary>Queues a job. Returns false (without running anything) when the queue is full or stopped.</summary>
    public bool TryEnqueue(string operation, Func<CancellationToken, Task> work, WorkContext? context = null) =>
        TryEnqueueCore(operation, work, static () => { }, default, context);

    private bool TryEnqueueCore(string operation, Func<CancellationToken, Task> work, Action discard, CancellationToken callerToken,
        WorkContext? explicitContext = null)
    {
        if (_lifetime.IsCancellationRequested || callerToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref Rejected);
            return false;
        }

        // Context is captured here, on the caller's thread, at the moment the work is accepted
        var job = new Job(operation, work, discard, explicitContext ?? _captureContext?.Invoke(), callerToken, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(job))
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref Rejected);
            Interlocked.Increment(ref PluginMetrics.DbJobsRejected);
            RateLimitedLog.Warning($"{_name}.full", $"{_name} queue is full ({Capacity}); rejected '{operation}'");
            return false;
        }

        // The lifetime may have ended between the check above and the write, after the workers already left:
        // nobody would ever read this job. Drain from here so its completion is not lost.
        if (_lifetime.IsCancellationRequested) DiscardQueued();
        return true;
    }

    /// <summary>
    /// Queues a job with a result. Returns null when rejected. The returned task always completes: with the result,
    /// the job's exception, or cancellation when the job was discarded (unload / <paramref name="cancellationToken"/>)
    /// before it started or was cancelled while running.
    /// </summary>
    /// <param name="operation">Name used in logs and metrics.</param>
    /// <param name="work">The job.</param>
    /// <param name="cancellationToken">Cancels this one job: if it is still queued it is skipped and the task
    /// is cancelled at once; if running, the token reaches the work (linked with the lifetime token).</param>
    public Task<T>? TryEnqueue<T>(string operation, Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = TryEnqueueCore(operation, async ct =>
        {
            using var linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken)
                : null;
            var token = linked?.Token ?? ct;
            try
            {
                tcs.TrySetResult(await work(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
                throw;
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                throw;
            }
        }, () => tcs.TrySetCanceled(), cancellationToken);

        if (!accepted) return null;
        // Caller cancellation completes the task immediately even though the job is still waiting in the queue;
        // the worker then skips it when it gets there (see WorkerLoop).
        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            tcs.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Dispose(), registration,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return tcs.Task;
    }

    /// <summary>Stops accepting work and completes whatever is still queued as cancelled. Never blocks.</summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
        DiscardQueued();
    }

    private void DiscardQueued()
    {
        while (_channel.Reader.TryRead(out var job)) DiscardJob(job);
    }

    private void DiscardJob(Job job)
    {
        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref Discarded);
        try
        {
            job.Discard();
        }
        catch (Exception ex)
        {
            RateLimitedLog.Error($"{_name}.discard", ex, $"Discarding job '{job.Operation}' failed");
        }
    }

    private async Task WorkerLoop()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_lifetime).ConfigureAwait(false))
            {
                while (reader.TryRead(out var job))
                {
                    _waitHistogram?.RecordSince(job.Enqueued);
                    if (_lifetime.IsCancellationRequested || job.CallerToken.IsCancellationRequested)
                    {
                        // Cancelled before it started: complete it (exactly once) instead of silently dropping it
                        DiscardJob(job);
                        if (_lifetime.IsCancellationRequested) return;
                        continue;
                    }

                    Interlocked.Increment(ref _running);
                    WorkContext.Current = job.Context;
                    try
                    {
                        await job.Work(_lifetime).ConfigureAwait(false);
                        Interlocked.Increment(ref Completed);
                        Interlocked.Increment(ref PluginMetrics.DbJobsCompleted);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OperationCanceledException) when (job.CallerToken.IsCancellationRequested)
                    {
                        // Only this job was cancelled; the worker keeps serving the queue
                        Interlocked.Increment(ref Discarded);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref Failed);
                        Interlocked.Increment(ref PluginMetrics.DbJobsFailed);
                        RateLimitedLog.Error($"{_name}.{job.Operation}", ex, $"Background job '{job.Operation}' failed");
                    }
                    finally
                    {
                        WorkContext.Current = null;
                        Interlocked.Decrement(ref _running);
                        Interlocked.Decrement(ref _pending);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Complete (as cancelled) whatever is still queued so Pending reflects reality and no awaiter hangs
            DiscardQueued();
        }
    }

    public string Describe() =>
        $"{_name}: pending={Pending}/{Capacity} running={Running}/{Workers} completed={Interlocked.Read(ref Completed)} failed={Interlocked.Read(ref Failed)} rejected={Interlocked.Read(ref Rejected)} discarded={Interlocked.Read(ref Discarded)}";
}
