using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdminApi;

namespace CS2_SimpleAdmin.Managers;

/// <summary>
/// In-memory gag/mute/silence state of connected players, keyed by slot.
/// <para>
/// Each slot holds an immutable <see cref="SlotPenalties"/> object that is replaced atomically
/// (compare-and-swap) on every change. Readers (chat hook, command listener, API) take one reference and never
/// see a half-applied change, never mutate anything and never allocate. Writers normally run on the game thread
/// (commands, results applied through the dispatcher); the CAS keeps a stray call from another thread correct
/// without any lock that could stall the game thread.
/// </para>
/// <para>
/// Expiry is evaluated when reading (an expired entry is simply not active) and physically removed only by
/// <see cref="RemoveExpiredPenalties()"/> from the periodic timer.
/// </para>
/// </summary>
public static class PlayerPenaltyManager
{
    /// <param name="Revision">
    /// Position of the operation that created the entry in the order in which the game thread accepted operations
    /// (<see cref="NextRevision"/>). A deferred removal captures its own revision when it is accepted and later removes
    /// only entries up to it, so a penalty accepted after the removal can never be erased by it.
    /// </param>
    /// <param name="DbId">
    /// sa_mutes.id of the row this entry mirrors: &gt; 0 read from the database, 0 added by this server's own command
    /// (its row id is not known to the game thread; the first sync after the command's SQL replaces it by the row),
    /// <see cref="ExternalDbId"/> added through the public API by another plugin (never touched by a database sync).
    /// </param>
    internal readonly record struct Entry(DateTime EndDateTime, int Duration, bool Passed, long Revision = 0, long DbId = 0);

    /// <summary><see cref="Entry.DbId"/> of an entry that only exists in memory because another plugin added it.</summary>
    internal const long ExternalDbId = -1;

    /// <summary>One active database row as the sync sees it.</summary>
    internal readonly record struct DbPenalty(long Id, PenaltyType Type, DateTime EndDateTime, int Duration);

    /// <summary>Immutable penalties of one slot. Index = (int)PenaltyType.</summary>
    internal sealed class SlotPenalties
    {
        public static readonly SlotPenalties Empty = new(new Entry[TypeCount][]);
        public readonly Entry[]?[] ByType;

        public SlotPenalties(Entry[]?[] byType) => ByType = byType;

        public bool IsEmpty
        {
            get
            {
                foreach (var list in ByType)
                    if (list is { Length: > 0 }) return false;
                return true;
            }
        }

        public Entry[]? Get(PenaltyType type) => (uint)type < TypeCount ? ByType[(int)type] : null;

        public SlotPenalties With(PenaltyType type, Entry[]? entries)
        {
            var copy = (Entry[]?[])ByType.Clone();
            copy[(int)type] = entries is { Length: > 0 } ? entries : null;
            return new SlotPenalties(copy);
        }
    }

    private const int TypeCount = (int)PenaltyType.Warn + 1;
    private static readonly SlotPenalties?[] Slots = new SlotPenalties?[PlayerSessions.MaxSlots];

    private static long _revision;

    /// <summary>
    /// Next position in the accept order of penalty operations (commands, connect loads). Unique and increasing; taken
    /// on the game thread when an operation is accepted, so it orders operations exactly like the per-player SQL order
    /// (see <see cref="Infrastructure.BoundedWorkQueue"/>).
    /// </summary>
    internal static long NextRevision() => Interlocked.Increment(ref _revision);

    private static int CurrentTimeMode => CS2_SimpleAdmin.CurrentConfig.OtherSettings.TimeMode;

