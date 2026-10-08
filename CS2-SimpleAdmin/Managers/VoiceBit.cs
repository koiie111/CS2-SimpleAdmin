using CounterStrikeSharp.API;
using CS2_SimpleAdmin.Infrastructure;

namespace CS2_SimpleAdmin.Managers;

/// <summary>
/// The one place that changes <see cref="VoiceFlags.Muted"/> on behalf of the penalty state. Game thread only.
/// <para>
/// The native flag lives in the engine's player object, not in this plugin: a hot reload of SimpleAdmin clears
/// <see cref="PlayerPenaltyManager"/> but leaves the flag of connected players as it was. So after an authoritative read the
/// bit must be set <i>and</i> cleared to match the penalty state in force, otherwise a mute lifted elsewhere while the
/// plugin was down would silence the player until reconnect. Only the Muted bit is touched; other voice flags stay.
/// </para>
/// </summary>
internal static class VoiceBit
{
    /// <summary>Seam: the controller's current flags, or null when the session is no longer the connection in its slot.</summary>
    internal static Func<PlayerSession, VoiceFlags?> Read { get; set; } = static session =>
        PlayerManager.ResolveController(session)?.VoiceFlags;

    /// <summary>Seam: stores the flags on the controller of the session (no-op when the session is not current).</summary>
    internal static Action<PlayerSession, VoiceFlags> Write { get; set; } = static (session, flags) =>
    {
        if (PlayerManager.ResolveController(session) is { } player) player.VoiceFlags = flags;
    };

    /// <summary>Flags with the Muted bit set or cleared; everything else is kept.</summary>
    internal static VoiceFlags With(VoiceFlags flags, bool muted) =>
        muted ? flags | VoiceFlags.Muted : PenaltyRemoval.WithoutMuted(flags);

    /// <summary>Makes the Muted bit equal <paramref name="muted"/>; writes only when it differs.</summary>
    internal static void Set(PlayerSession session, bool muted)
    {
        if (Read(session) is not { } current) return;
        var wanted = With(current, muted);
        if (wanted != current) Write(session, wanted);
    }

    /// <summary>Muted bit follows the MUTE/SILENCE state of the slot as it is now.</summary>
    internal static void SyncToPenalties(PlayerSession session) =>
        Set(session, PenaltyRemoval.HasVoiceRestriction(session.Slot));
}
