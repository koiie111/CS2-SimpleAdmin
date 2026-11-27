using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core;
using CS2_SimpleAdmin_StealthModule;
using StealthPlugin = CS2_SimpleAdmin_StealthModule.CS2_SimpleAdmin_StealthModule;

namespace CS2_SimpleAdmin.Tests;

public sealed class StealthTransmitTests
{
    [Fact]
    public void RegistersOnlyWhenAvailableWithThreePlayersAndASilentAdmin()
    {
        var adds = 0;
        var removes = 0;
        var state = new TransmitSubscription(() => adds++, () => removes++);
        for (var players = 0; players < 3; players++)
        for (var silent = 0; silent <= 2; silent++)
            state.Sync(true, players, silent);

        state.Sync(false, 3, 2);
        state.Sync(true, 3, 0);
        Assert.False(state.Active);
        Assert.Equal(0, adds);

        state.Sync(true, 3, 1);
        state.Sync(true, 3, 2);
        state.Sync(true, 3, 1);
        Assert.True(state.Active);
        Assert.Equal(1, adds);

        state.Sync(true, 3, 0); // last silent admin leaves
        state.Sync(true, 3, 0);
        Assert.False(state.Active);
        Assert.Equal(1, removes);

        state.Sync(true, 3, 1); // bootstrap from the existing silent snapshot
        state.Sync(true, 2, 1);
        state.Sync(false, 2, 1);
        Assert.Equal(2, adds);
        Assert.Equal(2, removes);
        Assert.False(state.Active);
    }

    [Fact]
    public void NativeAndIndexerPreserveTheirOwnRecipientPolicies()
    {
        bool[] tracked = [true, false, false]; // first human, then untracked client and HLTV
        Assert.True(StealthPlugin.IsNativeRecipient(tracked, 0));
        Assert.False(StealthPlugin.IsNativeRecipient(tracked, 1));
        Assert.False(StealthPlugin.IsNativeRecipient(tracked, 2));
        Assert.False(StealthPlugin.IsNativeRecipient(tracked, -1));
        Assert.False(StealthPlugin.IsNativeRecipient(tracked, 128));
        Assert.True(StealthPlugin.IsIndexerRecipient(true, false)); // also accepts untracked non-HLTV
        Assert.False(StealthPlugin.IsIndexerRecipient(true, true));
        Assert.False(StealthPlugin.IsIndexerRecipient(false, false));
    }

    [Fact]
    public void ClearsExactlyTheBaselineBitsAndNeverAddsBits()
    {
        var targets = new ObserverTargets();
        (int Owner, int Index)[] entries = [(0, 31), (4, 32), (7, 63), (127, 64), (6, 8193)];
        foreach (var (owner, index) in entries)
            targets.Set(owner, index, (uint)index);

        foreach (var recipient in new[] { 0, 4, 7, 127, 6, 3, -1, 129 })
        {
            var before = Enumerable.Repeat(uint.MaxValue, 260).ToArray();
            before[0] &= ~(1u << 31);
            var expected = (uint[])before.Clone();
            foreach (var (owner, index) in entries)
                if (owner != recipient)
                    expected[index >> 5] &= ~(1u << (index & 31));
            Hide(targets, before, recipient);
            Assert.Equal(expected, before);
        }
    }

    [Fact]
    public void DeletingAnOldSerialKeepsReplacementAndDeletingCurrentSerialUpdatesDensePositions()
    {
        var targets = new ObserverTargets();
        targets.Set(0, 31, 0x1001f);
        targets.Set(1, 32, 0x10020);
        targets.Set(0, 31, 0x2001f);
        Assert.False(targets.RemoveDeleted(0, 0x1001f));
        Assert.True(targets.ContainsIndex(31));
        Assert.True(targets.RemoveDeleted(1, 0x10020));

        uint[] words = [uint.MaxValue, uint.MaxValue];
        Hide(targets, words, 0);
        Assert.Equal(new[] { uint.MaxValue, uint.MaxValue }, words);
        Hide(targets, words, 1);
        Assert.Equal(new[] { 0x7fffffffu, uint.MaxValue }, words);
        Assert.True(targets.RemoveDeleted(0, 0x2001f));
        Assert.False(targets.ContainsIndex(31));
        targets.Reset();
        Hide(targets, words, 0);
        Assert.Equal(new[] { 0x7fffffffu, uint.MaxValue }, words);
    }

    private static unsafe void Hide(ObserverTargets targets, uint[] words, int recipient)
    {
        fixed (uint* bits = words)
        {
            nint ptr = (nint)bits;
            targets.Hide(Unsafe.As<nint, CFixedBitVecBase>(ref ptr), recipient);
        }
    }
}
