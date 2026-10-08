using System.Diagnostics;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdminApi;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// Enforcement at connect time and while online, against a real database: the real load, decision, apply and sync code
/// runs; only the engine-facing operations (controller lookup, voice flags, kick, timers) are recorded seams.
/// </summary>
public class ConnectEnforcementTests
{
    internal sealed class Harness : IDisposable
    {
        public readonly TestWorld World;
        public readonly TestPlugin Plugin;
        public readonly PlayerManager Manager = new();
        public readonly CS2_SimpleAdminConfig Config;
        public readonly List<(TimeSpan Delay, Action Callback)> Scheduled = [];
        public readonly List<PlayerSession> Applied = [];
        public readonly List<PlayerSession> Kicked = [];
        public readonly List<PlayerSession> KickedUnverified = [];
        public readonly List<bool> Voice = [];

        public Harness(Database.IDatabaseProvider provider, bool sqliteWorkers = true, int dbCapacity = Runtime.DbQueueCapacity,
            PluginState state = PluginState.Ready, int timeMode = 1, bool multiServerMode = true, int banType = 1,
            bool checkMulti = false)
        {
            World = new TestWorld(sqlite: sqliteWorkers, state: state, dbCapacity: dbCapacity);
            Config = TestConfig.Use(c =>
            {
                c.DatabaseConfig.DatabaseType = "SQLite";
                c.OtherSettings.CheckMultiAccountsByIp = checkMulti;
                c.OtherSettings.BanType = banType;
                c.OtherSettings.TimeMode = timeMode;
                c.MultiServerMode = multiServerMode;
            });
            Plugin = new TestPlugin(Config, provider);
            PlayerManager.RetryScheduler = (delay, callback) => Scheduled.Add((delay, callback));
            PlayerManager.ControllerAvailable = s => Runtime.Sessions.IsCurrent(s);
            PlayerManager.NativeEffects = (session, _, _, _) => Applied.Add(session);
            PlayerManager.KickBanned = s => Kicked.Add(s);
            PlayerManager.KickUnverified = (s, _) => KickedUnverified.Add(s);
            PlayerManager.NotifyAdminsEffect = (_, _) => { };
            PeriodicMaintenance.VoiceEffect = (_, muted) => Voice.Add(muted);
        }

        public PlayerSession Connect(int slot = 1, ulong steam = 76561198100100001, string? ip = null)
            => Runtime.Sessions.BeginOrGet(slot, steam, slot + 100, "probe", ip, out _);

        public Task Settle(PlayerSession s) => World.PumpUntil(() => s.LoadState != ConnectLoadState.InFlight);

        public Task Idle() => World.PumpUntil(() => Runtime.Db!.Pending == 0 && World.World.Pending == 0);

        public async Task Load(PlayerSession s)
        {
            Manager.QueueLoad(s, Stopwatch.GetTimestamp());
            await Settle(s);
            await Idle();
        }

        public void Dispose()
        {
            PlayerManager.RetryScheduler = static (_, _) => { };
            PlayerManager.KickBanned = static _ => { };
            PlayerManager.KickUnverified = static (_, _) => { };
            PlayerManager.NotifyAdminsEffect = static (_, _) => { };
            PlayerManager.NativeEffects = static (_, _, _, _) => { };
            PeriodicMaintenance.VoiceEffect = static (_, _) => { };
            Plugin.Dispose();
            World.Dispose();
        }
    }

    internal static async Task<(Harness H, FlakyQueriesProvider Provider, TestDatabase Db)> With(bool sqliteWorkers = true,
        int dbCapacity = Runtime.DbQueueCapacity, PluginState state = PluginState.Ready, int timeMode = 1, bool multiServerMode = true,
        int banType = 1)
    {
        var db = await TestDatabases.CreateAsync("SQLite");
        var provider = new FlakyQueriesProvider(db.Provider);
        return (new Harness(provider, sqliteWorkers, dbCapacity, state, timeMode, multiServerMode, banType), provider, db);
    }

