using System.Collections.Concurrent;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Runs the database jobs of <b>one player</b> (one SteamID64 key) in the order in which the game thread accepted them,
/// without serialising anything else.
/// <para>
/// Why it exists: the database queue may have several workers (MySQL: 4), so two jobs accepted one after the other
/// could otherwise run in either order. For penalties that is visible: an <c>unmute</c> accepted before a new
/// <c>mute</c> must not close the new row, and a <c>mute</c> accepted before an <c>unmute</c> must be closed by it.
/// </para>
/// <para>
/// How: <see cref="Acquire"/> (game thread, lock-free apart from the dictionary's own bucket synchronisation, never
/// waits) links the new ticket behind the previous ticket of the same key. The job, already running on a queue worker,
/// <b>awaits</b> the previous ticket asynchronously (no thread is blocked, the wait honours the lifetime token) and
/// releases its own ticket in a <c>finally</c>, whatever the job's outcome: a failed or cancelled predecessor lets its
/// successors run (the successor's own SQL then simply sees the committed state).
/// </para>
/// <para>
/// Bounds and guarantees. A ticket exists only while its job is queued or running, so the number of entries is
/// bounded by the queue capacity. Because jobs leave the FIFO queue in acceptance order, a job only ever waits for
/// jobs that are already running or finished, never for one behind it: no deadlock, with any number of workers
/// (including one: the predecessor has completed before the successor is even dequeued). The cost is that a worker
/// may spend time waiting for the earlier job of the <i>same</i> player; jobs of other players are not affected.
/// One instance belongs to one <see cref="Runtime"/> start/stop cycle; a restart creates a new one, so a stuck ticket
/// of a dead lifetime can never block the next.
/// </para>
/// </summary>
internal sealed class KeyedSequencer
{
    internal sealed class Link(ulong key)
    {
        public readonly ulong Key = key;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A place in one key's order. Release exactly once per acquisition (extra calls are ignored).</summary>
    internal sealed class Ticket
    {
        private readonly KeyedSequencer _owner;
        private readonly Task? _previous;
        private readonly Link _link;
        private int _released;

        internal Ticket(KeyedSequencer owner, Task? previous, Link link)
        {
            _owner = owner;
            _previous = previous;
            _link = link;
        }

        /// <summary>Completes when every earlier ticket of the key was released. Cancels with <paramref name="ct"/>.</summary>
        public Task WaitTurnAsync(CancellationToken ct) =>
            _previous == null || _previous.IsCompleted ? Task.CompletedTask : _previous.WaitAsync(ct);

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _link.Done.TrySetResult();
            // Only the tail is removed; an older ticket never touches a newer successor's entry
            _owner._tails.TryRemove(new KeyValuePair<ulong, Link>(_link.Key, _link));
        }
    }

    private readonly ConcurrentDictionary<ulong, Link> _tails = new();

    /// <summary>Number of keys that currently have a pending or running job (diagnostics, tests).</summary>
    public int ActiveKeys => _tails.Count;

    /// <summary>Takes the next place for <paramref name="key"/>. Never blocks and never waits for a job.</summary>
    public Ticket Acquire(ulong key)
    {
        var mine = new Link(key);
        while (true)
        {
            if (_tails.TryGetValue(key, out var previous))
            {
                if (_tails.TryUpdate(key, mine, previous)) return new Ticket(this, previous.Done.Task, mine);
            }
            else if (_tails.TryAdd(key, mine))
            {
                return new Ticket(this, null, mine);
            }
        }
    }
}
