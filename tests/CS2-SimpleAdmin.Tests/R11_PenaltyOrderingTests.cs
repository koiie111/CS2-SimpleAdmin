using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Entities;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdminApi;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// Third review, T1 and T2: a delayed unmute must not erase a penalty accepted after it, on one worker and on several,
/// and the voice state follows the restriction that is still in force.
/// The first four tests are the reviewer's reproductions (assertions unchanged); the rest extend them.
/// </summary>
public class R11_PenaltyOrderingTests
{
    private const ulong Steam = 76561198000000042;
    private static readonly PenaltyRemoval.Target Target = new(5, 11, Steam);

    private static PenaltyType Kind(int type) => type switch { 1 => PenaltyType.Mute, 2 => PenaltyType.Silence, _ => PenaltyType.Gag };
    private static string Name(int type) => type switch { 1 => "MUTE", 2 => "SILENCE", _ => "GAG" };

    private static int InMemory(PenaltyType type) => PlayerPenaltyManager.GetPlayerPenalties(Target.Slot, type).Count;

    // =====================================================================================================
    // The reviewer's reproductions (ported from third-probes/ThirdProbes.cs)
    // =====================================================================================================

    [Fact]
    public async Task DelayedUnmuteMustNotEraseNewMuteAcceptedBeforeItsApply()
    {
        await using var db = await TestDatabases.CreateAsync("SQLite");
        using var world = new TestWorld();
        var config = TestConfig.Use(c => { c.DatabaseConfig.DatabaseType = "SQLite"; c.MultiServerMode = true; });
        using var plugin = new TestPlugin(config, db.Provider);
        const ulong steam = 76561198000000042;
        var target = new PenaltyRemoval.Target(5, 11, steam);
        async Task Insert(string reason)
        {
            await using var c = await db.OpenAsync();
            await c.ExecuteAsync("INSERT INTO sa_mutes(player_steamid,player_name,admin_steamid,admin_name,reason,duration,ends,created,type,server_id) VALUES (@steam,'p',0,'Console',@reason,0,@t,@t,'MUTE',1)", new { steam = steam.ToString(), reason, t = DateTime.UtcNow });
        }
        await Insert("old");
        PlayerPenaltyManager.AddPenalty(target.Slot, PenaltyType.Mute, DateTime.Now, 0);
        PenaltyRemoval.IsTargetCurrent = _ => true;
        PenaltyRemoval.ResetVoice = _ => { };
        try
        {
            Assert.True(PenaltyRemoval.TryQueue(CallerRef.Console, "0", steam.ToString(), "remove old", 1, target, false));
            // SQL finished; the game thread has not yet consumed the old removal callback.
            await TestWorld.WaitUntil(() => world.World.Pending > 0);
            // A subsequent mute command accepts SQL work and immediately adds its in-memory penalty,
            // as Commands/basecomms.cs does. SQLite uses one queue worker, so its SQL runs after old apply.
            Assert.True(CS2_SimpleAdmin.TryQueuePenaltyWork(CallerRef.Console, null, "mute-write", _ => Insert("new")));
            PlayerPenaltyManager.AddPenalty(target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 0);
            await world.PumpUntil(() => Runtime.Db!.Pending == 0);
            await using var c = await db.OpenAsync();
            var active = await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sa_mutes WHERE reason='new' AND status='ACTIVE'");
            Assert.Equal(1, active);
            Assert.True(PlayerPenaltyManager.IsPenalized(target.Slot, PenaltyType.Mute, out _),
                "New mute is ACTIVE in real SQLite, but the delayed old unmute erased it in memory");
        }
        finally { PenaltyRemoval.RestoreDefaults(); }
    }