    private static void Update(int slot, Func<SlotPenalties, SlotPenalties?> change)
    {
        if ((uint)slot >= Slots.Length) return;
        while (true)
        {
            var current = Volatile.Read(ref Slots[slot]);
            var next = change(current ?? SlotPenalties.Empty);
            if (next != null && next.IsEmpty) next = null;
            if (ReferenceEquals(next, current)) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref Slots[slot], next, current), current)) return;
        }
    }

    /// <summary>
    /// Adds a penalty for a specific player slot and penalty type.
    /// </summary>
    /// <param name="slot">The player slot where the penalty should be applied.</param>
    /// <param name="penaltyType">The type of penalty to apply (e.g. gag, mute, silence).</param>
    /// <param name="endDateTime">The validity expiration date/time of the penalty.</param>
    /// <param name="durationInMinutes">The duration of the penalty in minutes (0 for permanent).</param>
    public static void AddPenalty(int slot, PenaltyType penaltyType, DateTime endDateTime, int durationInMinutes) =>
        AddPenalty(slot, penaltyType, endDateTime, durationInMinutes, 0, ExternalDbId);

    /// <param name="revision">The accept-order position of the operation (0 = take a new one now).</param>
    /// <param name="dbId">Row id when the entry mirrors a database row (see <see cref="Entry.DbId"/>).</param>
    internal static void AddPenalty(int slot, PenaltyType penaltyType, DateTime endDateTime, int durationInMinutes, long revision,
        long dbId = 0)
    {
        if ((uint)penaltyType >= TypeCount) return;
        var entry = new Entry(endDateTime, durationInMinutes, false, revision != 0 ? revision : NextRevision(), dbId);
        Update(slot, current =>
        {
            var list = current.Get(penaltyType);
            Entry[] next;
            if (list == null)
            {
                next = [entry];
            }
            else
            {
                next = new Entry[list.Length + 1];
                list.CopyTo(next, 0);
                next[^1] = entry;
            }

            return current.With(penaltyType, next);
        });
    }

    /// <summary>
    /// Determines whether a player is currently penalized with the given penalty type.
    /// Read-only and allocation-free.
    /// </summary>
    /// <param name="slot">The player slot to check.</param>
    /// <param name="penaltyType">The penalty type to check.</param>
    /// <param name="endDateTime">The out-parameter returning the end datetime of the penalty if active.</param>
    /// <returns>True if the player has an active penalty, false otherwise.</returns>
    public static bool IsPenalized(int slot, PenaltyType penaltyType, out DateTime? endDateTime)
    {
        endDateTime = null;
        if ((uint)slot >= Slots.Length) return false;
        var state = Volatile.Read(ref Slots[slot]);
        if (state == null) return false;
        var list = state.Get(penaltyType);
        if (list == null) return false;

        // TimeMode 1 needs the clock; TimeMode 0 must not pay for it.
        var timeMode = CurrentTimeMode;
        return IsPenalized(list, timeMode, timeMode == 0 ? default : Time.ActualDateTime(), out endDateTime);
    }

    /// <summary>Core rule, separated for tests: which entry (if any) is active.</summary>
    internal static bool IsPenalized(Entry[] list, int timeMode, DateTime now, out DateTime? endDateTime)
    {
        foreach (var penalty in list)
        {
            if (penalty.Duration == 0)
            {
                endDateTime = penalty.EndDateTime;
                return true;
            }

            // TimeMode 0 (online time): a timed penalty ends when the DB marks its online minutes as used up.
            // TimeMode 1 (real time): it ends at EndDateTime.
            var active = timeMode == 0 ? !penalty.Passed : now < penalty.EndDateTime;
            if (!active) continue;
            endDateTime = penalty.EndDateTime;
            return true;
        }

        endDateTime = null;
        return false;
    }

    /// <summary>
    /// Retrieves all penalties for a player of a specific penalty type. Returns a copy.
    /// </summary>
    public static List<(DateTime EndDateTime, int Duration, bool Passed)> GetPlayerPenalties(int slot, PenaltyType penaltyType)
    {
        var result = new List<(DateTime EndDateTime, int Duration, bool Passed)>();
        AppendPenalties(slot, penaltyType, result);
        return result;
    }

    /// <summary>
    /// Retrieves all penalties for a player across multiple penalty types. Returns a copy.
    /// </summary>
    public static List<(DateTime EndDateTime, int Duration, bool Passed)> GetPlayerPenalties(int slot, List<PenaltyType> penaltyType)
    {
        var result = new List<(DateTime EndDateTime, int Duration, bool Passed)>();
        foreach (var type in penaltyType)
            AppendPenalties(slot, type, result);
        return result;
    }

    /// <summary>True when the slot has at least one penalty of any of the given types. Allocation-free.</summary>
    internal static bool HasAnyPenalty(int slot, PenaltyType a, PenaltyType b)
    {
        if ((uint)slot >= Slots.Length) return false;
        var state = Volatile.Read(ref Slots[slot]);
        return state != null && (state.Get(a) != null || state.Get(b) != null);
    }

    private static void AppendPenalties(int slot, PenaltyType type, List<(DateTime EndDateTime, int Duration, bool Passed)> target)
    {
        if ((uint)slot >= Slots.Length) return;
        var list = Volatile.Read(ref Slots[slot])?.Get(type);
        if (list == null) return;
        foreach (var e in list) target.Add((e.EndDateTime, e.Duration, e.Passed));
    }

    /// <summary>
    /// Retrieves all penalties for a player across all penalty types.
    /// Returns a new dictionary with new lists: callers (including other plugins through the API) can
    /// modify the result freely without affecting the plugin state.
    /// </summary>
    public static Dictionary<PenaltyType, List<(DateTime EndDateTime, int Duration, bool Passed)>> GetAllPlayerPenalties(int slot)
    {
        var result = new Dictionary<PenaltyType, List<(DateTime EndDateTime, int Duration, bool Passed)>>();
        if ((uint)slot >= Slots.Length) return result;
        var state = Volatile.Read(ref Slots[slot]);
        if (state == null) return result;
        for (var t = 0; t < TypeCount; t++)
        {
            var list = state.ByType[t];
            if (list == null) continue;
            var copy = new List<(DateTime EndDateTime, int Duration, bool Passed)>(list.Length);
            foreach (var e in list) copy.Add((e.EndDateTime, e.Duration, e.Passed));
            result[(PenaltyType)t] = copy;
        }

        return result;
    }

    /// <summary>
    /// Checks if a given slot has any penalties assigned.
    /// </summary>
    public static bool IsSlotInPenalties(int slot) => (uint)slot < Slots.Length && Volatile.Read(ref Slots[slot]) != null;

    /// <summary>
    /// Removes all penalties assigned to a specific player slot.
    /// </summary>
    public static void RemoveAllPenalties(int slot)
    {
        if ((uint)slot < Slots.Length) Volatile.Write(ref Slots[slot], null);
    }

    /// <summary>
    /// Removes all penalties for all players.
    /// </summary>
    public static void RemoveAllPenalties()
    {
        for (var i = 0; i < Slots.Length; i++) Volatile.Write(ref Slots[i], null);
    }

    /// <summary>
    /// Removes all penalties of a specific type from a player.
    /// </summary>
    public static void RemovePenaltiesByType(int slot, PenaltyType penaltyType)
    {
        if ((uint)penaltyType >= TypeCount) return;
        Update(slot, current => current.Get(penaltyType) == null ? current : current.With(penaltyType, null));
    }

    /// <summary>
    /// Removes the penalties of one type that were accepted at or before <paramref name="upToRevision"/>; later ones
    /// (a mute issued after the removal was accepted) stay.
    /// </summary>
    internal static void RemovePenaltiesByType(int slot, PenaltyType penaltyType, long upToRevision)
    {
        if ((uint)penaltyType >= TypeCount) return;
        Update(slot, current =>
        {
            var list = current.Get(penaltyType);
            if (list == null) return current;
            var keep = 0;
            foreach (var e in list)
                if (e.Revision > upToRevision) keep++;
            if (keep == list.Length) return current;

            var next = new Entry[keep];
            var j = 0;
            foreach (var e in list)
                if (e.Revision > upToRevision) next[j++] = e;
            return current.With(penaltyType, next);
        });
    }

    /// <summary>
    /// Makes the in-memory gag/mute/silence entries of a slot equal to the database state that was read at
    /// <paramref name="readRevision"/> (the accept position of the ordered read, see <see cref="Infrastructure.BoundedWorkQueue"/>).
    /// <list type="bullet">
    /// <item>A row not mirrored yet is added; a changed one (extended, shortened, converted to permanent) replaces its
    /// old entry; an entry whose row is gone (unmuted, expired, removed on another server) is dropped.</item>
    /// <item>Entries accepted <b>after</b> the read (<see cref="Entry.Revision"/> &gt; readRevision) are newer than this
    /// answer and are never dropped by it, so a stale answer cannot erase a newer mute or resurrect a removed one:
    /// the read ran in the player's ordered lane, i.e. after every earlier mute/unmute SQL and before every later one.</item>
    /// <item>Entries added through the public API (<see cref="ExternalDbId"/>) are not database state and stay.</item>
    /// <item>Several overlapping rows of any types stay several entries; a type's restriction holds while any entry does.</item>
    /// </list>
    /// </summary>
    /// <returns>True when a MUTE/SILENCE restriction was in force before and none is now (voice may be released).</returns>
    internal static bool ReconcileWithDatabase(int slot, long readRevision, IReadOnlyList<DbPenalty> rows)
    {
        var releasedVoice = false;
        Update(slot, current =>
        {
            releasedVoice = false;
            var changed = current;
            foreach (var type in SyncedTypes)
            {
                var existing = current.Get(type);
                var kept = new List<Entry>();
                var keptIds = new HashSet<long>();
                if (existing != null)
                    foreach (var e in existing)
                    {
                        // Newer than the read, or not database state at all: not this answer's business
                        if (e.Revision > readRevision || e.DbId == ExternalDbId)
                        {
                            kept.Add(e);
                            if (e.DbId > 0) keptIds.Add(e.DbId);
                        }
                    }

                foreach (var row in rows)
                {
                    if (row.Type != type || keptIds.Contains(row.Id)) continue;
                    kept.Add(new Entry(row.EndDateTime, row.Duration, false, readRevision, row.Id));
                }

                var before = existing ?? [];
                var same = before.Length == kept.Count;
                if (same)
                    for (var i = 0; i < before.Length && same; i++)
                        same = before[i] == kept[i];
                if (same) continue;

                changed = changed.With(type, kept.ToArray());
            }

            if (ReferenceEquals(changed, current)) return current;
            releasedVoice = HasVoice(current) && !HasVoice(changed);
            return changed;
        });
        return releasedVoice;
    }

    private static readonly PenaltyType[] SyncedTypes = [PenaltyType.Gag, PenaltyType.Mute, PenaltyType.Silence];

    private static bool HasVoice(SlotPenalties state) =>
        state.Get(PenaltyType.Mute) is { Length: > 0 } || state.Get(PenaltyType.Silence) is { Length: > 0 };

    /// <summary>
    /// Marks penalties with a specific end datetime as "passed" for a player (TimeMode 0).
    /// </summary>
    public static void RemovePenaltiesByDateTime(int slot, DateTime dateTime)
    {
        Update(slot, current =>
        {
            var changed = current;
            for (var t = 0; t < TypeCount; t++)
            {
                var list = changed.ByType[t];
                if (list == null) continue;
                Entry[]? copy = null;
                for (var i = 0; i < list.Length; i++)
                {
                    // The DB stores whole seconds; a penalty added in-game carries sub-second precision
                    if (list[i].Passed || Math.Abs((list[i].EndDateTime - dateTime).Ticks) >= TimeSpan.TicksPerSecond) continue;
                    copy ??= (Entry[])list.Clone();
                    copy[i] = copy[i] with { Passed = true };
                }

                if (copy != null) changed = changed.With((PenaltyType)t, copy);
            }

            return changed;
        });
    }

    /// <summary>
    /// Removes expired penalties across all players and drops empty types and slots.
    /// </summary>
    /// <remarks>
    /// If <c>TimeMode == 0</c>, penalties flagged as passed are removed.
    /// Otherwise, timed penalties whose end time has been reached are removed.
    /// </remarks>
    public static void RemoveExpiredPenalties()
    {
        var timeMode = CurrentTimeMode;
        RemoveExpiredPenalties(timeMode, timeMode == 0 ? default : Time.ActualDateTime());
    }

    internal static void RemoveExpiredPenalties(int timeMode, DateTime now)
    {
        for (var slot = 0; slot < Slots.Length; slot++)
        {
            if (Volatile.Read(ref Slots[slot]) == null) continue;
            Update(slot, current =>
            {
                var changed = current;
                for (var t = 0; t < TypeCount; t++)
                {
                    var list = changed.ByType[t];
                    if (list == null) continue;
                    var keep = 0;
                    foreach (var p in list)
                        if (!IsExpired(p, timeMode, now)) keep++;
                    if (keep == list.Length) continue;

                    var next = new Entry[keep];
                    var j = 0;
                    foreach (var p in list)
                        if (!IsExpired(p, timeMode, now)) next[j++] = p;
                    changed = changed.With((PenaltyType)t, next);
                }

                return changed;
            });
        }
    }

    private static bool IsExpired(Entry p, int timeMode, DateTime now) =>
        p.Duration > 0 && (timeMode == 0 ? p.Passed : now >= p.EndDateTime);
}
