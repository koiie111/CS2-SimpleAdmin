using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Identity of whoever issued a command (an admin, or the server console), copied from the controller <b>on the
/// game thread when the command is handled</b>. Background code carries this plain value instead of the
/// <see cref="CCSPlayerController"/>: reading <c>Slot</c>/<c>SteamID</c>/<c>UserId</c> of a controller is a native
/// schema access that is only allowed on the game thread and may hit a disconnected player.
/// The controller is looked up again, on the game thread only, by <see cref="Notify"/>, and only if the same
/// connection (slot + SteamID + userid) is still there, so a message can never reach another player that reused the slot.
/// </summary>
internal readonly record struct CallerRef(bool IsConsole, int Slot, ulong SteamId, int UserId)
{
    public static readonly CallerRef Console = new(true, -1, 0, -1);

    /// <summary>Game thread. Snapshot of the caller; null means the server console.</summary>
    public static CallerRef Capture(CCSPlayerController? caller) =>
        caller == null ? Console : new CallerRef(false, caller.Slot, caller.SteamID, caller.UserId ?? -1);

    // ---- test seams: the real implementations touch native memory ----

    /// <summary>Looks up the connection currently in a slot (game thread).</summary>
    internal static Func<int, (ulong SteamId, int UserId)?> Lookup { get; set; } = static slot =>
        Utilities.GetPlayerFromSlot(slot) is { IsValid: true } p ? (p.SteamID, p.UserId ?? -1) : null;

    internal static Action<int, string> PrintToChat { get; set; } = static (slot, message) =>
        Utilities.GetPlayerFromSlot(slot)?.PrintToChat(message);

    internal static Action<string> PrintToConsole { get; set; } = static message => Server.PrintToConsole(message);

    /// <summary>
    /// Game thread only. Tells the caller; if the caller left (or their slot now belongs to someone else) the message
    /// goes to the server console so that a failed mandatory write is still visible.
    /// </summary>
    public void Notify(string message)
    {
        GameThread.AssertCurrent(nameof(Notify));
        if (!IsConsole && Lookup(Slot) is { } now && now.SteamId == SteamId && now.UserId == UserId)
            PrintToChat(Slot, message);
        else
            PrintToConsole(message);
    }
}
