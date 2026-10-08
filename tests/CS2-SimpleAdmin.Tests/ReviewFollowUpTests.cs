using System.Diagnostics;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdminApi;
using Dapper;
using Xunit.Abstractions;
using static CS2_SimpleAdmin.Tests.ConnectEnforcementTests;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// Follow-up to the network-wide penalty work: TimeMode 0 ordering of sync and online-time expiry, DB-authoritative
/// periodic ban enforcement (IP addresses never ban), refusal semantics, the verification deadline of a SteamID 0
/// connection, the unverified-connection text gate, and the vote delivery. Real queues, dispatcher, database and production
/// code; only engine-facing seams are recorded. These are pure-logic tests, not a native two-server CS2 test.
/// </summary>
public class ReviewFollowUpTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- TimeMode 0: sync vs. online-time expiry

    private static OnlineCredit OneMinute(ulong steam)
    {
        var start = DateTime.Now.AddMinutes(-1);
        return new OnlineCredit(steam, 1, OnlineCredit.TicksPerMinute, start, start.AddMinutes(1));
    }

    private static async Task<long> TimedMute(TestDatabase db, ulong steam, string type, int duration, int passed) =>
        await Net.Mute(db, steam, type, duration: duration, ends: DateTime.Now.AddMinutes(duration), passed: passed, created: DateTime.Now.AddHours(-3));

    /// <summary>A hook that holds the first call at a barrier until released; later calls pass.</summary>
    private sealed class Barrier
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hits;
        public Task Reached => _reached.Task;
        public void Release() => _release.TrySetResult();

        public Task Hit()
        {
            if (Interlocked.Increment(ref _hits) != 1) return Task.CompletedTask;
            _reached.SetResult();
            return _release.Task;
        }
    }

    [Fact]
    public async Task AnOlderSyncCannotBringBackAMuteThatTheOnlineCreditJustUsedUp()
    {
        var (h, _, db) = await With(timeMode: 0);
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        await TimedMute(db, session.SteamId, "MUTE", duration: 10, passed: 9);
        await h.Load(session);
        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Mute, out _));

        var barrier = new Barrier();
        PeriodicMaintenance.SyncHook = (_, _) => barrier.Hit();
        try
        {
            PeriodicMaintenance.QueueMuteSync(h.Config, [session]);      // S1 reads the row while it is still running ...
            await barrier.Reached;

            // ... then the credit uses it up and the expiry result arrives (old code: Passed=true by id on the game thread)
            var credit = new PeriodicMaintenance.SessionCredit(session, OneMinute(session.SteamId));
            var expiry = PeriodicMaintenance.ApplyOnlineTimeAsync(h.Plugin.Plugin, h.Config, [credit], default);
            await h.World.World.PumpWhile(expiry);

            barrier.Release();                                             // ... and only now S1 applies its stale answer
            await h.Idle();

            Assert.False(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Mute, out _));   // the expired mute stays gone
            Assert.False(PlayerPenaltyManager.IsSlotInPenalties(session.Slot));
            Assert.Equal(false, h.Voice.LastOrDefault());                                             // and the voice was released at once
        }
        finally
        {
            PeriodicMaintenance.SyncHook = null;
        }
    }

    [Fact]
    public async Task AnExpiryReadOfAnOldVersionCannotLiftAGagThatWasExtendedUnderTheSameId()
    {
        var (h, _, db) = await With(timeMode: 0);
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        var id = await TimedMute(db, session.SteamId, "GAG", duration: 10, passed: 9);
        await h.Load(session);

        var barrier = new Barrier();
        h.Plugin.Plugin.MuteManager.FaultHook = point => point == "after-select" ? barrier.Hit() : Task.CompletedTask;
        var credit = new PeriodicMaintenance.SessionCredit(session, OneMinute(session.SteamId));
        var expiry = PeriodicMaintenance.ApplyOnlineTimeAsync(h.Plugin.Plugin, h.Config, [credit], default);
        await barrier.Reached;                   // the pass read "row used up" (passed 10 of 10) ...

        await Net.Exec(db, "UPDATE sa_mutes SET duration = 100, ends = @ends WHERE id = @id",
            new { ends = DateTime.Now.AddMinutes(100), id });                // ... the site extends the same row ...
        PeriodicMaintenance.QueueMuteSync(h.Config, [session]);              // ... a sync applies the new version ...
        await h.Idle();
        Assert.Equal(100, Assert.Single(PlayerPenaltyManager.GetPlayerPenalties(session.Slot, PenaltyType.Gag)).Duration);

        barrier.Release();                       // ... and only now the old expiry result arrives (old code: Passed=true by id)
        await h.World.World.PumpWhile(expiry);
        await h.Idle();

        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Gag, out _));
        var entry = Assert.Single(PlayerPenaltyManager.GetPlayerPenalties(session.Slot, PenaltyType.Gag));
        Assert.Equal(100, entry.Duration);
        Assert.False(entry.Passed);
        Assert.Equal(id, id);
    }

    [Fact]
    public async Task ExpiryOfOneVoiceRestrictionKeepsTheOverlappingOneAndOnlyTheMutedBitFollows()
    {
        var (h, _, db) = await With(timeMode: 0);
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        await TimedMute(db, session.SteamId, "MUTE", duration: 10, passed: 9);
        await Net.Mute(db, session.SteamId, "SILENCE", duration: 0);          // permanent, overlapping
        await TimedMute(db, session.SteamId, "GAG", duration: 10, passed: 9);
        await h.Load(session);
        h.Voice.Clear();

        var credit = new PeriodicMaintenance.SessionCredit(session, OneMinute(session.SteamId));
        await h.World.World.PumpWhile(PeriodicMaintenance.ApplyOnlineTimeAsync(h.Plugin.Plugin, h.Config, [credit], default));
        await h.Idle();

        Assert.False(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Mute, out _));
        Assert.False(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Gag, out _));
        Assert.True(PlayerPenaltyManager.IsPenalized(session.Slot, PenaltyType.Silence, out _));
        Assert.DoesNotContain(false, h.Voice);                      // the SILENCE still holds the voice: never released
        Assert.True(h.Voice.Count > 0 && h.Voice[^1]);
    }

    [Fact]
    public async Task AnExpiryResultForAReplacedConnectionOrALiftedRowChangesNothingElse()
    {
        var (h, _, db) = await With(timeMode: 0);
        using var _h = h;
        await using var _db = db;
        var old = h.Connect(steam: 76561198100100001);
        var id = await TimedMute(db, old.SteamId, "MUTE", duration: 10, passed: 9);
        await h.Load(old);

        var credit = new PeriodicMaintenance.SessionCredit(old, OneMinute(old.SteamId));
        var barrier = new Barrier();
        h.Plugin.Plugin.MuteManager.FaultHook = point => point == "after-select" ? barrier.Hit() : Task.CompletedTask;
        var expiry = PeriodicMaintenance.ApplyOnlineTimeAsync(h.Plugin.Plugin, h.Config, [credit], default);
        await barrier.Reached;
        Runtime.Sessions.End(old.Slot);                                                                  // the player leaves ...
        var newcomer = Runtime.Sessions.BeginOrGet(old.Slot, 76561198100100009, 555, "new", null, out _); // ... another one takes the slot
        PlayerPenaltyManager.RemoveAllPenalties(old.Slot);
        barrier.Release();
        await h.World.World.PumpWhile(expiry);
        await h.Idle();

        Assert.False(PlayerPenaltyManager.IsSlotInPenalties(newcomer.Slot));
        Assert.Equal("ACTIVE", await ScalarAsync(db, "SELECT status FROM sa_mutes WHERE id = @id", id));   // nothing was written for it
    }

    private static async Task<string> ScalarAsync(TestDatabase db, string sql, long id)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<string>(sql, new { id }) ?? "";
    }

    // ---------------------------------------------------------------- online ban enforcement: the database, by SteamID

    private sealed class KickRecorder : IDisposable
    {
        public readonly List<PlayerSession> Kicked = [];
        public KickRecorder() => PeriodicMaintenance.KickBannedOnline = s => Kicked.Add(s);
        public void Dispose() => PeriodicMaintenance.KickBannedOnline = static _ => { };
    }

    [Fact]
    public async Task OnlinePlayersAreKickedOnlyForABanOfTheirOwnSteamIdRegardlessOfIpCacheOrBanType()
    {
        var (h, _, db) = await With(banType: 1);
        using var _h = h;
        await using var _db = db;
        using var kicks = new KickRecorder();

        var banned = h.Connect(slot: 1, steam: 76561198100100001, ip: "5.5.5.5");
        var sameIp = h.Connect(slot: 2, steam: 76561198100100002, ip: "5.5.5.5");        // shares the banned player's address
        var freshBan = h.Connect(slot: 3, steam: 76561198100100003, ip: "6.6.6.6");     // banned after the last cache refresh
        var lifted = h.Connect(slot: 4, steam: 76561198100100004, ip: "7.7.7.7");
        var elapsed = h.Connect(slot: 5, steam: 76561198100100005, ip: "8.8.8.8");
        var clean = h.Connect(slot: 6, steam: 76561198100100006, ip: "5.5.5.5");

        await Net.Ban(db, banned.SteamId, ip: "5.5.5.5");
        var cache = h.Plugin.Plugin.CacheManager!;
        await cache.InitializeCacheAsync(h.Config, default);                              // the cache knows only this one
        await Net.Ban(db, freshBan.SteamId, serverId: 77);
        var liftedId = await Net.Ban(db, lifted.SteamId);
        await Net.Exec(db, "UPDATE sa_bans SET status = 'UNBANNED' WHERE id = @liftedId", new { liftedId });
        await Net.Ban(db, elapsed.SteamId, duration: 5, ends: Time.ActualDateTime().AddMinutes(-1));
        // the cached ban row of `banned` moves to another address and another account entirely
        await Net.Exec(db, "UPDATE sa_bans SET player_ip = '9.9.9.9' WHERE player_steamid = @s", new { s = (long)banned.SteamId });

        await h.World.World.PumpWhile(PeriodicMaintenance.EnforceBansAsync(db.Provider, [banned, sameIp, freshBan, lifted, elapsed, clean], default));
        await h.World.PumpUntil(() => kicks.Kicked.Count >= 2);
        await h.Idle();

        Assert.Equal(new[] { banned.Slot, freshBan.Slot }, kicks.Kicked.Select(s => s.Slot).Order());
    }

    [Fact]
    public async Task AnUnreadableBanTableNeverKicksAndEveryoneIsCheckedAgainNextPass()
    {
        var (h, provider, db) = await With();
        using var _h = h;
        await using var _db = db;
        using var kicks = new KickRecorder();
        var banned = h.Connect(slot: 1, steam: 76561198100100001);
        await Net.Ban(db, banned.SteamId);

        await h.World.World.PumpWhile(PeriodicMaintenance.EnforceBansAsync(new OutageProvider(), [banned], default));
        await h.Idle();
        Assert.Empty(kicks.Kicked);

        await h.World.World.PumpWhile(PeriodicMaintenance.EnforceBansAsync(provider, [banned], default));
        await h.World.PumpUntil(() => kicks.Kicked.Count == 1);
        Assert.Equal([banned], kicks.Kicked);
    }

    // ---------------------------------------------------------------- refusal: nothing changes

    [Fact]
    public async Task ARefusedWriteLeavesNoPenaltyNoVoiceChangeAndNoDatabaseRow()
    {
        using var world = new TestWorld(dbCapacity: 1);
        TestConfig.Use(_ => { });
        await using var db = await TestDatabases.CreateAsync("SQLite");
        var writes = 0;
        var voice = 0;
        const int slot = 9;
        const ulong steam = 76561198100100099;

        bool Queue() => CS2_SimpleAdmin.TryQueuePenaltyWork(CallerRef.Console, null, "mute-write", async _ =>
        {
            Interlocked.Increment(ref writes);
            await Net.Mute(db, steam, "MUTE");
        }, orderKey: steam);

        // the plugin is not ready for server-scoped writes
        Runtime.State = PluginState.Starting;
        foreach (var type in new[] { PenaltyType.Gag, PenaltyType.Mute, PenaltyType.Silence })
            Assert.False(LocalPenalty.Issue(Queue, type, slot, 30, type == PenaltyType.Gag ? null : () => voice++));
        Assert.False(PlayerPenaltyManager.IsSlotInPenalties(slot));
        Assert.Equal(0, voice);

        // the queue is full
        Runtime.State = PluginState.Ready;
        CS2_SimpleAdmin.GlobalServerId = 1;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Runtime.TryQueueDb("blocker", async _ => { started.SetResult(); await release.Task; }));
        await started.Task;
        Assert.True(Runtime.TryQueueDb("filler", _ => Task.CompletedTask));
        foreach (var type in new[] { PenaltyType.Gag, PenaltyType.Mute, PenaltyType.Silence })
            Assert.False(LocalPenalty.Issue(Queue, type, slot, 30, type == PenaltyType.Gag ? null : () => voice++));
        Assert.False(PlayerPenaltyManager.IsSlotInPenalties(slot));
        Assert.Equal(0, voice);
        Assert.Equal(0, writes);

        release.SetResult();
        await TestWorld.WaitUntil(() => Runtime.Db!.Pending == 0);
        await using var c = await db.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sa_mutes"));
        Assert.Equal(0, writes);

        // accepted work: the restriction and the voice flag appear, once per call
        Assert.True(LocalPenalty.Issue(Queue, PenaltyType.Mute, slot, 30, () => voice++));
        Assert.True(PlayerPenaltyManager.IsPenalized(slot, PenaltyType.Mute, out _));
        Assert.Equal(1, voice);
        await TestWorld.WaitUntil(() => writes == 1);
        PlayerPenaltyManager.RemoveAllPenalties(slot);
    }

    // ---------------------------------------------------------------- SteamID 0 and the verification deadline

    [Fact]
    public async Task OneDeadlineBudgetCoversASteamIdZeroConnectionAndItsLaterAuthorization()
    {
        var (h, _, db) = await With();
        using var _h = h;
        await using var _db = db;

        var zero = h.Connect(slot: 3, steam: 0);                           // connected, authorization not finished
        h.Manager.ArmVerificationDeadline(zero);
        var deadline = Assert.Single(h.Scheduled);
        Assert.InRange(deadline.Delay, TimeSpan.FromSeconds(44), TimeSpan.FromSeconds(45));
        h.Manager.LoadPendingSessions();
        Assert.Equal(ConnectLoadState.Pending, zero.LoadState);            // nothing is loaded, nothing is "clean"

        // the SteamID arrives: a session of the same connection inherits the start and does not restart the budget
        var authorized = Runtime.Sessions.BeginOrGet(3, 76561198100100003, zero.UserId, "probe", null, out var created);
        Assert.True(created);
        Assert.True(authorized.DeadlineArmed);
        Assert.Equal(zero.ConnectedTimestamp, authorized.ConnectedTimestamp);
        h.Manager.ArmVerificationDeadline(authorized);
        h.Manager.ArmVerificationDeadline(authorized);
        Assert.Single(h.Scheduled);                                        // events and repeats arm nothing new

        // the original timer fires while the authorized session is still unverified (slow database): that connection goes
        await Net.Ban(db, authorized.SteamId);
        deadline.Callback();
        Assert.Equal([authorized], h.KickedUnverified);
        await using var c = await db.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sa_bans"));                  // only the ban inserted above
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sa_mutes"));
    }

    [Fact]
    public async Task ASteamIdZeroConnectionThatNeverAuthorizesIsDisconnectedAndALoadedOneIsNot()
    {
        var (h, _, db) = await With();
        using var _h = h;
        await using var _db = db;

        var never = h.Connect(slot: 1, steam: 0);
        h.Manager.ArmVerificationDeadline(never);
        h.Scheduled[0].Callback();
        Assert.Equal([never], h.KickedUnverified);                         // no authorized / full-connect event ever came

        var late = h.Connect(slot: 2, steam: 0);
        h.Manager.ArmVerificationDeadline(late);
        var timer = h.Scheduled[^1];
        var authorized = Runtime.Sessions.BeginOrGet(2, 76561198100100002, late.UserId, "probe", null, out _);
        await h.Load(authorized);                                          // authorized in time and verified
        timer.Callback();
        Assert.DoesNotContain(authorized, h.KickedUnverified);
    }

    [Fact]
    public void AReplacedSlotIsNotKickedByTheTimerOfItsFormerOccupant()
    {
        using var world = new TestWorld();
        TestConfig.Use(_ => { });
        var kicked = new List<PlayerSession>();
        PlayerManager.KickUnverified = (s, _) => kicked.Add(s);
        try
        {
            var first = Runtime.Sessions.BeginOrGet(5, 0, 105, "a", null, out _);
            Runtime.Sessions.End(5);
            var other = Runtime.Sessions.BeginOrGet(5, 76561198100100005, 777, "b", null, out _);
            PlayerManager.EnforceVerificationDeadline(first);              // timer of a connection that is gone
            Assert.Empty(kicked);
            Assert.NotNull(other);
        }
        finally
        {
            PlayerManager.KickUnverified = static (_, _) => { };
        }
    }

    // ---------------------------------------------------------------- unverified connections (hot reload, reconnect, slot reuse)

    [Fact]
    public async Task ATextIsNotPublishedBeforeTheDatabaseAnsweredAndAGagThenApplies()
    {
        var (h, provider, db) = await With();
        using var _h = h;
        await using var _db = db;
        var session = h.Connect();
        await Net.Mute(db, session.SteamId, "GAG", duration: 0, serverId: 4);

        // a freshly adopted connection (hot reload, reconnect): nothing is known yet
        Assert.Equal(ChatRestriction.Unverified, ChatGuard.Evaluate(session.Slot, session.SteamId, session.UserId, out _));

        // slow / failing database: still unverified, the load is retried; no penalty is invented, no text allowed
        provider.FailMutes = true;
        h.Manager.QueueLoad(session, Stopwatch.GetTimestamp());
        await h.Settle(session);
        Assert.Equal(ConnectLoadState.RetryWait, session.LoadState);
        Assert.Equal(ChatRestriction.Unverified, ChatGuard.Evaluate(session.Slot, session.SteamId, session.UserId, out _));
        var line = ChatGate.Decide("say", "hello", ChatRestriction.Unverified, ["!"], null);
        Assert.Equal(ChatVerdict.BlockUnverified, line.Verdict);

        provider.FailMutes = false;
        h.Scheduled[^1].Callback();
        await h.Settle(session);
        await h.Idle();
        Assert.Equal(ChatRestriction.Gagged, ChatGuard.Evaluate(session.Slot, session.SteamId, session.UserId, out _));
    }

    [Fact]
    public async Task ASlotReusedByAnotherConnectionNeverInheritsTheFormerOccupantsRestrictions()
    {
        var (h, _, db) = await With();
        using var _h = h;
        await using var _db = db;
        var gagged = h.Connect(slot: 7, steam: 76561198100100007);
        await Net.Mute(db, gagged.SteamId, "GAG", duration: 0);
        await Net.Mute(db, gagged.SteamId, "SILENCE", duration: 0);
        await h.Load(gagged);
        Assert.Equal(ChatRestriction.Gagged, ChatGuard.Evaluate(7, gagged.SteamId, gagged.UserId, out _));

        // the disconnect event was missed: the entries of the old occupant are still in the slot
        Runtime.Sessions.End(7);
        var next = Runtime.Sessions.BeginOrGet(7, 76561198100100008, 808, "next", null, out _);
        Assert.Equal(ChatRestriction.Gagged, ChatGuard.Evaluate(7, next.SteamId, next.UserId, out _) );  // stale slot state, not yet replaced
        await h.Load(next);                                                                         // the new connection's own, authoritative read

        Assert.False(PlayerPenaltyManager.IsSlotInPenalties(7));
        Assert.Equal(ChatRestriction.None, ChatGuard.Evaluate(7, next.SteamId, next.UserId, out _));
    }

    // ---------------------------------------------------------------- vote delivery

    private sealed record FakePlayer(int Id, string Language);

    [Fact]
    public void AVoiceVoteMakesOneCenterCallPerRecipientEachInTheRecipientsLanguage()
    {
        foreach (var n in new[] { 1, 16, 64, 128 })
        {
            var players = Enumerable.Range(0, n).Select(i => new FakePlayer(i, i % 3 == 0 ? "ru" : "en")).ToList();
            var center = new List<(FakePlayer, string)>();
            var chat = new List<FakePlayer>();
            var menu = new List<FakePlayer>();
            var localizations = 0;

            VoteDelivery.Deliver(players, new VoteDelivery.Sink<FakePlayer>
            {
                Localize = (p, _, caller, question) => { localizations++; return $"{p.Language}:{caller}:{question}"; },
                Center = (p, text) => center.Add((p, text)),
                Chat = chat.Add,
                OpenMenu = menu.Add
            }, null!, "Admin", "Kick?");

            Assert.Equal(n, center.Count);                        // N, not N²
            Assert.Equal(n, localizations);
            Assert.Equal(players, center.Select(c => c.Item1));
            Assert.All(center, c => Assert.Equal($"{c.Item1.Language}:Admin:Kick?", c.Item2));
            Assert.Equal(players, chat);
            Assert.Equal(players, menu);
        }
    }

    // ---------------------------------------------------------------- game-thread cost (measured, not promised)

    [Fact]
    public async Task GameThreadWorkPerMessageAndPerPassIsSmall()
    {
        var (h, _, db) = await With(timeMode: 0);
        using var _h = h;
        await using var _db = db;
        TestHelpersRegistry.Use();

        // per chat line: restriction check + decision for a gagged player (the heaviest path)
        const int slot = 11;
        var session = Runtime.Sessions.BeginOrGet(slot, 76561198100100011, 111, "p", null, out _);
        session.TryBeginLoad(1);
        session.CompleteLoad(1);
        PlayerPenaltyManager.AddPenalty(slot, PenaltyType.Gag, Time.ActualDateTime().AddMinutes(30), 30, 0);
        string[] triggers = ["!", "/"];
        for (var i = 0; i < 1_000; i++) Line();
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        const int lines = 100_000;
        for (var i = 0; i < lines; i++) Line();
        sw.Stop();
        var perLine = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        output.WriteLine($"chat line (gagged, plain text): {sw.Elapsed.TotalMilliseconds * 1000 / lines:F3} us, {perLine / (double)lines:F1} B allocated");
        Assert.True(sw.Elapsed.TotalMilliseconds * 1000 / lines < 50);

        void Line()
        {
            var r = ChatGuard.Evaluate(slot, 76561198100100011, 111, out _);
            ChatGate.Decide("say", "hello world", r, triggers, (IEnumerable<string>?)null);
        }

        // per pass, game-thread part of the follow-up resync for 128 players (one dispatcher item)
        var sessions = new List<PlayerSession>();
        for (var i = 0; i < 128; i++)
        {
            var s = Runtime.Sessions.BeginOrGet(i, 76561198100200000UL + (ulong)i, 1000 + i, "p" + i, null, out _);
            s.TryBeginLoad(1);
            s.CompleteLoad(1);
            sessions.Add(s);
        }

        var queued = Stopwatch.StartNew();
        PeriodicMaintenance.QueueMuteSync(h.Config, sessions);
        queued.Stop();
        output.WriteLine($"QueueMuteSync for 128 sessions (game thread part): {queued.Elapsed.TotalMilliseconds:F3} ms");
        Assert.True(queued.Elapsed.TotalMilliseconds < 100);
        await h.Idle();
    }
}

internal static class TestHelpersRegistry
{
    public static void Use() => GagTriggerBypassTests.Registry.Use();
}
