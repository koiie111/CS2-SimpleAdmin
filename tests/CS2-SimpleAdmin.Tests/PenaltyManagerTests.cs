using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdminApi;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// F03: the old manager kept List/Dictionary inside a ConcurrentDictionary and mutated them from the connect
/// task, the online-mute task and the chat hook at the same time (lost/duplicated penalties, "collection was
/// modified" exceptions). Read paths also mutated (removal inside IsPenalized).
/// </summary>
public class PenaltyManagerTests : IDisposable
{
    public PenaltyManagerTests()
    {
        TestConfig.Use(c => c.OtherSettings.TimeMode = 1);
        PlayerPenaltyManager.RemoveAllPenalties();
    }

    public void Dispose() => PlayerPenaltyManager.RemoveAllPenalties();

    [Fact]
    public async Task ConcurrentWritersAndReadersLoseNothingAndNeverThrow()
    {
        const int writers = 8, perWriter = 2_000, slot = 3;
        var end = DateTime.UtcNow.AddYears(1);
        using var stop = new CancellationTokenSource();
        var readerErrors = 0;

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    PlayerPenaltyManager.IsPenalized(slot, PenaltyType.Gag, out DateTime? _);
                    PlayerPenaltyManager.GetAllPlayerPenalties(slot);
                    PlayerPenaltyManager.RemovePenaltiesByDateTime(slot, end.AddDays(1)); // no-op writer racing
                }
                catch
                {
                    Interlocked.Increment(ref readerErrors);
                }
            }
        })).ToArray();

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
                PlayerPenaltyManager.AddPenalty(slot, PenaltyType.Gag, end, 10);
        })));
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Equal(0, readerErrors);
        Assert.Equal(writers * perWriter, PlayerPenaltyManager.GetPlayerPenalties(slot, PenaltyType.Gag).Count);
    }

    [Fact]
    public void TimeMode1_ExpiredEntryIsInactiveWithoutMutatingOnRead()
    {
        var now = new DateTime(2026, 10, 7, 12, 0, 0);
        var entries = new[]
        {
            new PlayerPenaltyManager.Entry(now.AddMinutes(-1), 5, false), // expired
            new PlayerPenaltyManager.Entry(now.AddMinutes(10), 5, false)  // active
        };

        Assert.True(PlayerPenaltyManager.IsPenalized(entries, 1, now, out var endsAt));
        Assert.Equal(now.AddMinutes(10), endsAt);
        Assert.Equal(2, entries.Length); // nothing removed by a read

        Assert.False(PlayerPenaltyManager.IsPenalized([entries[0]], 1, now, out DateTime? _));
        Assert.True(PlayerPenaltyManager.IsPenalized([new PlayerPenaltyManager.Entry(now.AddYears(-1), 0, false)], 1, now, out DateTime? _)); // permanent
    }

    [Fact]
    public void TimeMode0_PassedEntryIsInactiveAndPermanentStaysActive()
    {
        var now = DateTime.UtcNow;
        Assert.False(PlayerPenaltyManager.IsPenalized([new PlayerPenaltyManager.Entry(now, 5, true)], 0, now, out DateTime? _));
        Assert.True(PlayerPenaltyManager.IsPenalized([new PlayerPenaltyManager.Entry(now.AddYears(-1), 5, false)], 0, now, out DateTime? _));
        Assert.True(PlayerPenaltyManager.IsPenalized([new PlayerPenaltyManager.Entry(now, 0, true)], 0, now, out DateTime? _));
    }

    [Fact]
    public void RemoveExpired_DropsEmptyTypesAndSlots()
    {
        var now = new DateTime(2026, 10, 7, 12, 0, 0);
        PlayerPenaltyManager.AddPenalty(1, PenaltyType.Mute, now.AddMinutes(-1), 5);
        PlayerPenaltyManager.AddPenalty(2, PenaltyType.Mute, now.AddMinutes(-1), 5);
        PlayerPenaltyManager.AddPenalty(2, PenaltyType.Gag, now.AddMinutes(30), 30);

        PlayerPenaltyManager.RemoveExpiredPenalties(1, now);

        Assert.False(PlayerPenaltyManager.IsSlotInPenalties(1)); // old code kept the empty slot forever
        Assert.True(PlayerPenaltyManager.IsSlotInPenalties(2));
        Assert.False(PlayerPenaltyManager.GetAllPlayerPenalties(2).ContainsKey(PenaltyType.Mute));
    }

    [Fact]
    public void ApiResultIsACopy()
    {
        PlayerPenaltyManager.AddPenalty(4, PenaltyType.Gag, DateTime.UtcNow.AddHours(1), 60);
        var copy = PlayerPenaltyManager.GetAllPlayerPenalties(4);
        copy[PenaltyType.Gag].Clear();
        copy.Clear();
        Assert.Single(PlayerPenaltyManager.GetPlayerPenalties(4, PenaltyType.Gag));
    }

    [Fact]
    public void MarkPassed_MatchesDbSecondPrecision()
    {
        // In-game penalties carry sub-second ends; the DB returns whole seconds
        var inGame = new DateTime(2026, 10, 7, 12, 0, 5, 734);
        PlayerPenaltyManager.AddPenalty(5, PenaltyType.Mute, inGame, 10);
        PlayerPenaltyManager.RemovePenaltiesByDateTime(5, new DateTime(2026, 10, 7, 12, 0, 5));
        Assert.True(PlayerPenaltyManager.GetPlayerPenalties(5, PenaltyType.Mute)[0].Passed);
    }

    [Fact]
    public void ChatFastPathSeesNoPenaltyWithoutAllocating()
    {
        PlayerPenaltyManager.HasAnyPenalty(7, PenaltyType.Gag, PenaltyType.Silence); // JIT
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            PlayerPenaltyManager.HasAnyPenalty(7, PenaltyType.Gag, PenaltyType.Silence);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
