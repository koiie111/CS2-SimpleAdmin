using CounterStrikeSharp.API.ValveConstants.Protobuf;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// The order of events of a kick, independent of the engine. <b>The disconnect is the only mandatory step.</b> Everything
/// that makes a kick look nicer (freeze, red tint, blocked weapons, muting voice) is decoration: it may be impossible
/// (no pawn, dead or observing player, no WeaponServices) or throw, and none of that may ever cancel the disconnect. The
/// helper <c>banid</c> command is best effort for the same reason.
/// <para>
/// A delayed kick does not keep a controller object: the timer re-resolves the slot and disconnects only if it still holds
/// the same connection (slot + userid + SteamID64), so a reused slot is never kicked by the timer of a former occupant.
/// </para>
/// </summary>
internal static class KickFlow
{
    /// <summary>The connection a kick was ordered for, snapshotted when it was ordered.</summary>
    internal readonly record struct Target(int Slot, int UserId, ulong SteamId);

    internal sealed class Ops
    {
        /// <summary>Marks the player as "kick pending" (PlayersInfo.WaitingForKick). Informational.</summary>
        public required Action<Target> MarkWaiting { get; init; }

        /// <summary>Cosmetic effects, each one best effort.</summary>
        public required Action<Target> Decorate { get; init; }

        /// <summary>Disconnects whatever controller currently is that connection; does nothing if it is gone.</summary>
        public required Action<Target, NetworkDisconnectionReason> Disconnect { get; init; }

        /// <summary>Does the slot still hold this exact connection?</summary>
        public required Func<Target, bool> IsSameConnection { get; init; }

        /// <summary>Runs an action on the game thread after the given seconds.</summary>
        public required Action<float, Action> Schedule { get; init; }

        /// <summary>The <c>banid</c> helper for a banned player; best effort.</summary>
        public required Action<Target> BanId { get; init; }

        /// <summary>Is a delayed kick already pending for this player?</summary>
        public required Func<Target, bool> IsKickPending { get; init; }

        public Action<string, Exception>? OnError { get; init; }
    }

    public static void Run(Target target, NetworkDisconnectionReason reason, int delay, Ops ops)
    {
        // A second delayed kick would only stack another timer; an immediate kick is never swallowed by an earlier one
        if (delay > 0 && Safe(ops, "pending", () => ops.IsKickPending(target), false)) return;

        Safe(ops, "mark", () => ops.MarkWaiting(target));
        Safe(ops, "decorate", () => ops.Decorate(target));

        if (reason == NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED)
            Safe(ops, "banid", () => ops.BanId(target));

        if (delay > 0)
        {
            ops.Schedule(delay, () =>
            {
                if (!Safe(ops, "same", () => ops.IsSameConnection(target), false)) return;
                Safe(ops, "disconnect", () => ops.Disconnect(target, reason));
            });
            return;
        }

        ops.Disconnect(target, reason);
    }

    private static void Safe(Ops ops, string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ops.OnError?.Invoke(step, ex);
        }
    }

    private static T Safe<T>(Ops ops, string step, Func<T> func, T fallback)
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            ops.OnError?.Invoke(step, ex);
            return fallback;
        }
    }
}
