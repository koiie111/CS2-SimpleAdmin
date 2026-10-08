using System.Diagnostics;
using CounterStrikeSharp.API;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;
using CS2_SimpleAdminApi;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// Native storage of the voice flags per slot, like the engine's player object: it is NOT cleared when the plugin's own
/// state is (hot reload). The real <see cref="VoiceBit"/> logic runs on top of it; only the controller access is faked.
/// A session that is no longer the connection of its slot reads/writes nothing, as <c>ResolveController</c> does.
/// </summary>
internal sealed class FakeNativeVoice
{
    public readonly Dictionary<int, VoiceFlags> Slots = [];
    public int Writes;

    public VoiceFlags this[int slot]
    {
        get => Slots.GetValueOrDefault(slot);
        set => Slots[slot] = value;
    }

    public void Install()
    {
        VoiceBit.Read = s => Runtime.Sessions.IsCurrent(s) && Slots.TryGetValue(s.Slot, out var f) ? f : null;
        VoiceBit.Write = (s, f) =>
        {
            if (!Runtime.Sessions.IsCurrent(s) || !Slots.ContainsKey(s.Slot)) return;
            Slots[s.Slot] = f;
            Writes++;
        };
    }

    public static void Uninstall()
    {
        VoiceBit.Read = static _ => null;
        VoiceBit.Write = static (_, _) => { };
    }
}

public class VoiceBitTests
{
    private const VoiceFlags Other = VoiceFlags.ListenAll | VoiceFlags.Team;

    private static PlayerManager.LoadResult Loaded(long revision, params ActiveMuteRow[] rows) =>
        new(false, new PlayerPenaltyStats(), rows.ToList(), [], revision);

    private static ActiveMuteRow Row(long id, string type, int duration = 0, DateTime? ends = null) =>
        new() { Id = id, Type = type, Duration = duration, Ends = ends };

    private static async Task<(ConnectEnforcementTests.Harness H, FlakyQueriesProvider Flaky, TestDatabase Db, PlayerSession S)> Start(VoiceFlags native)
    {
        var (h, flaky, db) = await ConnectEnforcementTests.With();
        PlayerPenaltyManager.RemoveAllPenalties();
        var s = h.Connect();
        h.Native[s.Slot] = native;
        PeriodicMaintenance.VoiceEffect = VoiceBit.Set;
        return (h, flaky, db, s);
    }

    /// <summary>The connect load, with the answer given explicitly.</summary>
    private static void Apply(ConnectEnforcementTests.Harness h, PlayerSession s, PlayerManager.LoadResult result)
    {
        var attempt = s.TryBeginLoad(Stopwatch.GetTimestamp());
        Assert.True(attempt > 0);
        PlayerManager.ApplyLoadResult(s, attempt, result, h.Config);
    }

    /// <summary>Hot reload of the plugin: its penalty state and sessions are rebuilt, the native player object is not.</summary>
    private static PlayerSession HotReload(PlayerSession old)
    {
        PlayerPenaltyManager.RemoveAllPenalties();
        Runtime.Sessions.End(old.Slot);
        return Runtime.Sessions.BeginOrGet(old.Slot, old.SteamId, old.UserId, "probe", null, out _);
    }

    private static DateTime In(int minutes) => Time.ActualDateTime().AddMinutes(minutes);

    [Fact]
    public void BitHelperSetsAndClearsOnlyMuted()
    {
        Assert.Equal(Other | VoiceFlags.Muted, VoiceBit.With(Other, true));
        Assert.Equal(Other, VoiceBit.With(Other | VoiceFlags.Muted, false));
        Assert.Equal(VoiceFlags.Normal, VoiceBit.With(VoiceFlags.Muted, false));
        Assert.Equal(VoiceFlags.Muted, VoiceBit.With(VoiceFlags.Muted, true));
    }

    [Fact]
    public async Task ControlActiveMuteSetsTheNativeFlag()
    {
        var (h, _, db, s) = await Start(Other);
        using var _h = h; await using var _db = db;
        Apply(h, s, Loaded(1, Row(1, "MUTE", 30, In(30))));
        Assert.Equal(Other | VoiceFlags.Muted, h.Native[s.Slot]);
    }

