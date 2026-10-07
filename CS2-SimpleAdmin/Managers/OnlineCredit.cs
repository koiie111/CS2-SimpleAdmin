using System.Diagnostics;

namespace CS2_SimpleAdmin.Managers;

/// <summary>One mute's planned online-time change: <c>passed</c> goes from <see cref="Expected"/> to <see cref="Target"/>.</summary>
public readonly record struct OnlineCreditStep(int MuteId, int Expected, int Target);

/// <summary>
/// One idempotent credit of online minutes (TimeMode 0) for one session.
/// <para>
/// Why a protocol and not a transaction: a failure can happen after the database committed but before the plugin
/// learned about it (lost COMMIT acknowledgement, cancellation after commit, a game-thread apply that never ran).
/// "Add N minutes" is not safe to retry then. This credit therefore fixes, once, the exact pre- and post-image of every
/// mute it touches (<see cref="Plan"/>) and applies each as a compare-and-set
/// <c>UPDATE … SET passed = Target WHERE id = … AND passed = Expected</c>. Re-running any step any number of times can
/// change the row at most once (after the first success <c>passed</c> is no longer <c>Expected</c>), so retries and
/// ambiguous commits cannot double-credit. A mute whose <c>passed</c> was changed by someone else in between is left
/// alone (under-crediting by at most this window is the safe direction: a sanction is never shortened by a retry).
/// </para>
/// <para>
/// Lifecycle: <b>planned</b> (<see cref="Plan"/> set from a read) → <b>applied</b> (<see cref="Applied"/>: every step
/// committed or proven unnecessary) → <b>folded</b> (the game thread added <see cref="Ticks"/> to the session's
/// <c>CreditedTicks</c> and dropped the credit). The same object is retried until folded, and the session computes no
/// new window while one is outstanding, so windows never overlap or repeat.
/// </para>
/// <para>
/// A mute only receives the part of the window that lies after its own <c>created</c> time, so a mute issued
/// between maintenance passes does not inherit minutes played before it existed.
/// </para>
/// Fields written by the database worker are volatile; the game thread reads them between passes (passes are
/// single-flight, which gives the needed ordering).
/// </summary>
internal sealed class OnlineCredit(ulong steamId, int minutes, long ticks, DateTime windowStart, DateTime windowEnd)
{
    public ulong SteamId { get; } = steamId;

    /// <summary>Whole minutes of online time in this window.</summary>
    public int Minutes { get; } = minutes;

    /// <summary>Stopwatch ticks of the window (<see cref="Minutes"/> × ticks per minute).</summary>
    public long Ticks { get; } = ticks;

    /// <summary>Wall-clock bounds of the window, in the clock that <c>sa_mutes.created</c> is written with.</summary>
    public DateTime WindowStart { get; } = windowStart;

    public DateTime WindowEnd { get; } = windowEnd;

    private volatile IReadOnlyList<OnlineCreditStep>? _plan;
    private volatile bool _applied;

    /// <summary>Null until planned; afterwards never changes.</summary>
    public IReadOnlyList<OnlineCreditStep>? Plan
    {
        get => _plan;
        internal set => _plan = value;
    }

    public bool Applied
    {
        get => _applied;
        internal set => _applied = value;
    }

    /// <summary>Minutes of the window that a mute created at <paramref name="created"/> is entitled to.</summary>
    internal int EligibleMinutes(DateTime? created)
    {
        if (created is not { } c || c <= WindowStart) return Minutes;
        if (c >= WindowEnd) return 0;
        return Math.Min(Minutes, (int)Math.Floor((WindowEnd - c).TotalMinutes));
    }

    public static readonly long TicksPerMinute = Stopwatch.Frequency * 60;
}