    [Theory]
    [InlineData(1, PenaltyType.Mute, PenaltyType.Silence)]
    [InlineData(2, PenaltyType.Silence, PenaltyType.Mute)]
    public void RemovingOneVoicePenaltyMustKeepOtherVoicePenalty(int type, PenaltyType removed, PenaltyType retained)
    {
        using var world = new TestWorld();
        TestConfig.Use();
        var target = new PenaltyRemoval.Target(5, 11, 76561198000000042);
        PlayerPenaltyManager.AddPenalty(target.Slot, removed, DateTime.Now, 0);
        PlayerPenaltyManager.AddPenalty(target.Slot, retained, DateTime.Now, 0);
        var voiceResets = 0;
        PenaltyRemoval.IsTargetCurrent = _ => true;
        PenaltyRemoval.ResetVoice = _ => voiceResets++;
        try
        {
            PenaltyRemoval.ApplyToTarget(target, type, false);
            Assert.True(PlayerPenaltyManager.IsPenalized(target.Slot, retained, out _));
            Assert.True(voiceResets == 0, "VoiceFlags.Normal was requested although a different voice penalty remains active");
        }
        finally { PenaltyRemoval.RestoreDefaults(); }
    }

    // =====================================================================================================
    // T2 – the voice follows what is still in force
    // =====================================================================================================

    private sealed class Voice : IDisposable
    {
        public int Resets;

        public Voice()
        {
            PenaltyRemoval.IsTargetCurrent = _ => true;
            PenaltyRemoval.ResetVoice = _ => Resets++;
        }

        public void Dispose() => PenaltyRemoval.RestoreDefaults();
    }

