using System.Diagnostics;
using CS2_SimpleAdmin.Managers;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// One connection of one player: identity snapshot taken on the game thread.
/// Background work carries this object instead of a controller/PlayerInfo and the game thread checks
/// <see cref="PlayerSessions.IsCurrent"/> before applying anything, so a result never lands on a player that
/// disconnected, on another account that reused the slot, or on a new connection of the same account.
/// </summary>
internal sealed class PlayerSession(long id, int slot, ulong steamId, int userId, string name, string? ipAddress)
{
    public long Id { get; } = id;
    public int Slot { get; } = slot;
    public ulong SteamId { get; } = steamId;
    public int UserId { get; } = userId;
    public string Name { get; } = name;
    public string? IpAddress { get; } = ipAddress;
    public long StartedTimestamp { get; } = Stopwatch.GetTimestamp();

    // ---- connect load state machine (game thread only) ----
    // Pending ──TryBeginLoad──▶ InFlight ──CompleteLoad──▶ Loaded
    //    ▲                          │
    //    └─ RetryWait ◀─FailLoad────┘   (queue full: ReleaseLoad(queueFull) → RetryWait without counting an attempt)
    // Every transition carries the attempt number it was started with, so a late result of an older attempt
    // (or of a session that was replaced) cannot move the state.

    public ConnectLoadState LoadState { get; private set; } = ConnectLoadState.Pending;

    /// <summary>Number of load attempts started for this connection.</summary>
    public int LoadAttempts { get; private set; }

    /// <summary>Stopwatch timestamp from which the next attempt may start (while <see cref="ConnectLoadState.RetryWait"/>).</summary>
    public long NextLoadAttemptTimestamp { get; private set; }

    /// <summary>True once a load was started or finished: used to deduplicate connect / connect_full.</summary>
    public bool LoadStartedOrDone => LoadState is ConnectLoadState.InFlight or ConnectLoadState.Loaded;

    /// <summary>Starts an attempt if the session is waiting for one that is due. Returns its number, or 0.</summary>
    public int TryBeginLoad(long nowTimestamp)
    {
        if (LoadState == ConnectLoadState.Pending ||
            LoadState == ConnectLoadState.RetryWait && nowTimestamp >= NextLoadAttemptTimestamp)
        {
            LoadState = ConnectLoadState.InFlight;
            return ++LoadAttempts;
        }

        return 0;
    }

    /// <summary>The attempt's result was applied (or the session was kicked). Ignored for a stale attempt.</summary>
    public bool CompleteLoad(int attempt)
    {
        if (LoadState != ConnectLoadState.InFlight || attempt != LoadAttempts) return false;
        LoadState = ConnectLoadState.Loaded;
        return true;
    }

    /// <summary>The attempt failed after the database accepted the work: schedule a retry with backoff.</summary>
    public bool FailLoad(int attempt, long nowTimestamp, out TimeSpan delay)
    {
        delay = default;
        if (LoadState != ConnectLoadState.InFlight || attempt != LoadAttempts) return false;
        delay = LoadRetryPolicy.Delay(attempt, SteamId);
        NextLoadAttemptTimestamp = nowTimestamp + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        LoadState = ConnectLoadState.RetryWait;
        return true;
    }

    /// <summary>
    /// The queue refused the attempt (full/stopped) so nothing was tried: wait a short time without consuming the
    /// attempt counter (a full queue says nothing about this player's data).
    /// </summary>
    public bool ReleaseLoad(int attempt, long nowTimestamp, TimeSpan delay)
    {
        if (LoadState != ConnectLoadState.InFlight || attempt != LoadAttempts) return false;
        LoadAttempts--;
        NextLoadAttemptTimestamp = nowTimestamp + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        LoadState = ConnectLoadState.RetryWait;
        return true;
    }

    /// <summary>Pending credit of online minutes (TimeMode 0); see <see cref="OnlineCredit"/>. Written by the DB worker, read by the game thread between passes.</summary>
    public OnlineCredit? PendingCredit { get; set; }

    /// <summary>TimeMode 0 accounting: Stopwatch ticks of online time already converted to whole minutes.</summary>
    public long CreditedTicks { get; set; }

