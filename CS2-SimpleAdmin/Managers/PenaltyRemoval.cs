using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdminApi;

namespace CS2_SimpleAdmin.Managers;

/// <summary>How an unmute/ungag/unsilence ended in the database.</summary>
internal enum UnmuteOutcome
{
    /// <summary>At least one active penalty row was closed.</summary>
    Removed,

    /// <summary>The statement ran, nothing active matched (already expired/removed). Not an error.</summary>
    NothingActive,

    /// <summary>The database work failed; nothing may be assumed to be saved.</summary>
    Failed
}

/// <summary>
/// css_unmute / css_ungag / css_unsilence. Contract: nothing in the game changes until the work was <b>accepted</b>
/// by the database queue, and the in-memory penalties / voice flags / counters are removed only after the SQL
/// really succeeded (on the game thread, for the same connection that was targeted). A refusal (plugin not ready,
/// queue full) or a SQL failure therefore leaves the player exactly as muted as before and the database untouched or
/// unchanged-known. Nothing here waits for SQL on the game thread.
/// </summary>
internal static class PenaltyRemoval
{
    /// <summary>The connection a command targeted, snapshotted on the game thread.</summary>
    internal readonly record struct Target(int Slot, int UserId, ulong SteamId);

    /// <summary>Seam: the SQL part. Production: <see cref="MuteManager.UnmutePlayer"/>.</summary>
    internal static Func<string, string, string, int, Task<UnmuteOutcome>> UnmuteSql { get; set; } =
        static (pattern, admin, reason, type) => CS2_SimpleAdmin.Instance.MuteManager.UnmutePlayer(pattern, admin, reason, type);

    /// <summary>Seam, game thread: is the targeted connection still in its slot?</summary>
    internal static Func<Target, bool> IsTargetCurrent { get; set; } = static target =>
    {
        var player = Utilities.GetPlayerFromSlot(target.Slot);
        return player is { IsValid: true } && player.SteamID == target.SteamId && (player.UserId ?? -1) == target.UserId;
    };

    /// <summary>Seam, game thread: voice flags back to normal (mute/silence only).</summary>
    internal static Action<Target> ResetVoice { get; set; } = static target =>
    {
        if (Utilities.GetPlayerFromSlot(target.Slot) is { IsValid: true } player)
            player.VoiceFlags = VoiceFlags.Normal;
    };

    /// <summary>Tests: put the production implementations of the seams back.</summary>
    internal static void RestoreDefaults()
    {
        UnmuteSql = static (pattern, admin, reason, type) => CS2_SimpleAdmin.Instance.MuteManager.UnmutePlayer(pattern, admin, reason, type);
        IsTargetCurrent = static target =>
        {
            var player = Utilities.GetPlayerFromSlot(target.Slot);
            return player is { IsValid: true } && player.SteamID == target.SteamId && (player.UserId ?? -1) == target.UserId;
        };
        ResetVoice = static target =>
        {
            if (Utilities.GetPlayerFromSlot(target.Slot) is { IsValid: true } player)
                player.VoiceFlags = VoiceFlags.Normal;
        };
    }

    /// <param name="type">0 = gag, 1 = mute, 2 = silence (same codes as <see cref="MuteManager.UnmutePlayer"/>).</param>
    /// <param name="sqlPattern">SteamID64 text or name pattern the SQL matches.</param>
    /// <param name="connection">The online connection (a value snapshot, not a controller) to clean up after the SQL succeeded; null for an offline target.</param>
    /// <param name="decrementCounter">Also lower the player's penalty counter (name-matched commands did this before).</param>
    /// <returns>True when the work was accepted; false when refused (the caller was already told, nothing changed).</returns>
    internal static bool TryQueue(CallerRef caller, string callerSteamId, string sqlPattern, string reason, int type,
        Target? connection, bool decrementCounter)
    {
        return CS2_SimpleAdmin.TryQueuePenaltyWork(caller, null, "mute-write", async _ =>
        {
            var outcome = await UnmuteSql(sqlPattern, callerSteamId, reason, type).ConfigureAwait(false);
            if (outcome == UnmuteOutcome.Failed)
            {
                await CS2_SimpleAdmin.ReportWriteFailureAsync("Unmute").ConfigureAwait(false);
                return;
            }

            // Bound to the lifetime the job was accepted in; the callback re-checks the connection itself
            if (connection is { } snapshot)
                await Runtime.OnGameThread(() => ApplyToTarget(snapshot, type, decrementCounter)).ConfigureAwait(false);
        });
    }

    /// <summary>Game thread. Removes the in-memory side of the penalty if the targeted connection is still there.</summary>
    internal static void ApplyToTarget(Target target, int type, bool decrementCounter)
    {
        if (!IsTargetCurrent(target)) return; // left, or the slot holds another connection now

        var penaltyType = type switch { 1 => PenaltyType.Mute, 2 => PenaltyType.Silence, _ => PenaltyType.Gag };
        PlayerPenaltyManager.RemovePenaltiesByType(target.Slot, penaltyType);
        if (type != 0) ResetVoice(target);

        if (decrementCounter && CS2_SimpleAdmin.PlayersInfo.TryGetValue(target.SteamId, out var info))
        {
            switch (penaltyType)
            {
                case PenaltyType.Gag when info.TotalGags > 0: info.TotalGags--; break;
                case PenaltyType.Mute when info.TotalMutes > 0: info.TotalMutes--; break;
                case PenaltyType.Silence when info.TotalSilences > 0: info.TotalSilences--; break;
            }
        }
    }
}
