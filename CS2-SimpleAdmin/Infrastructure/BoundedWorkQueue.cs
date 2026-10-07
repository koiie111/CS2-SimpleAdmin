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
/// </list>
/// </summary>
internal sealed class BoundedWorkQueue
{
    private readonly record struct Job(string Operation, Func<CancellationToken, Task> Work, long Enqueued);

    private readonly Channel<Job> _channel;
    private readonly CancellationToken _lifetime;
    private readonly string _name;
    private readonly LatencyHistogram? _waitHistogram;
    private int _pending;
    private int _running;

    public BoundedWorkQueue(string name, int capacity, int workers, CancellationToken lifetime,
        LatencyHistogram? waitHistogram = null)
    {
        _name = name;
        Capacity = capacity;
        Workers = workers;
        _lifetime = lifetime;
        _waitHistogram = waitHistogram;
        _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full
            SingleReader = workers == 1,
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

    /// <summary>Queues a job. Returns false (without running anything) when the queue is full or stopped.</summary>
    public bool TryEnqueue(string operation, Func<CancellationToken, Task> work)
    {
        if (_lifetime.IsCancellationRequested)
        {
            Interlocked.Increment(ref Rejected);
            return false;
        }

        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(new Job(operation, work, Stopwatch.GetTimestamp())))
            return true;

        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref Rejected);
        Interlocked.Increment(ref PluginMetrics.DbJobsRejected);
        RateLimitedLog.Warning($"{_name}.full", $"{_name} queue is full ({Capacity}); rejected '{operation}'");
        return false;
    }

    /// <summary>Queues a job with a result. Returns null when rejected.</summary>
    public Task<T>? TryEnqueue<T>(string operation, Func<CancellationToken, Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = TryEnqueue(operation, async ct =>
        {
            try
            {
                tcs.TrySetResult(await work(ct).ConfigureAwait(false));
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
        });
        return accepted ? tcs.Task : null;
    }

    /// <summary>Stops accepting work; workers exit after the lifetime token is cancelled. Never blocks.</summary>
    public void Complete() => _channel.Writer.TryComplete();

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
                    if (_lifetime.IsCancellationRequested)
                    {
                        Interlocked.Decrement(ref _pending);
                        return;
                    }

                    Interlocked.Increment(ref _running);
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
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref Failed);
                        Interlocked.Increment(ref PluginMetrics.DbJobsFailed);
                        RateLimitedLog.Error($"{_name}.{job.Operation}", ex, $"Background job '{job.Operation}' failed");
                    }
                    finally
                    {
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
            // Drop whatever is still queued so Pending reflects reality after unload.
            while (reader.TryRead(out _)) Interlocked.Decrement(ref _pending);
        }
    }

    public string Describe() =>
        $"{_name}: pending={Pending}/{Capacity} running={Running}/{Workers} completed={Interlocked.Read(ref Completed)} failed={Interlocked.Read(ref Failed)} rejected={Interlocked.Read(ref Rejected)}";
}