    // ---------------------------------------------------------------- bans at connect

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABanIssuedOnAnotherServerAfterTheLastCacheRefreshKicksAtConnect(bool multiServerMode)
    {
        var (h, _, db) = await With(multiServerMode: multiServerMode);
        using var _h = h;
        await using var _db = db;
        await h.Plugin.Plugin.CacheManager!.InitializeCacheAsync(h.Config, default); // the last refresh: no ban yet
        var session = h.Connect();
        await Net.Ban(db, session.SteamId, serverId: 999); // written on another server a moment later

        Assert.False(h.Plugin.Plugin.CacheManager!.CheckBan(h.Config, session.SteamId, null, Time.ActualDateTime()).IsBanned);
        await h.Load(session);

        Assert.Equal([session], h.Kicked);      // refused although the local cache could not know
        Assert.Empty(h.Applied);
        Assert.Equal(ConnectLoadState.Loaded, session.LoadState);
        Assert.Equal(1, session.LoadAttempts);
    }

    [Fact]
    public async Task ABanLiftedElsewhereIsNotKickedEvenIfTheCacheStillHoldsIt()
    {
        var (h, _, db) = await With();
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        var id = await Net.Ban(db, session.SteamId);
        await h.Plugin.Plugin.CacheManager!.InitializeCacheAsync(h.Config, default);
        await Net.Exec(db, "UPDATE sa_bans SET status = 'UNBANNED' WHERE id = @id", new { id });

        await h.Load(session);
        Assert.Empty(h.Kicked);
        Assert.Single(h.Applied);
    }

    [Fact]
    public async Task ASteamBanKicksOnceWhateverBanTypeAndIpSay()
    {
        foreach (var banType in new[] { 0, 1, 2 })
        {
            var (h, _, db) = await With(banType: banType);
            using var _h = h;
            await using var _db = db;
            var session = h.Connect(ip: "10.0.0.5");
            await Net.Ban(db, session.SteamId, ip: "1.2.3.4");
            await h.Load(session);
            Assert.Equal([session], h.Kicked);
        }
    }

    [Fact]
    public async Task ABanReadFailureLeavesTheConnectionUnverifiedNotCleanAndNotBanned()
    {
        var (h, provider, db) = await With();
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        provider.FailSteamBans = true;

        h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());
        await h.Settle(session);

        Assert.Equal(ConnectLoadState.RetryWait, session.LoadState); // retried, not "loaded without a ban"
        Assert.Empty(h.Applied);
        Assert.Empty(h.Kicked);
        Assert.False(CS2_SimpleAdmin.PlayersInfo.ContainsKey(session.SteamId));
        await using var c = await db.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sa_bans"));   // nothing is written for the outage
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sa_mutes"));

