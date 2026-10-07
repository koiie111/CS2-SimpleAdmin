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
/// <item>TimeMode 0: each session accrues real online time; whole minutes are credited to its mutes set-based and
/// committed to the session only after the UPDATE succeeded (no double counting after reconnects or retries,
/// no drift from the 61 s period).</item>
/// </list>
/// </summary>
internal static class PeriodicMaintenance
{
    private static int _running;
    private static readonly long TicksPerMinute = Stopwatch.Frequency * 60;

    internal readonly record struct SessionCredit(PlayerSession Session, int Minutes, long CreditTicks);

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
        var credits = config.OtherSettings.TimeMode == 0 ? ComputeCredits(sessions, Stopwatch.GetTimestamp()) : [];

        if (!Runtime.TryQueueDb("periodic", ct => RunAsync(plugin, config, serverId, sessions, credits, ct)))
            Volatile.Write(ref _running, 0);
    }

    /// <summary>Whole online minutes not yet credited, per session (game thread; pure apart from reading sessions).</summary>
    internal static List<SessionCredit> ComputeCredits(List<PlayerSession> sessions, long nowTimestamp)
    {
        var credits = new List<SessionCredit>(sessions.Count);
        foreach (var session in sessions)
        {
            var uncredited = nowTimestamp - session.StartedTimestamp - session.CreditedTicks;
            var minutes = (int)(uncredited / TicksPerMinute);
            if (minutes > 0)
                credits.Add(new SessionCredit(session, minutes, minutes * TicksPerMinute));
        }

        return credits;
    }

    private static async Task RunAsync(CS2_SimpleAdmin plugin, CS2_SimpleAdminConfig config, int? serverId,
        List<PlayerSession> sessions, List<SessionCredit> credits, CancellationToken ct)
    {
        var start = LatencyHistogram.Now();
        try
        {
            // Dependent pair, in order
            await Step("expire-bans", () => plugin.BanManager.ExpireOldBans()).ConfigureAwait(false);
            var cache = plugin.CacheManager;
            if (cache != null)
                await Step("cache-refresh", () => cache.RefreshCacheAsync(config, serverId, ct)).ConfigureAwait(false);

            // Independent cleanups
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

            if (config.OtherSettings.TimeMode == 0 && credits.Count > 0)
                await Step("online-mutes", () => ApplyOnlineTimeAsync(plugin, config, serverId, credits, ct)).ConfigureAwait(false);
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
        var rows = new List<(ulong SteamId, int Minutes)>(credits.Count);
        foreach (var credit in credits) rows.Add((credit.Session.SteamId, credit.Minutes));

        var expired = await plugin.MuteManager.CheckOnlineModeMutesAsync(rows, config.MultiServerMode, serverId, ct)
            .ConfigureAwait(false);

        await Runtime.OnGameThread(() =>
        {
            // Commit only after the UPDATE succeeded; a failed pass credits the same time next pass
            foreach (var credit in credits)
                credit.Session.CreditedTicks += credit.CreditTicks;

            foreach (var row in expired)
            {
                var session = Runtime.Sessions.FindBySteamId((ulong)row.SteamId);
                if (session == null || row.Ends == null) continue;
                PlayerPenaltyManager.RemovePenaltiesByDateTime(session.Slot, row.Ends.Value);
            }
        }).ConfigureAwait(false);
    }

    private static async Task Step(string name, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RateLimitedLog.Error($"periodic.{name}", ex, $"Maintenance step '{name}' failed");
        }
    }
}
