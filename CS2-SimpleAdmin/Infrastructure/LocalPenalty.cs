using CounterStrikeSharp.API.Core;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdminApi;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Order of effects when an admin issues a GAG / MUTE / SILENCE to an online player: the database work is offered to the
/// bounded queue first; <b>only if it was accepted</b> the in-memory restriction is added and the voice flag set. A refusal
/// (queue full, plugin not ready) leaves the player exactly as before: no local penalty, no voice change, no success message.
/// </summary>
internal static class LocalPenalty
{
    /// <param name="tryQueue">Offers the write to the queue; false = refused (the caller was told).</param>
    /// <param name="applyVoice">Sets the Muted voice bit of the target (MUTE / SILENCE); null for GAG.</param>
    /// <returns>True when the work was accepted and the local state applied.</returns>
    internal static bool Issue(Func<bool> tryQueue, PenaltyType type, CCSPlayerController player, int minutes,
        Action<CCSPlayerController>? applyVoice) =>
        Issue(tryQueue, type, player.Slot, minutes, applyVoice == null ? null : () => applyVoice(player));

    internal static bool Issue(Func<bool> tryQueue, PenaltyType type, int slot, int minutes, Action? applyVoice)
    {
        if (!tryQueue()) return false;
        PlayerPenaltyManager.AddPenalty(slot, type, Time.ActualDateTime().AddMinutes(minutes), minutes, 0);
        applyVoice?.Invoke();
        return true;
    }
}