    [Theory]
    [InlineData("MUTE")]
    [InlineData("SILENCE")]
    public async Task NativeMutedFromAnEarlierPluginIsClearedByAnAuthoritativeEmptyLoad(string type)
    {
        var (h, _, db, s) = await Start(Other);
        using var _h = h; await using var _db = db;
        Apply(h, s, Loaded(1, Row(1, type, 30, In(30))));
        Assert.True(h.Native[s.Slot].HasFlag(VoiceFlags.Muted));

        s = HotReload(s);                                        // the mute was lifted elsewhere before this server synced
        Assert.True(h.Native[s.Slot].HasFlag(VoiceFlags.Muted)); // the reload itself leaves the engine flag as it was
        Apply(h, s, Loaded(5));                                  // successful empty answer

        Assert.Equal(Other, h.Native[s.Slot]);                   // Muted gone, the other bits stay
        Assert.False(PenaltyRemoval.HasVoiceRestriction(s.Slot));
    }

    [Fact]
    public async Task GagOnlyLoadLeavesVoiceUnmuted()
    {
        var (h, _, db, s) = await Start(VoiceFlags.Muted | Other);
        using var _h = h; await using var _db = db;
        Apply(h, s, Loaded(1, Row(1, "GAG", 30, In(30))));
        Assert.Equal(Other, h.Native[s.Slot]);
        Assert.True(PlayerPenaltyManager.IsPenalized(s.Slot, PenaltyType.Gag, out _));
    }

    [Theory]
    [InlineData("MUTE", 30)]
    [InlineData("SILENCE", 30)]
    [InlineData("MUTE", 0)]
    [InlineData("SILENCE", 0)]
    public async Task ActiveTimedOrPermanentRestrictionKeepsMuted(string type, int duration)
    {
        var (h, _, db, s) = await Start(VoiceFlags.Muted | Other);
        using var _h = h; await using var _db = db;
        Apply(h, s, Loaded(1, Row(1, type, duration, duration == 0 ? null : In(duration))));
        Assert.Equal(VoiceFlags.Muted | Other, h.Native[s.Slot]);
        Assert.Equal(0, h.Native.Writes);                        // nothing to correct: no write at all
    }

    [Theory]
    [InlineData(PenaltyType.Mute)]
    [InlineData(PenaltyType.Silence)]
    public async Task ANewerLocalRestrictionAcceptedDuringTheReadDecidesVoiceEvenIfTheReadWasEmpty(PenaltyType type)
    {
        var (h, _, db, s) = await Start(VoiceFlags.Normal);
        using var _h = h; await using var _db = db;
        var readRevision = PlayerPenaltyManager.NextRevision();
        PlayerPenaltyManager.AddPenalty(s.Slot, type, In(10), 10, PlayerPenaltyManager.NextRevision(), 0);
        Apply(h, s, Loaded(readRevision));                       // the read returned []
        Assert.Equal(VoiceFlags.Muted, h.Native[s.Slot]);
    }

    [Fact]
    public async Task ARetainedApiRestrictionAlsoCounts()
    {
        var (h, _, db, s) = await Start(Other);
        using var _h = h; await using var _db = db;
        PlayerPenaltyManager.AddPenalty(s.Slot, PenaltyType.Mute, In(60), 60);       // another plugin through the API
        Apply(h, s, Loaded(PlayerPenaltyManager.NextRevision()));
        Assert.Equal(Other | VoiceFlags.Muted, h.Native[s.Slot]);
    }

    [Fact]
    public async Task OverlappingMuteAndSilenceReleaseVoiceOnlyWithTheLastOne()
    {
        var (h, _, db, s) = await Start(Other);
        using var _h = h; await using var _db = db;
        var mute = await Net.Mute(db, s.SteamId, "MUTE", duration: 30);
        var silence = await Net.Mute(db, s.SteamId, "SILENCE", duration: 30);
        await h.Load(s);
        Assert.Equal(Other | VoiceFlags.Muted, h.Native[s.Slot]);

        async Task Sync() { PeriodicMaintenance.QueueMuteSync(h.Config, [s]); await h.Idle(); }

        await Net.Exec(db, "UPDATE sa_mutes SET status = 'UNMUTED' WHERE id = @id", new { id = mute });
        await Sync();
        Assert.Equal(Other | VoiceFlags.Muted, h.Native[s.Slot]);   // the silence still holds

        await Net.Exec(db, "UPDATE sa_mutes SET status = 'UNMUTED' WHERE id = @id", new { id = silence });
        await Sync();
        Assert.Equal(Other, h.Native[s.Slot]);
    }

