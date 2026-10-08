using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdminApi;

namespace CS2_SimpleAdmin.Managers;

/// <summary>
/// The 61-second maintenance pass.
/// <list type="bullet">
/// <item>Single-flight: a tick that finds the previous pass still running (slow DB) is skipped and counted, so
/// passes never overlap and never pile up.</item>
/// <item>Dependent steps are ordered: SQL ban expiry runs before the cache refresh, so the refresh does not
/// re-read a ban as ACTIVE that is already past its end (cached bans also carry ends/duration and are checked
/// against the clock, which closes the remaining window).</item>
/// <item>The game-thread part only snapshots sessions and applies results; all SQL runs on one DB worker.</item>
/// <item>TimeMode 0: each session accrues real online time; whole minutes are credited to its mutes set-based as an
/// <see cref="OnlineCredit"/>, an idempotent compare-and-set plan that is retried unchanged until it is applied and
/// only then folded into the session's checkpoint (no double counting after partial failures, lost commits or
/// retries; a new mute receives only the time after its own creation).</item>
/// </list>
/// </summary>
internal static class PeriodicMaintenance
{
    private static int _running;
    private static readonly long TicksPerMinute = Stopwatch.Frequency * 60;

    internal readonly record struct SessionCredit(PlayerSession Session, OnlineCredit Credit);

    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    public static void Reset() => Volatile.Write(ref _running, 0);

    /// <summary>Game thread (timer).</summary>
    public static void OnTimer()
    {
        var start = LatencyHistogram.Now();
        try
        {
            RunGameThreadPart();
        }
        finally
        {
            PluginMetrics.PeriodicTimer.RecordSince(start);
        }
    }

    private static void RunGameThreadPart()
    {
        var plugin = CS2_SimpleAdmin.Instance;
        if (CS2_SimpleAdmin.DatabaseProvider == null)
            return;

        // In-memory penalty upkeep (unchanged behaviour): reset voice of players whose mute ended, drop expired entries
        try
        {
            foreach (var player in Helper.GetValidPlayers())
            {
                if (!PlayerPenaltyManager.IsSlotInPenalties(player.Slot))
                    continue;

                var isMuted = PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Mute, out _);
                var isSilenced = PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence, out _);
                if (!isMuted && !isSilenced)
                    player.VoiceFlags = PenaltyRemoval.WithoutMuted(player.VoiceFlags); // only the penalty's own bit; other voice flags are not its business
            }

