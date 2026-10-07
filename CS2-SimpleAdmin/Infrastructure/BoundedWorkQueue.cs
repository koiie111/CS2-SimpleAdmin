using System.Diagnostics;
using System.Threading.Channels;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Fixed number of background workers reading from a bounded channel.
/// <list type="bullet">
/// <item>The number of accepted-but-not-started jobs is capped (<see cref="Capacity"/>); <see cref="TryEnqueue(string, Func{CancellationToken, Task}, WorkContext?)"/>
/// returns false instead of growing the queue. Callers of mandatory writes (bans, mutes) must report that refusal.</item>
/// <item>At most <see cref="Workers"/> jobs run at once. For SQLite (whose System.Data.SQLite "Async" API is
/// synchronous) a single worker serialises all database access off the game thread.</item>
/// <item>Enqueueing never runs any part of the job on the caller's (game) thread.</item>
/// <item>Jobs get the lifetime token; when the plugin unloads, queued jobs are skipped and workers exit.</item>
/// <item><b>Completion contract:</b> every job that was accepted (<see cref="TryEnqueue{T}"/> returned a task)
/// reaches a final state exactly once: success, fault, or cancellation. A job that is discarded before it starts
/// (unload, caller cancellation) is completed as cancelled by whoever discards it, so awaiting code never hangs.</item>
/// <item><b>Per-key order</b> (the overloads taking an <c>orderKey</c>, a SteamID64). Jobs with the same key run one
/// at a time, in acceptance order. The order is kept by the queue itself, <i>before</i> a worker is involved: each key
/// has a lane, and only the <b>head</b> job of a lane is ever in the channel or running. Later jobs of the key wait in
/// the lane as plain data (no worker, no task, no OS thread). When the head finishes (success, failure, cancellation,
/// unload) or is discarded before it starts, the next job of the lane moves to the <i>tail</i> of the channel: ready
/// keys are served in turn, a hot key cannot jump the line, and a worker slot is only given to an operation that can
/// start now. Capacity covers channel and lanes together (<see cref="Queued"/> ≤ <see cref="Capacity"/>), so memory
/// stays bounded however long one key's head runs.</item>
/// <item><b>Refusal never touches the order.</b> Admission first reserves capacity (one atomic counter), and only then
/// publishes the job to the channel or to its lane. A refused job has created no ticket, lane entry or continuation,
/// so it cannot detach later jobs from the earlier jobs that were accepted for the key.</item>
/// <item>Each job carries the <see cref="WorkContext"/> captured when it was accepted; the worker exposes it
/// as <see cref="WorkContext.Current"/> while the job runs, so results can only be applied by the lifetime
/// (and for the server) the work was accepted for.</item>
/// </list>
/// </summary>
internal sealed class BoundedWorkQueue
{
    private sealed class Job(string operation, Func<CancellationToken, Task> work, Action discard, WorkContext? context,
        CancellationToken callerToken, long enqueued, ulong? key)
    {
        public readonly string Operation = operation;
        public readonly Func<CancellationToken, Task> Work = work;

        /// <summary>Completes the job's result as cancelled; idempotent (the TCS ignores a second completion).</summary>
        public readonly Action Discard = discard;

        public readonly WorkContext? Context = context;
        public readonly CancellationToken CallerToken = callerToken;
        public readonly long Enqueued = enqueued;

        /// <summary>Order key, or null. A keyed job in the channel or running is the head of its key's lane.</summary>
        public readonly ulong? Key = key;
    }

    private sealed class Lane
    {
        /// <summary>Jobs of the key behind the head, in acceptance order.</summary>
        public readonly Queue<Job> Waiting = new();
    }

    private readonly Channel<Job> _channel;
    private readonly CancellationToken _lifetime;
    private readonly string _name;
    private readonly Func<WorkContext?>? _captureContext;
    private readonly LatencyHistogram? _waitHistogram;
    private int _pending;
    private int _running;
    private int _queued;  // accepted and not yet taken by a worker: channel + lanes; reserved before publishing, ≤ Capacity
    private int _waiting; // subset of _queued parked in lanes behind their key's head job

    // Guards _lanes, the lanes' queues and _stopped. Held only for dictionary/queue operations, never while a job or a
    // continuation runs, so the game thread's admission and a worker's completion never wait for anything long.
    private readonly object _lanesLock = new();
    private readonly Dictionary<ulong, Lane> _lanes = new();
    private bool _stopped;

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

