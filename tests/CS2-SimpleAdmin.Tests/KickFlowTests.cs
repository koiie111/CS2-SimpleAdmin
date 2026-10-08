using CounterStrikeSharp.API.ValveConstants.Protobuf;
using CS2_SimpleAdmin.Infrastructure;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// The kick must always reach Disconnect: with no pawn, a dead/observing player, no WeaponServices, a failing effect, a
/// failing banid helper, a pending earlier kick; and a delayed kick must only ever hit the connection it was ordered for.
/// (Both <c>Helper.KickPlayer</c> overloads run <see cref="KickFlow"/> with the production operations.)
/// </summary>
public class KickFlowTests
{
    private static readonly KickFlow.Target Player = new(3, 12, 76561198000000001);

    private sealed class Fake
    {
        public readonly List<string> Calls = [];
        public readonly List<(float Seconds, Action Run)> Timers = [];
        public readonly List<string> Errors = [];
        public bool SameConnection = true;
        public bool Pending;
        public Exception? DecorateFails, BanIdFails, MarkFails;

        public KickFlow.Ops Ops => new()
        {
            MarkWaiting = _ => { Calls.Add("mark"); if (MarkFails != null) throw MarkFails; },
            Decorate = _ => { Calls.Add("decorate"); if (DecorateFails != null) throw DecorateFails; },
            Disconnect = (_, reason) => Calls.Add("disconnect:" + reason),
            IsSameConnection = _ => SameConnection,
            Schedule = (s, a) => Timers.Add((s, a)),
            BanId = _ => { Calls.Add("banid"); if (BanIdFails != null) throw BanIdFails; },
            IsKickPending = _ => Pending,
            OnError = (step, ex) => Errors.Add(step + ":" + ex.Message)
        };
    }

    [Fact]
    public void ImmediateKickDisconnects()
    {
        var f = new Fake();
        KickFlow.Run(Player, NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED, 0, f.Ops);
        Assert.Equal(["mark", "decorate", "disconnect:NETWORK_DISCONNECT_KICKED"], f.Calls);
    }

    [Fact]
    public void AFailingDecorationNeverCancelsTheDisconnect()
    {
        // Stands for: no pawn, dead/observing player, WeaponServices == null (the old code returned before Disconnect), any native error
        var f = new Fake { DecorateFails = new InvalidOperationException("WeaponServices is null") };
        KickFlow.Run(Player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED, 0, f.Ops);
        Assert.Contains("disconnect:NETWORK_DISCONNECT_REJECT_BANNED", f.Calls);
        Assert.Contains(f.Errors, e => e.StartsWith("decorate:"));
    }

    [Fact]
    public void AFailingMarkOrBanIdNeverCancelsTheDisconnect()
    {
        var f = new Fake { MarkFails = new Exception("x"), BanIdFails = new Exception("banid unavailable") };
        KickFlow.Run(Player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED, 0, f.Ops);
        Assert.Contains("disconnect:NETWORK_DISCONNECT_REJECT_BANNED", f.Calls);
    }

    [Fact]
    public void ABannedConnectionDoesNotDependOnKickTimeOrTheBanIdHelper()
    {
        // Production passes delay 0 for a connect ban; the helper only runs for the banned reason and cannot block the kick
        var f = new Fake();
        KickFlow.Run(Player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED, 0, f.Ops);
        Assert.Empty(f.Timers);
        Assert.Equal("disconnect:NETWORK_DISCONNECT_REJECT_BANNED", f.Calls[^1]);

        var other = new Fake();
        KickFlow.Run(Player, NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED, 0, other.Ops);
        Assert.DoesNotContain("banid", other.Calls);
    }

    [Fact]
    public void ADelayedKickDisconnectsOnlyTheSameConnectionWhenTheTimerFires()
    {
        var f = new Fake();
        KickFlow.Run(Player, NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED, 5, f.Ops);
        Assert.Equal(["mark", "decorate"], f.Calls);
        var timer = Assert.Single(f.Timers);
        Assert.Equal(5, timer.Seconds);

        f.SameConnection = false;   // the slot was reused by another player meanwhile
        timer.Run();
        Assert.DoesNotContain(f.Calls, c => c.StartsWith("disconnect"));

        f.SameConnection = true;
        timer.Run();
        Assert.Contains("disconnect:NETWORK_DISCONNECT_KICKED", f.Calls);
    }

    [Fact]
    public void ADelayedKickDoesNotStackButAnImmediateKickIsNeverSwallowed()
    {
        var f = new Fake { Pending = true };
        KickFlow.Run(Player, NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED, 5, f.Ops);
        Assert.Empty(f.Calls);   // a kick is already waiting: no second timer
        Assert.Empty(f.Timers);

        // The old controller overload returned on WaitingForKick and so did not disconnect a banned player at all
        KickFlow.Run(Player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED, 0, f.Ops);
        Assert.Contains("disconnect:NETWORK_DISCONNECT_REJECT_BANNED", f.Calls);
    }

    [Fact]
    public void ARepeatedImmediateKickDisconnectsAgain()
    {
        var f = new Fake();
        KickFlow.Run(Player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED, 0, f.Ops);
        f.Pending = true; // WaitingForKick is set after the first call
        KickFlow.Run(Player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED, 0, f.Ops);
        Assert.Equal(2, f.Calls.Count(c => c.StartsWith("disconnect:")));
    }
}