            PlayerPenaltyManager.RemoveExpiredPenalties();
        }
        catch (Exception ex)
        {
            RateLimitedLog.Error("periodic.penalties", ex, "Unable to remove old penalties");
        }

        if (Runtime.State != PluginState.Ready) return;

        // Connect loads rejected earlier by a full queue get another chance here
        CS2_SimpleAdmin.PlayerManager.LoadPendingSessions();

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            Interlocked.Increment(ref PluginMetrics.PeriodicSkippedOverlap);
            RateLimitedLog.Warning("periodic.overlap", "Previous maintenance pass still running (slow database?); skipping this one");
            return;
        }

        var config = plugin.Config;
        var sessions = new List<PlayerSession>();
        Runtime.Sessions.Snapshot(sessions);
        var credits = config.OtherSettings.TimeMode == 0
            ? ComputeCredits(sessions, Stopwatch.GetTimestamp(), Time.ActualDateTime())
            : [];

        // Mutes issued meanwhile on the site or another server (and changes/removals of existing ones) reach the players who
        // are online here. Queued from the game thread, in each player's own ordered lane: see QueueMuteSync.
        QueueMuteSync(config, sessions);

        if (!Runtime.TryQueueDb("periodic", ct => RunAsync(plugin, config, sessions, credits, ct)))
            Volatile.Write(ref _running, 0);
    }

    /// <summary>
    /// Game thread. The credit each session should send this pass: its outstanding (unfinished) credit if it has one,
    /// otherwise a new one for the whole minutes accrued since its checkpoint. A session never has two credits at
    /// once, so windows cannot overlap or repeat.
    /// </summary>
    internal static List<SessionCredit> ComputeCredits(List<PlayerSession> sessions, long nowTimestamp, DateTime now)
    {
        var credits = new List<SessionCredit>(sessions.Count);
        foreach (var session in sessions)
        {
            if (session.PendingCredit is { } outstanding)
            {
                credits.Add(new SessionCredit(session, outstanding));
                continue;
            }

            var startTicks = session.StartedTimestamp + session.CreditedTicks;
            var minutes = (int)((nowTimestamp - startTicks) / OnlineCredit.TicksPerMinute);
            if (minutes <= 0) continue;

            var endTicks = startTicks + minutes * OnlineCredit.TicksPerMinute;
            var windowStart = now - TimeSpan.FromSeconds((double)(nowTimestamp - startTicks) / Stopwatch.Frequency);
            var windowEnd = now - TimeSpan.FromSeconds((double)(nowTimestamp - endTicks) / Stopwatch.Frequency);
            var credit = new OnlineCredit(session.SteamId, minutes, minutes * OnlineCredit.TicksPerMinute, windowStart, windowEnd);
            session.PendingCredit = credit;
            credits.Add(new SessionCredit(session, credit));
        }

        return credits;
    }

    /// <summary>Test seam: kick of a banned online connection (game thread). Production: <see cref="Helper.KickPlayer"/>.</summary>
    internal static Action<PlayerSession> KickBannedOnline { get; set; } = static session =>
    {
        if (PlayerManager.ResolveController(session) is { } player)
            Helper.KickPlayer(player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED);
        else
            Interlocked.Increment(ref PluginMetrics.StaleSessionResults);
    };

    /// <summary>
    /// Online players with an active ban of their own SteamID64 in the shared database are disconnected: one bounded batch
    /// read per <see cref="BanDecider.BatchSize"/> players, then one game-thread item that kicks the exact connections
    /// (slot + SteamID + userid + session still current). A failed read is logged and retried by the next pass; it never kicks.
    /// </summary>
    internal static async Task EnforceBansAsync(Database.IDatabaseProvider provider, List<PlayerSession> sessions, CancellationToken ct)
    {
        HashSet<ulong> banned;
        try
        {
            banned = await BanDecider.FindBannedAsync(provider, sessions.Select(s => s.SteamId).ToList(), Time.ActualDateTime(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RateLimitedLog.Error("periodic.ban-check", ex, "Unable to check online players against the ban table");
            return;
        }

        if (banned.Count == 0) return;
        var targets = sessions.Where(s => banned.Contains(s.SteamId)).ToList();
        await Runtime.OnGameThread(() =>
        {
            foreach (var session in targets) KickBannedOnline(session);
        }).ConfigureAwait(false);
    }

    private static async Task RunAsync(CS2_SimpleAdmin plugin, CS2_SimpleAdminConfig config,
        List<PlayerSession> sessions, List<SessionCredit> credits, CancellationToken ct)
    {
        var start = LatencyHistogram.Now();
        try
        {
            // TimeMode 0 first: its result (mutes used up) must be reported before the mute-expiry step below can mark
            // them EXPIRED in SQL, and if it failed that step is skipped so no used-up mute escapes the report.
            var onlineOk = true;
            if (config.OtherSettings.TimeMode == 0 && credits.Count > 0)
                onlineOk = await Step("online-mutes", () => ApplyOnlineTimeAsync(plugin, config, credits, ct)).ConfigureAwait(false);

            // Dependent pair, in order
            await Step("expire-bans", () => plugin.BanManager.ExpireOldBans()).ConfigureAwait(false);
            var cache = plugin.CacheManager;
            if (cache != null)
                await Step("cache-refresh", () => cache.RefreshCacheAsync(config, ct)).ConfigureAwait(false);

            // Independent cleanups
            if (onlineOk)
                await Step("expire-mutes", () => plugin.MuteManager.ExpireOldMutes()).ConfigureAwait(false);
            await Step("expire-warns", () => plugin.WarnManager.ExpireOldWarns()).ConfigureAwait(false);
            await Step("expire-admins", () => plugin.PermissionManager.DeleteOldAdmins()).ConfigureAwait(false);

            // Online players banned meanwhile (site, other servers): the database decides, by SteamID, for exactly the
            // connections that were snapshotted; the cache is not consulted and IP addresses never ban.
            if (sessions.Count > 0)
                await EnforceBansAsync(CS2_SimpleAdmin.DatabaseProvider!, sessions, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            PluginMetrics.PeriodicPass.RecordSince(start);
        }
    }

    /// <summary>
    /// Connect loads read the mutes once. A mute added, extended, shortened or lifted later (site, another server, a
    /// command on this one) must reach players who are already online, so each pass reconciles the in-memory state of
    /// every loaded online player with the active rows of the shared database.
    /// <para>
    /// <b>Ordering.</b> Game thread, one job per player in that player's ordered lane (<see cref="Runtime.TryQueueDbOrdered{T}"/>,
    /// the lane of every mute/unmute/connect-load of the SteamID). The job takes its place, and the revision, when the game
    /// thread accepts it, so its SQL runs after every earlier mute/unmute SQL of that player and before every later one.
    /// The answer is applied with <see cref="PlayerPenaltyManager.ReconcileWithDatabase"/>, which never touches entries
    /// accepted after the read: a stale answer cannot erase a newer mute or bring back a lifted one.
    /// </para>
    /// <para>
    /// A failed read is an exception: the job fails, the in-memory state stays as it is and the next pass tries again. An
    /// empty list is a successful "nothing active" and removes the entries whose rows are gone. The reads cannot be merged
    /// into one batch without giving up the per-player order, so each costs one pooled connection and one tiny query; a
    /// refused job (full queue) skips that player until the next pass.
    /// </para>
    /// </summary>
    internal static void QueueMuteSync(CS2_SimpleAdminConfig config, List<PlayerSession> sessions)
    {
        var timeMode = config.OtherSettings.TimeMode;
        foreach (var session in sessions)
        {
            if (session.LoadState != ConnectLoadState.Loaded || session.SteamId == 0) continue;
            var revision = PlayerPenaltyManager.NextRevision();
            var queued = Runtime.TryQueueDbOrdered<bool>("mute-sync", async ct =>
            {
                var rows = await CS2_SimpleAdmin.Instance.MuteManager
                    .GetActiveMutesAsync(session.SteamId, timeMode, Time.ActualDateTime(), ct).ConfigureAwait(false);
                if (SyncHook != null) await SyncHook("after-read", session.SteamId).ConfigureAwait(false);
                await Runtime.OnGameThread(() => ApplySyncResult(session, revision, rows)).ConfigureAwait(false);
                return true;
            }, session.SteamId);

            if (queued == null) Interlocked.Increment(ref PluginMetrics.MuteSyncRefused);
        }
    }

    /// <summary>Test seam: awaited inside the ordered sync job between the database read and the game-thread apply.</summary>
    internal static Func<string, ulong, Task>? SyncHook { get; set; }

    /// <summary>Game thread: voice effect of a sync (seam; production changes only <c>VoiceFlags.Muted</c> of the controller).</summary>
    internal static Action<PlayerSession, bool> VoiceEffect { get; set; } = static (session, muted) =>
    {
        if (PlayerManager.ResolveController(session) is not { } player) return;
        player.VoiceFlags = muted ? player.VoiceFlags | VoiceFlags.Muted : PenaltyRemoval.WithoutMuted(player.VoiceFlags);
    };

    /// <summary>Game thread: applies one player's database state (see <see cref="QueueMuteSync"/>) if that connection is still current.</summary>
    internal static void ApplySyncResult(PlayerSession session, long readRevision, List<Models.ActiveMuteRow> rows)
    {
        if (!Runtime.Sessions.IsCurrent(session) || !PlayerManager.ControllerAvailable(session))
        {
            Interlocked.Increment(ref PluginMetrics.StaleSessionResults);
            return;
        }

        var db = new List<PlayerPenaltyManager.DbPenalty>(rows.Count);
        foreach (var mute in rows)
        {
            var type = mute.Type switch
            {
                "GAG" => PenaltyType.Gag,
                "MUTE" => PenaltyType.Mute,
                _ => PenaltyType.Silence
            };
            db.Add(new PlayerPenaltyManager.DbPenalty(mute.Id, type, mute.Ends ?? DateTime.MinValue, mute.Duration));
        }

        var released = PlayerPenaltyManager.ReconcileWithDatabase(session.Slot, readRevision, db);

        // Voice follows the restriction in force now (MUTE or SILENCE, any number of them); a gag never touches voice
        if (PenaltyRemoval.HasVoiceRestriction(session.Slot)) VoiceEffect(session, true);
        else if (released) VoiceEffect(session, false);
    }

    /// <summary>
    /// TimeMode 0. Credits the online minutes (idempotent compare-and-set, see <see cref="OnlineCredit"/>), reads which mutes
    /// are now used up and then lets <b>a fresh read in each affected player's ordered lane</b> decide the in-memory state.
    /// <para>
    /// This pass deliberately does <b>not</b> mark entries "passed" from its own read. That read is unordered with respect to
    /// the per-player sync/command/connect jobs, so an application of it could be overtaken by an older sync (which would
    /// bring an expired mute back, scenario A) or applied to a row that was extended meanwhile under the same id (scenario
    /// B). The follow-up sync takes its place in the player's lane when the game thread accepts it, so it runs after every
    /// earlier sync (whose apply is part of that job) and reads the row as it is <i>now</i>: used up → entry dropped and the
    /// voice released at once; extended → entry follows the new duration. A refused follow-up (full queue) only delays the
    /// release until the next pass, which syncs every loaded player anyway: never in the unsafe direction.
    /// </para>
    /// </summary>
    internal static async Task ApplyOnlineTimeAsync(CS2_SimpleAdmin plugin, CS2_SimpleAdminConfig config, List<SessionCredit> credits,
        CancellationToken ct)
    {
        var batch = new List<OnlineCredit>(credits.Count);
        foreach (var credit in credits) batch.Add(credit.Credit);

        var expired = await plugin.MuteManager.CheckOnlineModeMutesAsync(batch, ct)
            .ConfigureAwait(false);

        await Runtime.OnGameThread(() =>
        {
            // Fold into the checkpoint only now that the database holds the credit AND the result was read. If this
            // item never runs (unload), the outstanding credit simply stays on the session; the next pass replays it
            // idempotently and folds it then.
            foreach (var credit in credits)
            {
                if (!credit.Credit.Applied || !ReferenceEquals(credit.Session.PendingCredit, credit.Credit)) continue;
                credit.Session.CreditedTicks += credit.Credit.Ticks;
                credit.Session.PendingCredit = null;
            }

            if (expired.Count == 0) return;
            var affected = new List<PlayerSession>();
            foreach (var row in expired)
            {
                var session = Runtime.Sessions.FindBySteamId((ulong)row.SteamId);
                if (session != null && !affected.Contains(session)) affected.Add(session);
            }

            QueueMuteSync(config, affected);
        }).ConfigureAwait(false);
    }

    /// <summary>Runs one maintenance step; returns false if it failed (cancellation still propagates).</summary>
    private static async Task<bool> Step(string name, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RateLimitedLog.Error($"periodic.{name}", ex, $"Maintenance step '{name}' failed");
            return false;
        }
    }
}