    [Fact]
    public void VoiceIsReleasedWhenNoVoicePenaltyRemains()
    {
        using var world = new TestWorld();
        TestConfig.Use();
        using var voice = new Voice();
        PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 60);
        PenaltyRemoval.ApplyToTarget(Target, 1, false);
        Assert.Equal(1, voice.Resets);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AGagThatRemainsNeverKeepsTheVoiceMuted(int type)
    {
        using var world = new TestWorld();
        TestConfig.Use();
        using var voice = new Voice();
        PlayerPenaltyManager.AddPenalty(Target.Slot, Kind(type), DateTime.Now.AddHours(1), 60);
        PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Gag, DateTime.Now.AddHours(1), 60);
        PenaltyRemoval.ApplyToTarget(Target, type, false);
        Assert.Equal(1, voice.Resets);
        Assert.True(PlayerPenaltyManager.IsPenalized(Target.Slot, PenaltyType.Gag, out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    public void RemovingAGagNeverTouchesTheVoice(int type, int voicePenalty)
    {
        using var world = new TestWorld();
        TestConfig.Use();
        using var voice = new Voice();
        PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Gag, DateTime.Now.AddHours(1), 60);
        PlayerPenaltyManager.AddPenalty(Target.Slot, Kind(voicePenalty), DateTime.Now.AddHours(1), 60);
        PenaltyRemoval.ApplyToTarget(Target, type, false);
        Assert.Equal(0, voice.Resets);
        Assert.True(PlayerPenaltyManager.IsPenalized(Target.Slot, Kind(voicePenalty), out _));
    }

    [Theory]
    [InlineData(1, 2, 0)] // permanent
    [InlineData(2, 1, 0)]
    [InlineData(1, 2, 60)] // timed, still running (TimeMode 1 = real time)
    [InlineData(2, 1, 60)]
    public void AnActiveOtherVoicePenaltyKeepsTheVoiceMuted(int removed, int kept, int minutes)
    {
        using var world = new TestWorld();
        TestConfig.Use(c => c.OtherSettings.TimeMode = 1);
        using var voice = new Voice();
        PlayerPenaltyManager.AddPenalty(Target.Slot, Kind(removed), Time.ActualDateTime().AddHours(1), 60);
        PlayerPenaltyManager.AddPenalty(Target.Slot, Kind(kept), minutes == 0 ? Time.ActualDateTime() : Time.ActualDateTime().AddMinutes(minutes), minutes);
        PenaltyRemoval.ApplyToTarget(Target, removed, false);
        Assert.Equal(0, voice.Resets);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void AnExpiredOtherVoicePenaltyDoesNotKeepTheVoiceMuted(int removed, int expired)
    {
        using var world = new TestWorld();
        TestConfig.Use(c => c.OtherSettings.TimeMode = 1);
        using var voice = new Voice();
        PlayerPenaltyManager.AddPenalty(Target.Slot, Kind(removed), Time.ActualDateTime().AddHours(1), 60);
        // timed, ended a minute ago, not yet swept by the periodic cleanup
        PlayerPenaltyManager.AddPenalty(Target.Slot, Kind(expired), Time.ActualDateTime().AddMinutes(-1), 5);
        PenaltyRemoval.ApplyToTarget(Target, removed, false);
        Assert.Equal(1, voice.Resets);
    }

    [Fact]
    public void OnlyTheMutedBitIsCleared()
    {
        var flags = VoiceFlags.Muted | VoiceFlags.ListenAll | VoiceFlags.Team;
        Assert.Equal(VoiceFlags.ListenAll | VoiceFlags.Team, PenaltyRemoval.WithoutMuted(flags));
        Assert.Equal(VoiceFlags.Normal, PenaltyRemoval.WithoutMuted(VoiceFlags.Muted));
        Assert.Equal(VoiceFlags.All, PenaltyRemoval.WithoutMuted(VoiceFlags.All));
    }

    // =====================================================================================================
    // T1 – revisions: which in-memory entries a removal may erase
    // =====================================================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ARemovalErasesOnlyWhatWasAcceptedBeforeIt(int type)
    {
        using var world = new TestWorld();
        TestConfig.Use();
        using var voice = new Voice();
        var kind = Kind(type);
        PlayerPenaltyManager.AddPenalty(Target.Slot, kind, DateTime.Now.AddHours(1), 60); // old 1
        PlayerPenaltyManager.AddPenalty(Target.Slot, kind, DateTime.Now.AddHours(2), 120); // old 2 (several of one type)
        var acceptedAt = PlayerPenaltyManager.NextRevision(); // the unmute is accepted here
        PlayerPenaltyManager.AddPenalty(Target.Slot, kind, DateTime.Now.AddHours(3), 180); // issued while the unmute waits

        PenaltyRemoval.ApplyToTarget(Target, type, false, acceptedAt);

        var left = PlayerPenaltyManager.GetPlayerPenalties(Target.Slot, kind);
        Assert.Single(left);
        Assert.Equal(180, left[0].Duration);
        Assert.Equal(0, voice.Resets); // a restriction (or, for a gag, nothing to do with voice) still applies
    }

    [Fact]
    public void AnEntryFromAConnectLoadAcceptedBeforeTheRemovalIsRemovedEvenIfAppliedAfterwards()
    {
        using var world = new TestWorld();
        TestConfig.Use();
        using var voice = new Voice();
        var loadAcceptedAt = PlayerPenaltyManager.NextRevision(); // connect load queued
        var unmuteAcceptedAt = PlayerPenaltyManager.NextRevision(); // unmute accepted while the load is in flight
        // the load result is applied only now, but it belongs to the earlier position
        PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 60, loadAcceptedAt);
        PenaltyRemoval.ApplyToTarget(Target, 1, false, unmuteAcceptedAt);
        Assert.False(PlayerPenaltyManager.IsPenalized(Target.Slot, PenaltyType.Mute, out _));
        Assert.Equal(1, voice.Resets);
    }

    [Fact]
    public void AnEntryFromAConnectLoadAcceptedAfterTheRemovalSurvives()
    {
        using var world = new TestWorld();
        TestConfig.Use();
        using var voice = new Voice();
        var unmuteAcceptedAt = PlayerPenaltyManager.NextRevision();
        var loadAcceptedAt = PlayerPenaltyManager.NextRevision();
        PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 60, loadAcceptedAt);
        PenaltyRemoval.ApplyToTarget(Target, 1, false, unmuteAcceptedAt);
        Assert.True(PlayerPenaltyManager.IsPenalized(Target.Slot, PenaltyType.Mute, out _));
        Assert.Equal(0, voice.Resets);
    }

