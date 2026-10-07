using System.Collections.Concurrent;
using System.Diagnostics;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Bounded queue of work that must run on the game thread (applying DB results, kicks, prints, menus).
/// <para>
/// Only one pump is scheduled into CounterStrikeSharp's world-update queue at a time, and a pump runs at most
/// <see cref="MaxItemsPerUpdate"/> items or <see cref="BudgetTicks"/> of wall time, whichever comes first;
/// leftovers continue on the next world update. A budget cannot interrupt a running item, native call or GC,
/// so every posted item must itself be small (callers split large outputs into chunks). The budget is therefore
/// <b>not</b> an upper bound of a pump's wall time: items longer than the budget are counted in
/// <c>DispatcherOverBudgetItems</c> and visible in the <c>game.dispatcher_item</c> max.
/// </para>
/// <para>
/// Producers: <see cref="TryPost"/> never blocks (used on the game thread; returns false when full);
/// <see cref="PostAsync"/> waits asynchronously for space (used by background work, giving back-pressure
/// to the bounded background queues instead of growing this queue).
/// </para>
/// </summary>
internal sealed class GameDispatcher
{
    private readonly struct WorkItem(Action action, TaskCompletionSource? completion, long enqueued)
    {
        public readonly Action Action = action;
        public readonly TaskCompletionSource? Completion = completion;
        public readonly long Enqueued = enqueued;
    }

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly SemaphoreSlim _space;
    private readonly Action<Action> _scheduleNextUpdate;
    private readonly Action _pump;
    private int _pumpScheduled;
    private volatile bool _stopped;

    public GameDispatcher(Action<Action> scheduleNextUpdate, int capacity = 4096, int maxItemsPerUpdate = 64,
        double budgetMilliseconds = 0.5)
    {
        _scheduleNextUpdate = scheduleNextUpdate;
        Capacity = capacity;
        MaxItemsPerUpdate = maxItemsPerUpdate;
        BudgetTicks = (long)(budgetMilliseconds * Stopwatch.Frequency / 1000.0);
        _space = new SemaphoreSlim(capacity, capacity);
        _pump = Pump;
    }

    public int Capacity { get; }
    public int MaxItemsPerUpdate { get; }
    public long BudgetTicks { get; }
    public int Count => _queue.Count;
    public bool IsStopped => _stopped;

    /// <summary>Non-blocking post. Returns false if the dispatcher is stopped or full.</summary>
    public bool TryPost(Action action)
    {
        if (_stopped || !_space.Wait(0))
        {
            Interlocked.Increment(ref PluginMetrics.DispatcherRejected);
            return false;
        }

        _queue.Enqueue(new WorkItem(action, null, Stopwatch.GetTimestamp()));
        EnsurePump();
        return true;
    }

    /// <summary>
    /// Posts work and completes when it has run on the game thread. Waits (asynchronously) while the queue is full.
    /// The returned task is cancelled if the dispatcher stops before the item runs.
    /// </summary>
    public async Task PostAsync(Action action, CancellationToken cancellationToken = default)
    {
        if (_stopped) throw new OperationCanceledException("Game dispatcher stopped");
        await _space.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_stopped)
        {
            _space.Release();
            throw new OperationCanceledException("Game dispatcher stopped");
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new WorkItem(action, tcs, Stopwatch.GetTimestamp()));
        EnsurePump();
        await tcs.Task.ConfigureAwait(false);
    }

    public async Task<T> PostAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        T result = default!;
        await PostAsync(() => { result = func(); }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Stops the dispatcher: pending items are dropped and their awaiters cancelled. Never blocks.</summary>
    public void Stop()
    {
        _stopped = true;
        while (_queue.TryDequeue(out var item))
        {
            item.Completion?.TrySetCanceled();
            Interlocked.Increment(ref PluginMetrics.DispatcherDroppedStale);
        }
    }

    private void EnsurePump()
    {
        if (Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) == 0)
            _scheduleNextUpdate(_pump);
    }

    /// <summary>Runs on the game thread from the world-update queue.</summary>
    internal void Pump()
    {
        if (_stopped)
        {
            Volatile.Write(ref _pumpScheduled, 0);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var processed = 0;
        while (processed < MaxItemsPerUpdate && _queue.TryDequeue(out var item))
        {
            _space.Release();
            processed++;
            var itemStart = Stopwatch.GetTimestamp();
            PluginMetrics.ApplyLatency.Record(itemStart - item.Enqueued);
            try
            {
                item.Action();
                item.Completion?.TrySetResult();
            }
            catch (OperationCanceledException) when (item.Completion != null)
            {
                // The producer's runtime context ended before the item ran: not an error, just a cancelled result
                item.Completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                if (item.Completion != null)
                    item.Completion.TrySetException(ex);
                else
                    RateLimitedLog.Error("dispatcher.item", ex, "Game-thread work item failed");
            }

            var now = Stopwatch.GetTimestamp();
            var itemTicks = now - itemStart;
            PluginMetrics.DispatcherItem.Record(itemTicks);
            // The budget below is checked only BETWEEN items; an item that alone exceeds it is the real stall
            if (itemTicks > BudgetTicks) Interlocked.Increment(ref PluginMetrics.DispatcherOverBudgetItems);
            if (now - start >= BudgetTicks) break;
        }

        PluginMetrics.DispatcherPump.RecordSince(start);
        Volatile.Write(ref _pumpScheduled, 0);

        // Work left (budget hit) or posted while we were finishing: continue on the next world update.
        if (!_queue.IsEmpty && !_stopped && Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) == 0)
        {
            Interlocked.Increment(ref PluginMetrics.DispatcherDeferredUpdates);
            _scheduleNextUpdate(_pump);
        }
    }
}
