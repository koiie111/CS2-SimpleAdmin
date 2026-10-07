using System.Diagnostics;

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

    /// <summary>Set once the connect load was queued; used to deduplicate connect / connect_full.</summary>
    public bool LoadQueued { get; set; }

    /// <summary>TimeMode 0 accounting: Stopwatch ticks of online time already converted to whole minutes.</summary>
    public long CreditedTicks { get; set; }

    public override string ToString() => $"session#{Id} slot={Slot} steam={SteamId} userid={UserId}";
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
