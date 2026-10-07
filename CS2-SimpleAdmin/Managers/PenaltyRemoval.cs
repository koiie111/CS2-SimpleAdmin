using System.Globalization;
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
/// css_unmute / css_ungag / css_unsilence.
/// <para>
/// <b>Contract.</b> Nothing in the game changes until the work was <i>accepted</i> by the database queue. The in-memory
/// penalties, voice flags and counters change only after the SQL really succeeded, on the game thread, for the same
/// connection that was targeted. A refusal (plugin not ready, queue full) or a SQL failure leaves the player exactly as
/// muted as before. Nothing here waits for SQL, a lock or a task on the game thread. The admin is told at once that the
/// work was queued, and again, from the game-thread callback, how it ended.
/// </para>
/// <para>
/// <b>Order guarantee.</b> All penalty operations about one SteamID64 (issue, remove, connect load) get a place in one
/// order when the game thread accepts them:
/// </para>
/// <list type="number">
/// <item>their SQL runs in exactly that order, on one worker (SQLite) and on several (MySQL) alike, see
/// <see cref="KeyedSequencer"/>; so an unmute closes the penalties accepted before it and none accepted after it;</item>
/// <item>every in-memory penalty carries the accept position of the operation that created it
/// (<see cref="PlayerPenaltyManager.Entry.Revision"/>); the removal remembers its own position and later removes only
/// entries up to it. A penalty issued while the removal was still waiting for its SQL or for the game thread survives;
/// the penalties it was meant to remove do not stay forever either.</item>
/// </list>
/// <para>
/// Limit: an offline unmute by a <i>name</i> pattern has no single player to order against and is not sequenced
/// (it has no in-memory side either).
/// </para>
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

    /// <summary>
    /// Seam, game thread: lift the voice restriction (mute/silence only). Only the <see cref="VoiceFlags.Muted"/> bit is
    /// cleared; other voice flags (listen-all, team...) are not the penalty's business and stay.
    /// </summary>
    internal static Action<Target> ResetVoice { get; set; } = static target =>
    {
        if (Utilities.GetPlayerFromSlot(target.Slot) is { IsValid: true } player)
            player.VoiceFlags = WithoutMuted(player.VoiceFlags);
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
                player.VoiceFlags = WithoutMuted(player.VoiceFlags);
        };
    }

    /// <summary>Voice flags with only the penalty's own bit cleared; listen-all, team and the other flags are kept.</summary>
    internal static VoiceFlags WithoutMuted(VoiceFlags flags) => flags & ~VoiceFlags.Muted;

    /// <summary>The first SteamID64 (7656119...); anything below is not an account id.</summary>
    private const ulong FirstSteamId64 = 76561197960265728UL;

    private static ulong? SteamIdOf(string pattern) =>
        ulong.TryParse(pattern, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= FirstSteamId64
            ? id
            : null;

    /// <param name="type">0 = gag, 1 = mute, 2 = silence (same codes as <see cref="MuteManager.UnmutePlayer"/>).</param>
    /// <param name="sqlPattern">SteamID64 text or name pattern the SQL matches.</param>
    /// <param name="connection">The online connection (a value snapshot, not a controller) to clean up after the SQL succeeded; null for an offline target.</param>
    /// <param name="decrementCounter">Also lower the player's penalty counter (name-matched commands did this before).</param>
    /// <param name="label">How the target is named in the result message (defaults to the pattern). Plain text snapshotted by the caller.</param>
    /// <returns>True when the work was accepted; false when refused (the caller was already told, nothing changed).</returns>
    internal static bool TryQueue(CallerRef caller, string callerSteamId, string sqlPattern, string reason, int type,
        Target? connection, bool decrementCounter, string? label = null)
    {
        // Game thread: this is the removal's place in the accept order (see the class remarks)
        var acceptedAt = connection != null ? PlayerPenaltyManager.NextRevision() : 0;
        var orderKey = connection?.SteamId ?? SteamIdOf(sqlPattern);
        var (done, noun) = type switch { 1 => ("Unmuted", "mute"), 2 => ("Unsilenced", "silence"), _ => ("Ungagged", "gag") };
        label ??= sqlPattern;

        return CS2_SimpleAdmin.TryQueuePenaltyWork(caller, null, "mute-write", async _ =>
        {
            var outcome = await UnmuteSql(sqlPattern, callerSteamId, reason, type).ConfigureAwait(false);
            if (outcome == UnmuteOutcome.Failed)
            {
                await CS2_SimpleAdmin.ReportWriteFailureAsync("Unmute").ConfigureAwait(false);
                return;
            }

            // Bound to the lifetime the job was accepted in; the callback re-checks the connection itself
            await Runtime.OnGameThread(() =>
            {
                if (connection is { } snapshot)
                    ApplyToTarget(snapshot, type, decrementCounter, acceptedAt);
                caller.Notify(outcome == UnmuteOutcome.Removed
                    ? $"{done} {label}."
                    : $"{label}: no active {noun} found (nothing to remove).");
            }).ConfigureAwait(false);
        }, orderKey: orderKey);
    }

    /// <summary>
    /// Game thread. Removes the in-memory side of the penalty if the targeted connection is still there: only the
    /// entries accepted up to <paramref name="upToRevision"/> (default: all), then recomputes the voice state from what
    /// is still in force.
    /// </summary>
    internal static void ApplyToTarget(Target target, int type, bool decrementCounter, long upToRevision = long.MaxValue)
    {
        if (!IsTargetCurrent(target)) return; // left, or the slot holds another connection now

        var penaltyType = type switch { 1 => PenaltyType.Mute, 2 => PenaltyType.Silence, _ => PenaltyType.Gag };
        PlayerPenaltyManager.RemovePenaltiesByType(target.Slot, penaltyType, upToRevision);

        // Voice follows the restriction that is in force now, not the one that was just removed: a MUTE and a SILENCE
        // may both be active, and a gag never touches voice
        if (type != 0 && !HasVoiceRestriction(target.Slot)) ResetVoice(target);

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

    /// <summary>True while an unexpired MUTE or SILENCE (timed or permanent) is in force for the slot.</summary>
    internal static bool HasVoiceRestriction(int slot) =>
        PlayerPenaltyManager.IsPenalized(slot, PenaltyType.Mute, out _) ||
        PlayerPenaltyManager.IsPenalized(slot, PenaltyType.Silence, out _);
}