    /// <summary>Accepted and not finished: waiting in a lane, ready in the channel, or running.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Jobs that currently occupy a worker slot. A job waiting for its key's earlier job is <b>not</b> counted.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Accepted jobs no worker has taken yet (ready + waiting for their key). Never above <see cref="Capacity"/>.</summary>
    public int Queued => Volatile.Read(ref _queued);

    /// <summary>Jobs parked behind an earlier job of their key; they hold no worker.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Keys that currently have a head job in the channel or running (diagnostics, tests).</summary>
    public int ActiveKeys
    {
        get
        {
            lock (_lanesLock) return _lanes.Count;
        }
    }

    public long Completed;
    public long Failed;
    public long Rejected;
    public long Discarded;

    /// <summary>Queues a job. Returns false (without running anything) when the queue is full or stopped.</summary>
    public bool TryEnqueue(string operation, Func<CancellationToken, Task> work, WorkContext? context = null) =>
        TryEnqueueCore(operation, work, static () => { }, default, context, null);

    /// <summary>
    /// Queues a job that starts after every earlier job with the same <paramref name="orderKey"/> has ended and before
    /// every later one. Never waits and never occupies a worker while it waits. A refusal changes nothing for the key.
    /// </summary>
    public bool TryEnqueue(string operation, Func<CancellationToken, Task> work, WorkContext? context, ulong orderKey) =>
        TryEnqueueCore(operation, work, static () => { }, default, context, orderKey);

    private bool TryReserve()
    {
        while (true)
        {
            var queued = Volatile.Read(ref _queued);
            if (queued >= Capacity) return false;
            if (Interlocked.CompareExchange(ref _queued, queued + 1, queued) == queued) return true;
        }
    }

    private bool TryEnqueueCore(string operation, Func<CancellationToken, Task> work, Action discard, CancellationToken callerToken,
        WorkContext? explicitContext, ulong? key)
    {
        if (_lifetime.IsCancellationRequested || callerToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref Rejected);
            return false;
        }

        // Context is captured here, on the caller's thread, at the moment the work is accepted
        var context = explicitContext ?? _captureContext?.Invoke();
        if (context != null && !context.Runtime.IsCurrent)
        {
            // Producer of a lifetime that is over (nested job of an old worker): never accepted into the current queue
            Interlocked.Increment(ref Rejected);
            return false;
        }

        // 1. Reserve capacity. A job refused here has created nothing: no lane entry, ticket or continuation.
        if (!TryReserve())
        {
            Interlocked.Increment(ref Rejected);
            Interlocked.Increment(ref PluginMetrics.DbJobsRejected);
            RateLimitedLog.Warning($"{_name}.full", $"{_name} queue is full ({Capacity}); rejected '{operation}'");
            return false;
        }

        var job = new Job(operation, work, discard, context, callerToken, Stopwatch.GetTimestamp(), key);
        Interlocked.Increment(ref _pending);

        // 2. Publish: to the channel when it can start (no key, or its key is idle), otherwise behind its key's head job.
        //    The channel cannot be full: every job in it holds one of the Capacity reservations.
        var published = false;
        lock (_lanesLock)
        {
            if (!_stopped)
            {
                if (key is { } k && _lanes.TryGetValue(k, out var lane))
                {
                    lane.Waiting.Enqueue(job);
                    Interlocked.Increment(ref _waiting);
                    published = true;
                }
                else if (_channel.Writer.TryWrite(job))
                {
                    if (key is { } idle) _lanes[idle] = new Lane();
                    published = true;
                }
            }
        }

