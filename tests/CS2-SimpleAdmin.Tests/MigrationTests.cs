using Dapper;

namespace CS2_SimpleAdmin.Tests;

public class MigrationTests
{
    public static IEnumerable<object[]> Engines() => TestDatabases.All();

    private static readonly string[] Expected =
    [
        "idx_sa_bans_steamid", "idx_sa_bans_status_ends", "idx_sa_bans_updated_at", "idx_sa_bans_created", "idx_sa_bans_ip",
        "idx_sa_warns_steamid", "idx_sa_warns_status_ends", "idx_sa_mutes_status_ends"
    ];

    private static async Task<HashSet<string>> Indexes(TestDatabase db)
    {
        await using var c = await db.OpenAsync();
        var sql = db.IsSqlite
            ? "SELECT name FROM sqlite_master WHERE type = 'index'"
            : "SELECT DISTINCT INDEX_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE()";
        return (await c.QueryAsync<string>(sql)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task PerformanceIndexesAreCreated(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        var indexes = await Indexes(db);
        Assert.All(Expected, name => Assert.Contains(name, indexes));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task IndexMigrationToleratesIndexesThatAlreadyExist(string engine)
    {
        // A second server may have created some of them already, or the migration may be re-run after a partial failure
        await using var db = await TestDatabases.CreateAsync(engine);
        await using (var c = await db.OpenAsync())
        {
            await c.ExecuteAsync("DELETE FROM sa_migrations WHERE version LIKE '017_ZZ%'");
            if (db.IsSqlite) await c.ExecuteAsync("DROP INDEX idx_sa_bans_ip");
            else await c.ExecuteAsync("ALTER TABLE sa_bans DROP INDEX idx_sa_bans_ip");
        }

        await db.MigrateAsync();
        Assert.All(Expected, name => Assert.Contains(name, Indexes(db).Result));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task TwoServersMigratingTheSameDatabaseAtOnceBothSucceed(string engine)
    {
        if (engine == "SQLite") return; // one plugin per SQLite file
        await using var db = await TestDatabases.CreateAsync(engine, migrate: false);
        Database.Migration.RetryDelay = TimeSpan.FromMilliseconds(200);
        await Task.WhenAll(db.MigrateAsync(), db.MigrateAsync());
        var indexes = await Indexes(db);
        Assert.All(Expected, name => Assert.Contains(name, indexes));
    }
}