    [Fact]
    public void ASlotThatHoldsANewConnectionKeepsEverythingItHas()
    {
        using var world = new TestWorld();
        TestConfig.Use();
        var resets = 0;
        // the targeted connection (userid 11) left; the slot now belongs to userid 12 with its own penalties
        PenaltyRemoval.IsTargetCurrent = t => t.UserId == 12;
        PenaltyRemoval.ResetVoice = _ => resets++;
        try
        {
            var acceptedAt = PlayerPenaltyManager.NextRevision();
            PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 60); // new connection's, loaded later
            CS2_SimpleAdmin.PlayersInfo[Steam] = new PlayerInfo(12, 5, new SteamID(Steam), "p", null, 0, 3, 0, 0, 0) { IsLoaded = true };

            PenaltyRemoval.ApplyToTarget(Target, 1, true, acceptedAt); // target = userid 11

            Assert.True(PlayerPenaltyManager.IsPenalized(Target.Slot, PenaltyType.Mute, out _));
            Assert.Equal(0, resets);
            Assert.Equal(3, CS2_SimpleAdmin.PlayersInfo[Steam].TotalMutes);
        }
        finally { PenaltyRemoval.RestoreDefaults(); }
    }

    [Fact]
    public void TheCounterIsLoweredOnceNoMatterHowManyEntriesWereRemoved()
    {
        using var world = new TestWorld();
        TestConfig.Use();
        using var voice = new Voice();
        CS2_SimpleAdmin.PlayersInfo[Steam] = new PlayerInfo(11, 5, new SteamID(Steam), "p", null, 0, 4, 0, 0, 0) { IsLoaded = true };
        PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 60);
        PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 60);
        var acceptedAt = PlayerPenaltyManager.NextRevision();
        PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 60);
        PenaltyRemoval.ApplyToTarget(Target, 1, true, acceptedAt);
        Assert.Equal(3, CS2_SimpleAdmin.PlayersInfo[Steam].TotalMutes);
        Assert.Equal(1, InMemory(PenaltyType.Mute));
    }

    // =====================================================================================================
    // T1 – the end-to-end order on one worker (real SQLite) and on several (model database with delays)
    // =====================================================================================================

    /// <summary>
    /// Shaped like MuteManager: <see cref="AddAsync"/> is an INSERT, <see cref="CloseAsync"/> reads the active rows and
    /// then closes the rows it read (exactly what <c>UnmutePlayer</c> does). The delays model a slow connection / a worker
    /// that picks the job up late, which is what lets two jobs of one player overtake each other on several workers.
    /// </summary>
    private sealed class ModelDb
    {
        private readonly object _gate = new();
        private readonly List<Row> _rows = [];

        private record struct Row(int Type, string Label, bool Active);

        public async Task AddAsync(int type, string label, int delayMs)
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            lock (_gate) _rows.Add(new Row(type, label, true));
        }

        public async Task<UnmuteOutcome> CloseAsync(int type, int delayBeforeReadMs)
        {
            if (delayBeforeReadMs > 0) await Task.Delay(delayBeforeReadMs);
            List<int> read;
            lock (_gate) read = Enumerable.Range(0, _rows.Count).Where(i => _rows[i].Type == type && _rows[i].Active).ToList();
            await Task.Delay(5);
            lock (_gate)
                foreach (var i in read)
                    _rows[i] = _rows[i] with { Active = false };
            return read.Count > 0 ? UnmuteOutcome.Removed : UnmuteOutcome.NothingActive;
        }

        public List<string> Active(int type)
        {
            lock (_gate) return _rows.Where(r => r.Type == type && r.Active).Select(r => r.Label).ToList();
        }
    }

    private sealed class OrderFixture : IDisposable
    {
        public readonly TestWorld World;
        public readonly ModelDb Db = new();
        public readonly List<string> Console = [];
        private readonly Action<string> _printToConsole = CallerRef.PrintToConsole;
        public int VoiceResets;
        public bool Keyed = true;

        public OrderFixture(bool singleWorker)
        {
            World = new TestWorld(sqlite: singleWorker);
            CS2_SimpleAdmin.DatabaseProvider = new OutageProvider(); // the model database replaces every SQL call; this only marks the plugin as connected
            TestConfig.Use();
            CallerRef.PrintToConsole = m => { lock (Console) Console.Add(m); };
            PenaltyRemoval.IsTargetCurrent = _ => true;
            PenaltyRemoval.ResetVoice = _ => Interlocked.Increment(ref VoiceResets);
            PenaltyRemoval.UnmuteSql = (_, _, _, _) => Task.FromException<UnmuteOutcome>(new InvalidOperationException("set per test"));
        }

        /// <summary>The "mute" command: SQL accepted, then the in-memory penalty added at once (as Commands/basecomms.cs does).</summary>
        public void Issue(int type, string label, int sqlDelayMs = 0)
        {
            Assert.True(CS2_SimpleAdmin.TryQueuePenaltyWork(CallerRef.Console, null, "mute-write",
                _ => Db.AddAsync(type, label, sqlDelayMs), orderKey: Keyed ? Steam : null));
            PlayerPenaltyManager.AddPenalty(Target.Slot, Kind(type), DateTime.Now.AddHours(1), 60);
        }

        public void Remove(int type, int sqlDelayBeforeReadMs = 0)
        {
            PenaltyRemoval.UnmuteSql = (_, _, _, t) => Db.CloseAsync(t, sqlDelayBeforeReadMs);
            Assert.True(PenaltyRemoval.TryQueue(CallerRef.Console, "0", Steam.ToString(), "r", type, Target, false, "Player"));
        }

        public Task SettleAsync() => World.PumpUntil(() => Runtime.Db!.Pending == 0 && World.World.Pending == 0);

        public void Dispose()
        {
            CallerRef.PrintToConsole = _printToConsole;
            PenaltyRemoval.RestoreDefaults();
            World.Dispose();
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task MuteAcceptedBeforeTheUnmuteIsClosedByIt_InSqlAndInMemory(int type, bool singleWorker)
    {
        using var f = new OrderFixture(singleWorker);
        f.Issue(type, "first", sqlDelayMs: 150); // slow INSERT; on several workers the unmute would overtake it
        f.Remove(type);
        await f.SettleAsync();

        Assert.Empty(f.Db.Active(type));
        Assert.Equal(0, InMemory(Kind(type)));
        Assert.Contains(f.Console, m => m.StartsWith(type switch { 1 => "Unmuted", 2 => "Unsilenced", _ => "Ungagged" }));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task MuteAcceptedAfterTheUnmuteSurvivesIt_InSqlAndInMemory(int type, bool singleWorker)
    {
        using var f = new OrderFixture(singleWorker);
        // an older penalty exists in both places
        await f.Db.AddAsync(type, "old", 0);
        PlayerPenaltyManager.AddPenalty(Target.Slot, Kind(type), DateTime.Now.AddHours(1), 60);

        f.Remove(type, sqlDelayBeforeReadMs: 150); // slow to start; a fast INSERT accepted later could overtake it
        f.Issue(type, "new");
        await f.SettleAsync();

        Assert.Equal(["new"], f.Db.Active(type));
        Assert.Equal(1, InMemory(Kind(type)));
        Assert.True(PlayerPenaltyManager.IsPenalized(Target.Slot, Kind(type), out _));
        Assert.Equal(0, f.VoiceResets); // the new mute/silence is in force; a gag has nothing to do with voice
    }

    [Fact]
    public async Task ControlWithoutAnOrderKeyTheSameRaceDoesHappenOnSeveralWorkers()
    {
        // Sensitivity check for the two tests above: take the order key away from the issue and the model database
        // ends up disagreeing with memory. If this stops failing the model no longer exercises the race.
        using var f = new OrderFixture(singleWorker: false) { Keyed = false };
        f.Issue(1, "first", sqlDelayMs: 150);
        f.Remove(1);
        await f.SettleAsync();

        Assert.Equal(["first"], f.Db.Active(1)); // the unmute overtook the INSERT: the row is ACTIVE in SQL ...
        Assert.Equal(0, InMemory(PenaltyType.Mute)); // ... while memory believes the player is unmuted
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnmuteThenMuteThenUnmuteLeavesNothing(bool singleWorker)
    {
        using var f = new OrderFixture(singleWorker);
        f.Issue(1, "a", sqlDelayMs: 100);
        f.Remove(1, sqlDelayBeforeReadMs: 60);
        f.Issue(1, "b", sqlDelayMs: 20);
        f.Remove(1);
        await f.SettleAsync();
        Assert.Empty(f.Db.Active(1));
        Assert.Equal(0, InMemory(PenaltyType.Mute));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeveralRowsOfOneTypeAreAllClosedAndOnlyTheLaterOneStays(bool singleWorker)
    {
        using var f = new OrderFixture(singleWorker);
        f.Issue(1, "a", sqlDelayMs: 80);
        f.Issue(1, "b", sqlDelayMs: 10);
        f.Remove(1, sqlDelayBeforeReadMs: 30);
        f.Issue(1, "c");
        f.Issue(1, "d", sqlDelayMs: 50);
        await f.SettleAsync();
        Assert.Equal(["c", "d"], f.Db.Active(1).Order().ToList());
        Assert.Equal(2, InMemory(PenaltyType.Mute));
    }

    [Fact]
    public async Task MuteAndSilenceOfOnePlayerKeepTheirOwnVoiceDecision()
    {
        using var f = new OrderFixture(singleWorker: false);
        f.Issue(1, "m");
        f.Issue(2, "s");
        f.Remove(1);
        await f.SettleAsync();
        Assert.Empty(f.Db.Active(1));
        Assert.Equal(["s"], f.Db.Active(2));
        Assert.Equal(0, f.VoiceResets); // silence still in force: voice stays muted
        f.Remove(2);
        await f.SettleAsync();
        Assert.Equal(1, f.VoiceResets);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailedUnmuteChangesNothingAndDoesNotBlockWhatFollows(bool singleWorker)
    {
        using var f = new OrderFixture(singleWorker);
        await f.Db.AddAsync(1, "old", 0);
        PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 60);
        PenaltyRemoval.UnmuteSql = (_, _, _, _) => Task.FromResult(UnmuteOutcome.Failed);
        Assert.True(PenaltyRemoval.TryQueue(CallerRef.Console, "0", Steam.ToString(), "r", 1, Target, false, "Player"));
        f.Issue(1, "new");
        await f.SettleAsync();

        Assert.Equal(["new", "old"], f.Db.Active(1).Order().ToList());
        Assert.Equal(2, InMemory(PenaltyType.Mute)); // old (still in force: the SQL failed) and new
        Assert.Equal(0, f.VoiceResets);
        Assert.Contains(f.Console, m => m.Contains("could NOT be saved"));
        Assert.DoesNotContain(f.Console, m => m.StartsWith("Unmuted"));

    }

    [Fact]
    public async Task TheAdminHearsTheResultOnlyAfterTheSqlFinished()
    {
        using var f = new OrderFixture(singleWorker: true);
        var gate = new TaskCompletionSource<UnmuteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlayerPenaltyManager.AddPenalty(Target.Slot, PenaltyType.Mute, DateTime.Now.AddHours(1), 60);
        PenaltyRemoval.UnmuteSql = (_, _, _, _) => gate.Task;
        Assert.True(PenaltyRemoval.TryQueue(CallerRef.Console, "0", Steam.ToString(), "r", 1, Target, false, "Bob"));

        await Task.Delay(50);
        f.World.World.RunOneUpdate();
        Assert.Empty(f.Console); // queued only: no success message, memory untouched
        Assert.True(PlayerPenaltyManager.IsPenalized(Target.Slot, PenaltyType.Mute, out _));

        gate.SetResult(UnmuteOutcome.Removed);
        await f.World.PumpUntil(() => f.Console.Count > 0);
        Assert.Equal(["Unmuted Bob."], f.Console);
        Assert.False(PlayerPenaltyManager.IsPenalized(Target.Slot, PenaltyType.Mute, out _));
    }

    [Fact]
    public async Task NothingActiveIsReportedAsSuchNotAsSuccess()
    {
        using var f = new OrderFixture(singleWorker: true);
        PenaltyRemoval.UnmuteSql = (_, _, _, _) => Task.FromResult(UnmuteOutcome.NothingActive);
        Assert.True(PenaltyRemoval.TryQueue(CallerRef.Console, "0", Steam.ToString(), "r", 2, Target, false, "Bob"));
        await f.World.PumpUntil(() => f.Console.Count > 0);
        Assert.Equal(["Bob: no active silence found (nothing to remove)."], f.Console);
    }

    // =====================================================================================================
    // The sequencer itself
    // =====================================================================================================

    [Fact]
    public async Task JobsOfOnePlayerRunOneAfterAnotherWhileOtherPlayersAreNotHeldBack()
    {
        using var world = new TestWorld(sqlite: false); // 4 workers
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        void Log(string s) { lock (log) log.Add(s); }
        var ctx = new WorkContext(Runtime.Context, 1);

        Assert.True(Runtime.TryQueueDbOrdered("a", async _ => { Log("a:start"); await gate.Task; Log("a:end"); }, ctx, 1));
        await TestWorld.WaitUntil(() => log.Contains("a:start"));
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(Runtime.TryQueueDbOrdered("b", _ => { Log("b:start"); return Task.CompletedTask; }, ctx, 1)); // same player
        Assert.True(Runtime.TryQueueDbOrdered("c", _ => { Log("c:start"); return Task.CompletedTask; }, ctx, 2)); // another player
        Assert.True(started.ElapsedMilliseconds < 500, "accepting work must not wait for the job in front of it");

        await TestWorld.WaitUntil(() => log.Contains("c:start"));
        await Task.Delay(100);
        lock (log) Assert.DoesNotContain("b:start", log);

        gate.SetResult();
        await TestWorld.WaitUntil(() => log.Contains("b:start"));
        lock (log) Assert.True(log.IndexOf("a:end") < log.IndexOf("b:start"));
        await TestWorld.WaitUntil(() => Runtime.Db!.ActiveKeys == 0);
    }

    [Fact]
    public async Task AFailedOrCancelledPredecessorStillLetsTheNextJobRun()
    {
        using var world = new TestWorld(sqlite: false);
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = new WorkContext(Runtime.Context, 1);
        Assert.True(Runtime.TryQueueDbOrdered("fails", _ => throw new InvalidOperationException("boom"), ctx, 7));
        Assert.True(Runtime.TryQueueDbOrdered("next", _ => { ran.SetResult(); return Task.CompletedTask; }, ctx, 7));
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await TestWorld.WaitUntil(() => Runtime.Db!.ActiveKeys == 0);
    }

    [Fact]
    public async Task ARefusedJobGivesItsPlaceBack()
    {
        using var world = new TestWorld(sqlite: true, dbCapacity: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = new WorkContext(Runtime.Context, 1);
        Assert.True(Runtime.TryQueueDb("blocker", async _ => { started.SetResult(); await gate.Task; }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Runtime.TryQueueDb("filler", _ => Task.CompletedTask)); // the single slot
        Assert.False(Runtime.TryQueueDbOrdered("refused", _ => Task.CompletedTask, ctx, 9));
        Assert.Equal(0, Runtime.Db!.ActiveKeys); // the refused job holds no place

        gate.SetResult();
        await TestWorld.WaitUntil(() => Runtime.Db!.Pending == 0);
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Runtime.TryQueueDbOrdered("later", _ => { ran.SetResult(); return Task.CompletedTask; }, ctx, 9));
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ARestartStartsWithAnEmptySequencer()
    {
        using var world = new TestWorld(sqlite: false);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Runtime.TryQueueDbOrdered("stuck", async _ => { started.SetResult(); await gate.Task; },
            new WorkContext(Runtime.Context, 1), 5));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var next = new TestWorld(sqlite: false); // a new lifetime and queues (the old job is still stuck)
        Assert.Equal(0, Runtime.Db!.ActiveKeys);
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Runtime.TryQueueDbOrdered("fresh", _ => { ran.SetResult(); return Task.CompletedTask; },
            new WorkContext(Runtime.Context, 1), 5));
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(2)); // not blocked by the dead lifetime's ticket
        gate.SetResult();
    }

    // =====================================================================================================
    // The real database: SQLite (one worker) and every reachable MySQL/MariaDB (several workers)
    // =====================================================================================================

    public static IEnumerable<object[]> Engines() => TestDatabases.All();

    [Theory, MemberData(nameof(Engines))]
    public async Task RealDatabaseAgreesWithMemoryForBothOrders(string engine)
    {
        TestDatabases.SkipIfNoServer(engine);
        await using var db = await TestDatabases.CreateAsync(engine);
        using var world = new TestWorld(sqlite: db.IsSqlite);
        var config = TestConfig.Use(c => { c.DatabaseConfig.DatabaseType = db.IsSqlite ? "SQLite" : "MySQL"; c.MultiServerMode = true; });
        using var plugin = new TestPlugin(config, db.Provider);
        PenaltyRemoval.IsTargetCurrent = _ => true;
        PenaltyRemoval.ResetVoice = _ => { };
        try
        {
            async Task Insert(int type, string reason, ulong steam)
            {
                await using var c = await db.OpenAsync();
                await c.ExecuteAsync("INSERT INTO sa_mutes(player_steamid,player_name,admin_steamid,admin_name,reason,duration,ends,created,type,server_id) VALUES (@steam,'p',0,'Console',@reason,0,@t,@t,@type,1)",
                    new { steam = steam.ToString(), reason, t = DateTime.UtcNow.AddHours(1), type = Name(type) });
            }

            async Task<List<string>> ActiveReasons(int type, ulong steam)
            {
                await using var c = await db.OpenAsync();
                return (await c.QueryAsync<string>("SELECT reason FROM sa_mutes WHERE player_steamid=@steam AND type=@type AND status='ACTIVE' ORDER BY reason",
                    new { steam = steam.ToString(), type = Name(type) })).ToList();
            }

            void Issue(int type, string reason, ulong steam)
            {
                Assert.True(CS2_SimpleAdmin.TryQueuePenaltyWork(CallerRef.Console, null, "mute-write", _ => Insert(type, reason, steam), orderKey: steam));
                PlayerPenaltyManager.AddPenalty(Target.Slot, Kind(type), DateTime.Now.AddHours(1), 60);
            }

            // A fresh player (SteamID) per round, so rounds never see each other's rows
            var next = Steam + 1000;
            foreach (var type in new[] { 0, 1, 2 })
            for (var round = 0; round < 8; round++)
            {
                // mute, then unmute: the row must be closed
                var first = next++;
                PlayerPenaltyManager.RemoveAllPenalties();
                Issue(type, "a", first);
                Assert.True(PenaltyRemoval.TryQueue(CallerRef.Console, "0", first.ToString(), "r", type, new PenaltyRemoval.Target(Target.Slot, 11, first), false));
                await world.PumpUntil(() => Runtime.Db!.Pending == 0 && world.World.Pending == 0);
                Assert.Empty(await ActiveReasons(type, first));
                Assert.Equal(0, InMemory(Kind(type)));

                // unmute, then mute: the new row must stay
                var second = next++;
                PlayerPenaltyManager.RemoveAllPenalties();
                await Insert(type, "old", second);
                PlayerPenaltyManager.AddPenalty(Target.Slot, Kind(type), DateTime.Now.AddHours(1), 60);
                Assert.True(PenaltyRemoval.TryQueue(CallerRef.Console, "0", second.ToString(), "r", type, new PenaltyRemoval.Target(Target.Slot, 11, second), false));
                Issue(type, "new", second);
                await world.PumpUntil(() => Runtime.Db!.Pending == 0 && world.World.Pending == 0);
                Assert.Equal(["new"], await ActiveReasons(type, second));
                Assert.Equal(1, InMemory(Kind(type)));
            }
        }
        finally { PenaltyRemoval.RestoreDefaults(); }
    }
}