        if (!published)
        {
            // Stopped between the check above and here: give the reservation back, nothing else was created
            Interlocked.Decrement(ref _pending);
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref Rejected);
            return false;
        }

        // The lifetime may have ended between the check above and the write, after the workers already left:
        // nobody would ever read this job. Drain from here so its completion is not lost.
        if (_lifetime.IsCancellationRequested) Shutdown();
        return true;
    }

    /// <summary>
    /// The head job of <paramref name="key"/> ended (finished, failed, cancelled) or was discarded before it started:
    /// hand the lane to its next job, which joins the channel behind every job that is ready already.
    /// </summary>
    private void KeyFinished(ulong key)
    {
        List<Job>? lost = null;
        lock (_lanesLock)
        {
            if (!_lanes.TryGetValue(key, out var lane)) return; // lane dropped by Shutdown: nothing to hand over
            if (lane.Waiting.Count == 0)
            {
                _lanes.Remove(key);
                return;
            }

            var next = lane.Waiting.Dequeue();
            Interlocked.Decrement(ref _waiting);
            if (_stopped || !_channel.Writer.TryWrite(next))
            {
                // Cannot be started any more (the queue is closing): neither can the rest of this lane
                _lanes.Remove(key);
                (lost ??= []).Add(next);
                while (lane.Waiting.TryDequeue(out var rest))
                {
                    Interlocked.Decrement(ref _waiting);
                    lost.Add(rest);
                }
            }
        }

        if (lost == null)
        {
            if (_lifetime.IsCancellationRequested) Shutdown(); // promoted after the workers left
            return;
        }

        foreach (var job in lost)
        {
            Interlocked.Decrement(ref _queued);
            DiscardJob(job);
        }
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
    /// <param name="orderKey">Optional order key, see <see cref="TryEnqueue(string, Func{CancellationToken, Task}, WorkContext?, ulong)"/>.</param>
    public Task<T>? TryEnqueue<T>(string operation, Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default, WorkContext? context = null, ulong? orderKey = null)
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
        }, () => tcs.TrySetCanceled(), cancellationToken, context, orderKey);

        if (!accepted) return null;
        // The awaiter is released by whichever happens first: the job's own outcome, the caller's token (even while the
        // job is still queued; the worker then skips it) or the plugin lifetime. The last one matters for work that is
        // already running and ignores its token (legacy SQL): unload must not leave its consumers hanging. The running
        // job itself is not interrupted and keeps its worker/pending slot until it really returns, so the concurrency
        // limit stays honest; its late result lands on an already completed task and is ignored.
        var lifetimeRegistration = _lifetime.CanBeCanceled ? _lifetime.Register(static s => ((TaskCompletionSource<T>)s!).TrySetCanceled(), tcs) : default;
        var callerRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static s => ((TaskCompletionSource<T>)s!).TrySetCanceled(), tcs)
            : default;
        if (lifetimeRegistration != default || callerRegistration != default)
            tcs.Task.ContinueWith(static (_, state) =>
                {
                    var (a, b) = ((CancellationTokenRegistration, CancellationTokenRegistration))state!;
                    a.Dispose();
                    b.Dispose();
                }, (lifetimeRegistration, callerRegistration), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return tcs.Task;
    }

    /// <summary>Stops accepting work and completes whatever is still queued as cancelled. Never blocks.</summary>
    public void Complete() => Shutdown();

    /// <summary>Idempotent: refuse new work, drop every lane, complete everything that has not started as cancelled.</summary>
    private void Shutdown()
    {
        List<Job>? parked = null;
        lock (_lanesLock)
        {
            _stopped = true;
            foreach (var lane in _lanes.Values)
            {
                while (lane.Waiting.TryDequeue(out var job))
                {
                    (parked ??= []).Add(job);
                    Interlocked.Decrement(ref _waiting);
                }
            }

            _lanes.Clear(); // a running head finishes into a missing lane
        }

        _channel.Writer.TryComplete();
        DiscardQueued();
        if (parked == null) return;
        foreach (var job in parked)
        {
            Interlocked.Decrement(ref _queued);
            DiscardJob(job);
        }
    }

    private void DiscardQueued()
    {
        while (_channel.Reader.TryRead(out var job))
        {
            Interlocked.Decrement(ref _queued);
            DiscardJob(job);
            if (job.Key is { } key) KeyFinished(key);
        }
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
                    Interlocked.Decrement(ref _queued);
                    _waitHistogram?.RecordSince(job.Enqueued);
                    if (_lifetime.IsCancellationRequested || job.CallerToken.IsCancellationRequested)
                    {
                        // Cancelled before it started: complete it (exactly once) instead of silently dropping it
                        DiscardJob(job);
                        if (job.Key is { } discardedKey) KeyFinished(discardedKey);
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
                        // Whatever the outcome, the key's next job may start now (a failed predecessor does not block it)
                        if (job.Key is { } key) KeyFinished(key);
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
            Shutdown();
        }
    }

    public string Describe() =>
        $"{_name}: pending={Pending} queued={Queued}/{Capacity} (waiting-for-key={Waiting}, keys={ActiveKeys}) running={Running}/{Workers} completed={Interlocked.Read(ref Completed)} failed={Interlocked.Read(ref Failed)} rejected={Interlocked.Read(ref Rejected)} discarded={Interlocked.Read(ref Discarded)}";
}