    public override string ToString() => $"session#{Id} slot={Slot} steam={SteamId} userid={UserId}";
}

internal enum ConnectLoadState
{
    /// <summary>No load started yet (connected before Ready, or not picked up).</summary>
    Pending,

    /// <summary>A load is queued or running on the database queue.</summary>
    InFlight,

    /// <summary>The load result was applied to the game state.</summary>
    Loaded,

    /// <summary>The last attempt failed (or could not be queued); the next one starts when due.</summary>
    RetryWait
}

/// <summary>Backoff of failed connect loads: 2 s, 5 s, 15 s, 30 s, then every 60 s, with up to 25% deterministic jitter per player.</summary>
internal static class LoadRetryPolicy
{
    private static readonly int[] Seconds = [2, 5, 15, 30, 60];
    public static readonly TimeSpan QueueFullDelay = TimeSpan.FromSeconds(2);

    public static TimeSpan Delay(int attempt, ulong steamId)
    {
        var baseSeconds = Seconds[Math.Min(Math.Max(attempt, 1), Seconds.Length) - 1];
        var jitter = 1.0 + (steamId % 25) / 100.0; // spreads a whole server's retries after a DB outage
        return TimeSpan.FromSeconds(baseSeconds * jitter);
    }
}

/// <summary>
/// Slot → current session table. Mutated only on the game thread; reads from any thread see either the
/// previous or the next session reference (array element writes are atomic), never a torn state.
/// </summary>
internal sealed class PlayerSessions
{
    public const int MaxSlots = 128;
    private static long _nextId;
    private readonly PlayerSession?[] _bySlot = new PlayerSession?[MaxSlots];

    /// <summary>
    /// Returns the session for this connection, creating it if the slot is empty or holds another connection.
    /// The same (slot, steamid, userid) seen from OnClientConnected and player_connect_full yields one session.
    /// </summary>
    public PlayerSession BeginOrGet(int slot, ulong steamId, int userId, string name, string? ipAddress, out bool created)
    {
        GameThread.AssertCurrent(nameof(BeginOrGet));
        if ((uint)slot >= MaxSlots) throw new ArgumentOutOfRangeException(nameof(slot));

        var existing = Volatile.Read(ref _bySlot[slot]);
        if (existing != null && existing.SteamId == steamId && existing.UserId == userId)
        {
            created = false;
            return existing;
        }

        var session = new PlayerSession(Interlocked.Increment(ref _nextId), slot, steamId, userId, name, ipAddress);
        Volatile.Write(ref _bySlot[slot], session);
        created = true;
        return session;
    }

    public PlayerSession? Get(int slot) => (uint)slot < MaxSlots ? Volatile.Read(ref _bySlot[slot]) : null;

    public bool IsCurrent(PlayerSession? session) =>
        session != null && (uint)session.Slot < MaxSlots && ReferenceEquals(Volatile.Read(ref _bySlot[session.Slot]), session);

    /// <summary>Ends the session in the slot (disconnect). Pending results for it become stale.</summary>
    public void End(int slot)
    {
        GameThread.AssertCurrent(nameof(End));
        if ((uint)slot < MaxSlots) Volatile.Write(ref _bySlot[slot], null);
    }

    /// <summary>Ends every session (map change, unload).</summary>
    public void Clear()
    {
        for (var i = 0; i < MaxSlots; i++) Volatile.Write(ref _bySlot[i], null);
    }

    /// <summary>Copies the current sessions into <paramref name="target"/> (cleared first).</summary>
    public void Snapshot(List<PlayerSession> target)
    {
        target.Clear();
        for (var i = 0; i < MaxSlots; i++)
        {
            var s = Volatile.Read(ref _bySlot[i]);
            if (s != null) target.Add(s);
        }
    }

    public PlayerSession? FindBySteamId(ulong steamId)
    {
        for (var i = 0; i < MaxSlots; i++)
        {
            var s = Volatile.Read(ref _bySlot[i]);
            if (s != null && s.SteamId == steamId) return s;
        }

        return null;
    }
}
