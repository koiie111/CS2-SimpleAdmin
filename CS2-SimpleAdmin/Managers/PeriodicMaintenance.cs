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
                    player.VoiceFlags = VoiceFlags.Normal;
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
        var serverId = CS2_SimpleAdmin.ServerId;
        var sessions = new List<PlayerSession>();
        Runtime.Sessions.Snapshot(sessions);
        var credits = config.OtherSettings.TimeMode == 0
            ? ComputeCredits(sessions, Stopwatch.GetTimestamp(), Time.ActualDateTime())
            : [];

        if (!Runtime.TryQueueDb("periodic", ct => RunAsync(plugin, config, serverId, sessions, credits, ct)))
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

    private static async Task RunAsync(CS2_SimpleAdmin plugin, CS2_SimpleAdminConfig config, int? serverId,
        List<PlayerSession> sessions, List<SessionCredit> credits, CancellationToken ct)
    {
        var start = LatencyHistogram.Now();
        try
        {
            // TimeMode 0 first: its result (mutes used up) must be reported before the mute-expiry step below can mark
            // them EXPIRED in SQL, and if it failed that step is skipped so no used-up mute escapes the report.
            var onlineOk = true;
            if (config.OtherSettings.TimeMode == 0 && credits.Count > 0)
                onlineOk = await Step("online-mutes", () => ApplyOnlineTimeAsync(plugin, config, serverId, credits, ct)).ConfigureAwait(false);

            // Dependent pair, in order
            await Step("expire-bans", () => plugin.BanManager.ExpireOldBans()).ConfigureAwait(false);
            var cache = plugin.CacheManager;
            if (cache != null)
                await Step("cache-refresh", () => cache.RefreshCacheAsync(config, serverId, ct)).ConfigureAwait(false);

            // Independent cleanups
            if (onlineOk)
                await Step("expire-mutes", () => plugin.MuteManager.ExpireOldMutes()).ConfigureAwait(false);
            await Step("expire-warns", () => plugin.WarnManager.ExpireOldWarns()).ConfigureAwait(false);
            await Step("expire-admins", () => plugin.PermissionManager.DeleteOldAdmins()).ConfigureAwait(false);

            // Online players banned meanwhile (site, other servers): kick the exact connections that were checked
            if (cache != null && sessions.Count > 0)
            {
                var now = Time.ActualDateTime();
                var banned = new List<PlayerSession>();
                foreach (var session in sessions)
                    if (cache.CheckBan(config, session.SteamId, session.IpAddress, now).IsBanned)
                        banned.Add(session);

                if (banned.Count > 0)
                    await Runtime.OnGameThread(() =>
                    {
                        foreach (var session in banned)
                        {
                            var player = PlayerManager.ResolveController(session);
                            if (player == null)
                            {
                                Interlocked.Increment(ref PluginMetrics.StaleSessionResults);
                                continue;
                            }

                            Helper.KickPlayer(player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED);
                        }
                    }).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            PluginMetrics.PeriodicPass.RecordSince(start);
        }
    }

    private static async Task ApplyOnlineTimeAsync(CS2_SimpleAdmin plugin, CS2_SimpleAdminConfig config, int? serverId,
        List<SessionCredit> credits, CancellationToken ct)
    {
        var batch = new List<OnlineCredit>(credits.Count);
        foreach (var credit in credits) batch.Add(credit.Credit);

        var expired = await plugin.MuteManager.CheckOnlineModeMutesAsync(batch, config.MultiServerMode, serverId, ct)
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

            foreach (var row in expired)
            {
                var session = Runtime.Sessions.FindBySteamId((ulong)row.SteamId);
                if (session == null || row.Ends == null) continue;
                PlayerPenaltyManager.RemovePenaltiesByDateTime(session.Slot, row.Ends.Value);
            }
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
