using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// F18: PlayerManager captured Instance.Config in a readonly field when the static was created (the default config
/// of a dummy instance), while other code read the live config. Checks now take the config snapshot of the
/// operation explicitly, so a reloaded config (BanType, CheckMultiAccountsByIp, TimeMode) is honoured at once.
/// </summary>
public class ConfigSnapshotTests
{
    [Fact]
    public void BanCheckUsesTheConfigOfTheOperation()
    {
        var cache = new CacheManager();
        typeof(CacheManager).GetMethod("Publish", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(cache, [BanCacheSnapshot.Create([new BanRecord { Id = 1, PlayerIp = "5.5.5.5", Status = "ACTIVE" }], IpHistoryIndex.Empty, [])]);

        var steamOnly = TestConfig.Use(c => c.OtherSettings.BanType = 0);
        var withIp = TestConfig.Use(c => c.OtherSettings.BanType = 1);
        var now = DateTime.UtcNow;

        Assert.False(cache.CheckBan(steamOnly, 1, "5.5.5.5", now).IsBanned);
        Assert.True(cache.CheckBan(withIp, 1, "5.5.5.5", now).IsBanned);
    }

    [Fact]
    public void PenaltyRulesFollowTheCurrentConfig()
    {
        PlayerPenaltyManager.RemoveAllPenalties();
        PlayerPenaltyManager.AddPenalty(9, CS2_SimpleAdminApi.PenaltyType.Mute, DateTime.UtcNow.AddYears(-1), 5); // ends long ago

        TestConfig.Use(c => { c.OtherSettings.TimeMode = 1; c.DatabaseConfig.DatabaseType = "SQLite"; });
        Assert.False(PlayerPenaltyManager.IsPenalized(9, CS2_SimpleAdminApi.PenaltyType.Mute, out DateTime? _)); // real time: expired

        TestConfig.Use(c => { c.OtherSettings.TimeMode = 0; c.DatabaseConfig.DatabaseType = "SQLite"; });
        Assert.True(PlayerPenaltyManager.IsPenalized(9, CS2_SimpleAdminApi.PenaltyType.Mute, out DateTime? _)); // online time: not passed yet
        PlayerPenaltyManager.RemoveAllPenalties();
    }
}
