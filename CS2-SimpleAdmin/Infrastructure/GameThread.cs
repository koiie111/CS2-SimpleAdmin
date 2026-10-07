using System.Diagnostics;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Identity of the server (game) thread. CounterStrikeSharp calls Load, timers, listeners, events and
/// NextWorldUpdate callbacks on it; native players/pawns/ConVars/AdminManager may only be touched there.
/// </summary>
internal static class GameThread
{
    private static int _threadId = -1;

    /// <summary>Records the calling thread as the game thread. Called from Load.</summary>
    public static void Capture() => _threadId = Environment.CurrentManagedThreadId;

    /// <summary>For tests: forget the captured thread.</summary>
    internal static void Reset() => _threadId = -1;

    /// <summary>True when running on the captured game thread (or when nothing was captured yet).</summary>
    public static bool IsCurrent => _threadId == -1 || _threadId == Environment.CurrentManagedThreadId;

    public static int ThreadId => _threadId;

    [Conditional("DEBUG")]
    public static void AssertCurrent(string what)
    {
        if (!IsCurrent)
            Debug.Fail($"{what} must run on the game thread");
    }
}
