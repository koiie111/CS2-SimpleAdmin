using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;

namespace CS2_SimpleAdmin.Tests;

public class IpHistoryIndexTests
{
    /// <summary>Incremental updates (overlay + automatic compaction) must equal a full rebuild from all rows.</summary>
    [Theory]
    [InlineData(200, 50)]
    [InlineData(60_000, 2_000)] // crosses CompactThreshold → exercises Compact()
    public void IncrementalEqualsFullRebuild(int baseRows, int batch)
    {
        var rnd = new Random(baseRows);
        var t0 = new DateTime(2026, 1, 1);
        IpHistoryRow Row() => new()
        {
            Steamid = 76561198000000000L + rnd.Next(0, baseRows / 2 + 10), Address = (uint)rnd.Next(0, baseRows / 3 + 10),
            Used_at = t0.AddSeconds(rnd.Next(0, 10_000_000)), Name = "n" + rnd.Next(0, 5)
        };

        var all = Enumerable.Range(0, baseRows).Select(_ => Row()).ToList();
        var index = IpHistoryIndex.Build(all, "Unknown");
        for (var round = 0; round < 40; round++)
        {
            var delta = Enumerable.Range(0, batch).Select(_ => Row()).ToList();
            all.AddRange(delta);
            index = index.With(delta, "Unknown");
        }

        var reference = IpHistoryIndex.Build(all, "Unknown");
        Assert.Equal(reference.AccountCount, index.AccountCount);
        Assert.Equal(reference.AddressCount, index.AddressCount);
        foreach (var steam in all.Select(r => (ulong)r.Steamid).Distinct())
        {
            Assert.True(index.TryGetIps(steam, out var got));
            Assert.True(reference.TryGetIps(steam, out var expected));
            Assert.Equal(expected.OrderBy(r => r.Ip).Select(r => (r.Ip, r.UsedAt)), got.OrderBy(r => r.Ip).Select(r => (r.Ip, r.UsedAt)));
        }

        foreach (var ip in all.Select(r => r.Address).Distinct())
        {
            Assert.True(index.TryGetAccounts(ip, out var got));
            Assert.True(reference.TryGetAccounts(ip, out var expected));
            Assert.Equal(expected.Order(), got.Order());
        }
    }
}