        provider.FailSteamBans = false;
        await Net.Ban(db, session.SteamId);
        h.Scheduled[^1].Callback();
        await h.Settle(session);
        Assert.Equal([session], h.Kicked);
    }

    // ---------------------------------------------------------------- IP write is independent of enforcement

    [Fact]
    public async Task AFailingIpWriteNeitherCancelsNorDelaysTheKickOrTheMutes()
    {
        var (h, provider, db) = await With();
        using var _h = h;
        await using var _db = db;
        provider.FailIpSave = true;

        var banned = h.Connect(slot: 1, steam: 76561198100100001, ip: "4.4.4.4");
        await Net.Ban(db, banned.SteamId);
        var muted = h.Connect(slot: 2, steam: 76561198100100002, ip: "4.4.4.5");
        await Net.Mute(db, muted.SteamId, "GAG", duration: 0, serverId: 5);
        await h.Load(banned);
        await h.Load(muted);

        Assert.Equal([banned], h.Kicked);
        Assert.Equal([muted], h.Applied);
        Assert.True(PlayerPenaltyManager.IsPenalized(muted.Slot, PenaltyType.Gag, out _));
        Assert.False(banned.IpSaved);
        Assert.False(muted.IpSaved);
    }

    [Fact]
    public async Task TheIpAddressIsStillSavedForTheSite()
    {
        var (h, _, db) = await With();
        using var _h = h;
        await using var _db = db;
        var clean = h.Connect(ip: "6.6.6.6");
        var banned = h.Connect(slot: 2, steam: 76561198100100002, ip: "6.6.6.7");
        await Net.Ban(db, banned.SteamId);
        await h.Load(clean);
        await h.Load(banned);

        await using var c = await db.OpenAsync();
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sa_players_ips"));  // banned players too
        Assert.True(clean.IpSaved && banned.IpSaved);
    }

    [Fact]
    public async Task ASlowIpWriteDoesNotHoldBackTheKick()
    {
        var (h, _, db) = await With(sqliteWorkers: false); // several workers, like MySQL
        using var _h = h;
        await using var _db = db;
        var session = h.Connect(ip: "8.8.4.4");
        await Net.Ban(db, session.SteamId);

        // Another process holds the write lock of the database file: the IP upsert has to wait, reads go on
        await using var blocker = (System.Data.Common.DbConnection)await db.OpenAsync();
        await blocker.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());
            await h.World.PumpUntil(() => h.Kicked.Count == 1, 8_000);
            Assert.False(session.IpSaved);     // still waiting for the lock
        }
        finally
        {
            await blocker.ExecuteAsync("ROLLBACK");
        }

        await h.Idle();
        Assert.True(session.IpSaved);          // and it completes when the lock is gone
    }

    // ---------------------------------------------------------------- mutes at connect

    [Fact]
    public async Task EveryActiveMuteOfAnyServerIsAppliedAtConnectWithItsRowId()
    {
        var (h, _, db) = await With();
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        var gag = await Net.Mute(db, session.SteamId, "GAG", duration: 0, serverId: null);
        var mute = await Net.Mute(db, session.SteamId, "MUTE", duration: 30, serverId: 0);
        var silence = await Net.Mute(db, session.SteamId, "SILENCE", duration: 30, serverId: 42);
        await Net.Mute(db, session.SteamId, "GAG", duration: 30, status: "UNMUTED");

        await h.Load(session);

        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Gag, out _));
        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Mute, out _));
        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Silence, out _));
        Assert.Single(PlayerPenaltyManager.GetPlayerPenalties(session.Slot, PenaltyType.Gag)); // the lifted one is not loaded
        Assert.True(gag > 0 && mute > 0 && silence > 0);
    }

    // ---------------------------------------------------------------- bounded verification

    [Fact]
    public async Task AnUnverifiedConnectionIsDisconnectedAtTheDeadlineAndNothingIsWritten()
    {
        var db = await TestDatabases.CreateAsync("SQLite");
        await using var _db = db;
        var outage = new OutageProvider();
        using var h = new Harness(outage);
        var session = h.Connect();
        h.Manager.ScheduleVerificationDeadline(session);
        var deadline = Assert.Single(h.Scheduled);
        Assert.InRange(deadline.Delay, TimeSpan.FromSeconds(44), TimeSpan.FromSeconds(45)); // what is left of the connection's budget

        h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());     // the database is down: attempt fails, retry is scheduled
        await h.Settle(session);
        Assert.Equal(ConnectLoadState.RetryWait, session.LoadState);
        Assert.Empty(h.KickedUnverified);                           // not before the deadline: the retries get their chance

        var before = PluginMetrics.UnverifiedKicked;
        deadline.Callback();
        Assert.Equal([session], h.KickedUnverified);
        Assert.Equal(before + 1, PluginMetrics.UnverifiedKicked);
        Assert.Contains("outage", session.LastLoadError ?? "");   // the diagnosis is kept for the log line
        await using var c = await db.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sa_bans"));
    }

    [Fact]
    public async Task TheDeadlineLeavesVerifiedGoneAndReplacedConnectionsAlone()
    {
        var (h, _, db) = await With();
        using var _h = h;
        await using var _db = db;

        var verified = h.Connect(slot: 1, steam: 76561198100100001);
        await h.Load(verified);
        PlayerManager.EnforceVerificationDeadline(verified);
        Assert.Empty(h.KickedUnverified);                          // loaded: verified

        var gone = h.Connect(slot: 2, steam: 76561198100100002);
        Runtime.Sessions.End(gone.Slot);
        PlayerManager.EnforceVerificationDeadline(gone);
        Assert.Empty(h.KickedUnverified);                          // disconnected before the deadline

        var oldOccupant = h.Connect(slot: 3, steam: 76561198100100003);
        var newOccupant = Runtime.Sessions.BeginOrGet(3, 76561198100100004, 777, "other", null, out _);
        PlayerManager.EnforceVerificationDeadline(oldOccupant);    // the timer of the former occupant of the slot
        Assert.Empty(h.KickedUnverified);
        PlayerManager.EnforceVerificationDeadline(newOccupant);    // the current one is still unverified
        Assert.Equal([newOccupant], h.KickedUnverified);
    }

    [Fact]
    public async Task AFullQueueAndAPluginThatIsNotReadyAreNotProofOfInnocenceEither()
    {
        var (h, _, db) = await With(dbCapacity: 1, state: PluginState.Starting);
        using var _h = h;
        await using var _db = db;
        var waiting = h.Connect(slot: 1, steam: 76561198100100001);

        // plugin not ready: LoadPlayerData and the maintenance pass start no load, so nothing is decided about the player
        Assert.Equal(ConnectLoadState.Pending, waiting.LoadState);
        Assert.Empty(h.Applied);
        PlayerManager.EnforceVerificationDeadline(waiting);
        Assert.Equal([waiting], h.KickedUnverified);

        Runtime.State = PluginState.Ready;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Runtime.TryQueueDb("blocker", async _ => { started.SetResult(); await release.Task; });
        await started.Task;
        Assert.True(Runtime.TryQueueDb("filler", _ => Task.CompletedTask));
        var blocked = h.Connect(slot: 2, steam: 76561198100100002);
        h.Manager.QueueLoad(blocked, Stopwatch.GetTimestamp());     // queue full
        Assert.Equal(ConnectLoadState.RetryWait, blocked.LoadState);
        PlayerManager.EnforceVerificationDeadline(blocked);
        Assert.Contains(blocked, h.KickedUnverified);
        release.SetResult();
    }

    [Fact]
    public async Task SteamIdZeroNeverStartsALoadAndTheTimeoutIsClamped()
    {
        var outage = new OutageProvider();
        using var h = new Harness(outage);
        var session = h.Connect(steam: 0);
        h.Manager.LoadPendingSessions();
        await Task.Delay(30);
        Assert.Equal(0, outage.Attempts);                           // no query was even tried for SteamID 0
        Assert.Equal(ConnectLoadState.Pending, session.LoadState);

        Assert.Equal(TimeSpan.FromSeconds(45), PlayerManager.VerificationTimeout(new CS2_SimpleAdminConfig()));
        Assert.Equal(TimeSpan.FromSeconds(10), PlayerManager.VerificationTimeout(TestConfig.Use(c => c.OtherSettings.UnverifiedConnectionTimeoutSeconds = 1)));
        Assert.Equal(TimeSpan.FromSeconds(600), PlayerManager.VerificationTimeout(TestConfig.Use(c => c.OtherSettings.UnverifiedConnectionTimeoutSeconds = 99999)));
    }

    // ---------------------------------------------------------------- online players: sync with the shared database

    private async Task Sync(Harness h, params PlayerSession[] sessions)
    {
        PeriodicMaintenance.QueueMuteSync(h.Config, sessions.ToList());
        await h.Idle();
    }

    [Fact]
    public async Task OnlinePlayersFollowAddExtendSecondRecordRemovalAndOverlap()
    {
        var (h, _, db) = await With();
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        await h.Load(session);
        Assert.False(PlayerPenaltyManager.IsSlotInPenalties(session.Slot));

        // issued on the site / another server meanwhile
        var mute = await Net.Mute(db, session.SteamId, "MUTE", duration: 30, serverId: 9);
        await Sync(h, session);
        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Mute, out var end1));
        Assert.Equal(true, h.Voice.LastOrDefault());

        // the same row extended: the entry follows (the old code skipped it because a mute was already present)
        await Net.Exec(db, "UPDATE sa_mutes SET duration = 600, ends = @ends WHERE id = @mute", new { ends = Time.ActualDateTime().AddMinutes(600), mute });
        await Sync(h, session);
        var entries = PlayerPenaltyManager.GetPlayerPenalties(session.Slot, PenaltyType.Mute);
        Assert.Single(entries);
        Assert.True(entries[0].EndDateTime > end1!.Value.AddMinutes(60));

        // a second, permanent record of another type; the mute row converted to permanent
        var gag = await Net.Mute(db, session.SteamId, "GAG", duration: 0, ends: Time.ActualDateTime().AddYears(-1), serverId: null);
        await Net.Exec(db, "UPDATE sa_mutes SET duration = 0 WHERE id = @mute", new { mute });
        await Sync(h, session);
        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Gag, out _));
        Assert.Equal(0, Assert.Single(PlayerPenaltyManager.GetPlayerPenalties(session.Slot, PenaltyType.Mute)).Duration);

        // a SILENCE as well, then the MUTE is lifted on another server: voice stays restricted by the SILENCE
        var silence = await Net.Mute(db, session.SteamId, "SILENCE", duration: 0, serverId: 3);
        await Net.Exec(db, "UPDATE sa_mutes SET status = 'UNMUTED' WHERE id = @mute", new { mute });
        h.Voice.Clear();
        await Sync(h, session);
        Assert.False(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Mute, out _));
        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Silence, out _));
        Assert.DoesNotContain(false, h.Voice);                      // the voice was never released
        Assert.True(h.Voice.Count > 0 && h.Voice[^1]);

        // everything lifted (a permanent gag stayed until now): all entries go, voice is released once
        await Net.Exec(db, "UPDATE sa_mutes SET status = 'UNMUTED' WHERE id IN (@gag, @silence)", new { gag, silence });
        h.Voice.Clear();
        await Sync(h, session);
        Assert.False(PlayerPenaltyManager.IsSlotInPenalties(session.Slot));
        Assert.Equal([false], h.Voice);
    }

    [Fact]
    public async Task AFailedSyncReadKeepsTheKnownRestrictions()
    {
        var (h, provider, db) = await With();
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        await Net.Mute(db, session.SteamId, "GAG", duration: 0);
        await h.Load(session);
        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Gag, out _));

        provider.FailMutes = true;
        await Sync(h, session);
        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Gag, out _)); // an error is not "nothing active"

        provider.FailMutes = false;
        await Net.Exec(db, "UPDATE sa_mutes SET status = 'UNMUTED'");
        await Sync(h, session);
        Assert.False(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Gag, out _)); // a successful empty answer is
    }

    [Fact]
    public async Task ASyncResultForAReplacedSlotIsNotAppliedToTheNewPlayer()
    {
        var (h, _, db) = await With();
        using var _h = h;
        await using var _db = db;
        var old = h.Connect(steam: 76561198100100001);
        await h.Load(old);
        await Net.Mute(db, old.SteamId, "GAG", duration: 0);

        PeriodicMaintenance.QueueMuteSync(h.Config, [old]);
        Runtime.Sessions.End(old.Slot);                                                // the player leaves while the read is queued
        var newcomer = Runtime.Sessions.BeginOrGet(old.Slot, 76561198100100009, 555, "new", null, out _);
        await h.Idle();

        Assert.False(PlayerPenaltyManager.IsPenalized(newcomer.Slot, PenaltyType.Gag, out _));
        Assert.True(PluginMetrics.StaleSessionResults > 0);
    }

    [Fact]
    public async Task ARestrictionIssuedByACommandAfterTheReadIsNotErasedByTheOlderAnswer()
    {
        var (h, _, db) = await With();
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        await h.Load(session);

        var readRevision = PlayerPenaltyManager.NextRevision();                         // the sync read is accepted here ...
        PlayerPenaltyManager.AddPenalty(session.Slot, PenaltyType.Gag, Time.ActualDateTime().AddMinutes(5), 5, 0); // ... then css_gag is accepted
        PeriodicMaintenance.ApplySyncResult(session, readRevision, []);                 // the older answer: nothing active

        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Gag, out _));
        // and a later read, which does see the committed row, replaces the optimistic entry by it (no duplicates)
        var gag = await Net.Mute(db, session.SteamId, "GAG", duration: 5);
        await Sync(h, session);
        Assert.Single(PlayerPenaltyManager.GetPlayerPenalties(session.Slot, PenaltyType.Gag));
        Assert.True(gag > 0);
    }

    [Fact]
    public void PermanentEntriesDoNotExpireBecauseOfTheirEndTime()
    {
        PlayerPenaltyManager.RemoveAllPenalties(7);
        PlayerPenaltyManager.AddPenalty(7, PenaltyType.Gag, DateTime.MinValue, 0, 0, 1);
        PlayerPenaltyManager.AddPenalty(7, PenaltyType.Mute, Time.ActualDateTime().AddYears(-5), 0, 0, 2);
        PlayerPenaltyManager.RemoveExpiredPenalties(1, Time.ActualDateTime());
        PlayerPenaltyManager.RemoveExpiredPenalties(0, default);
        Assert.True(PlayerPenaltyManager.IsPenalized(7, PenaltyType.Gag, out _));
        Assert.True(PlayerPenaltyManager.IsPenalized(7, PenaltyType.Mute, out _));
        PlayerPenaltyManager.RemoveAllPenalties(7);
    }

    [Fact]
    public void ReconcileKeepsExternalAndNewerEntriesAndDoesNotDuplicate()
    {
        const int slot = 8;
        PlayerPenaltyManager.RemoveAllPenalties(slot);
        var later = Time.ActualDateTime().AddHours(1);
        PlayerPenaltyManager.AddPenalty(slot, PenaltyType.Mute, later, 60);                       // another plugin's entry: external
        PlayerPenaltyManager.AddPenalty(slot, PenaltyType.Mute, later, 60, 100, 5);               // database row 5, read at revision 100
        PlayerPenaltyManager.AddPenalty(slot, PenaltyType.Mute, later, 60, 300, 0);               // a command accepted at revision 300

        var rows = new[] { new PlayerPenaltyManager.DbPenalty(5, PenaltyType.Mute, later, 60), new PlayerPenaltyManager.DbPenalty(6, PenaltyType.Mute, later, 60) };
        PlayerPenaltyManager.ReconcileWithDatabase(slot, 200, rows);

        Assert.Equal(4, PlayerPenaltyManager.GetPlayerPenalties(slot, PenaltyType.Mute).Count); // external + #300 + rows 5 and 6
        PlayerPenaltyManager.ReconcileWithDatabase(slot, 200, rows);                              // idempotent
        Assert.Equal(4, PlayerPenaltyManager.GetPlayerPenalties(slot, PenaltyType.Mute).Count);
        PlayerPenaltyManager.ReconcileWithDatabase(slot, 400, []);                                // later empty answer: only the external one stays
        Assert.Single(PlayerPenaltyManager.GetPlayerPenalties(slot, PenaltyType.Mute));
        PlayerPenaltyManager.RemoveAllPenalties(slot);
    }
}