    [Fact]
    public async Task EndToEndDatabaseLoadAfterHotReloadClearsALiftedMuteAndKeepsAnActiveOne()
    {
        var (h, _, db, s) = await Start(Other);
        using var _h = h; await using var _db = db;
        var id = await Net.Mute(db, s.SteamId, "MUTE", duration: 30);
        await h.Load(s);
        Assert.True(h.Native[s.Slot].HasFlag(VoiceFlags.Muted));

        await Net.Exec(db, "UPDATE sa_mutes SET status = 'UNMUTED' WHERE id = @id", new { id });   // lifted on the site
        s = HotReload(s);
        await h.Load(s);
        Assert.Equal(Other, h.Native[s.Slot]);

        await Net.Mute(db, s.SteamId, "SILENCE", duration: 30);                                     // still active across a reload
        s = HotReload(s);
        await h.Load(s);
        Assert.Equal(Other | VoiceFlags.Muted, h.Native[s.Slot]);
    }

    [Fact]
    public async Task AFailedSyncNeverReleasesAKnownMute()
    {
        var (h, flaky, db, s) = await Start(Other);
        using var _h = h; await using var _db = db;
        await Net.Mute(db, s.SteamId, "MUTE", duration: 30);
        await h.Load(s);
        Assert.True(h.Native[s.Slot].HasFlag(VoiceFlags.Muted));

        flaky.FailMutes = true;                                  // unreadable database: an error, not "nothing active"
        PeriodicMaintenance.QueueMuteSync(h.Config, [s]);
        await h.Idle();
        Assert.True(h.Native[s.Slot].HasFlag(VoiceFlags.Muted));
        Assert.True(PenaltyRemoval.HasVoiceRestriction(s.Slot));
    }

    [Fact]
    public async Task AFailedConnectLoadLeavesTheNativeFlagAlone()
    {
        var (h, flaky, db, s) = await Start(VoiceFlags.Muted | Other);
        using var _h = h; await using var _db = db;
        flaky.FailMutes = true;
        h.Manager.QueueLoad(s, Stopwatch.GetTimestamp());
        await h.Settle(s);
        await h.Idle();
        Assert.NotEqual(ConnectLoadState.Loaded, s.LoadState);
        Assert.Equal(VoiceFlags.Muted | Other, h.Native[s.Slot]);
    }

    [Fact]
    public async Task AStaleLoadForAReusedSlotDoesNotTouchTheNewConnectionsVoice()
    {
        var (h, _, db, old) = await Start(Other);
        using var _h = h; await using var _db = db;
        var attempt = old.TryBeginLoad(Stopwatch.GetTimestamp());
        Runtime.Sessions.End(old.Slot);                          // the player leaves while the read is in flight
        var newcomer = Runtime.Sessions.BeginOrGet(old.Slot, 76561198100100009, 555, "new", null, out _);
        h.Native[newcomer.Slot] = VoiceFlags.Muted | VoiceFlags.ListenAll;

        PlayerManager.ApplyLoadResult(old, attempt, Loaded(1, Row(1, "MUTE", 30, In(30))), h.Config);   // would set
        Assert.Equal(VoiceFlags.Muted | VoiceFlags.ListenAll, h.Native[newcomer.Slot]);
        PlayerManager.ApplyLoadResult(old, attempt, Loaded(2), h.Config);                                // would clear
        Assert.Equal(VoiceFlags.Muted | VoiceFlags.ListenAll, h.Native[newcomer.Slot]);
        Assert.False(PenaltyRemoval.HasVoiceRestriction(newcomer.Slot));
        Assert.Equal(0, h.Native.Writes);
    }

    [Fact]
    public async Task AStaleSyncForAReusedSlotDoesNotTouchTheNewConnectionsVoice()
    {
        var (h, _, db, old) = await Start(Other);
        using var _h = h; await using var _db = db;
        await h.Load(old);
        PeriodicMaintenance.SyncHook = (_, _) =>
        {
            Runtime.Sessions.End(old.Slot);
            Runtime.Sessions.BeginOrGet(old.Slot, 76561198100100009, 555, "new", null, out _);
            h.Native[old.Slot] = VoiceFlags.Muted | VoiceFlags.ListenAll;
            return Task.CompletedTask;
        };
        try
        {
            await Net.Mute(db, old.SteamId, "MUTE", duration: 30);
            PeriodicMaintenance.QueueMuteSync(h.Config, [old]);
            await h.Idle();
        }
        finally { PeriodicMaintenance.SyncHook = null; }

        Assert.Equal(VoiceFlags.Muted | VoiceFlags.ListenAll, h.Native[old.Slot]);
        Assert.Equal(0, h.Native.Writes);
    }
}
