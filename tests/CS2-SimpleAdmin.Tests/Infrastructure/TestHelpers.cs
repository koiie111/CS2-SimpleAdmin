using System.Collections.Concurrent;
using System.Data.Common;
using CS2_SimpleAdmin.Database;
using CS2_SimpleAdmin.Infrastructure;
using MySqlConnector;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// Stand-in for the engine's world-update queue: actions scheduled by the dispatcher run only when the test
/// "advances a world update", on the test thread (playing the game thread).
/// </summary>
internal sealed class FakeWorldUpdates
{
    private readonly ConcurrentQueue<Action> _queue = new();
    public int Scheduled;

    public void Schedule(Action action)
    {
        Interlocked.Increment(ref Scheduled);
        _queue.Enqueue(action);
    }

    /// <summary>Runs what is queued now (one engine world update). Returns how many actions ran.</summary>
    public int RunOneUpdate()
    {
        var n = _queue.Count;
        for (var i = 0; i < n && _queue.TryDequeue(out var a); i++) a();
        return n;
    }

    public int Pending => _queue.Count;

    /// <summary>Pumps updates until <paramref name="done"/> or timeout (background work may still be producing).</summary>
    public async Task PumpUntil(Func<bool> done, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("condition not reached");
            RunOneUpdate();
            await Task.Delay(1);
        }
    }

    public async Task PumpWhile(Task task, int timeoutMs = 20_000)
    {
        await PumpUntil(() => task.IsCompleted, timeoutMs);
        await task;
    }

    public async Task<T> PumpWhile<T>(Task<T> task, int timeoutMs = 20_000)
    {
        await PumpUntil(() => task.IsCompleted, timeoutMs);
        return await task;
    }
}

/// <summary>A database under test: SQLite file or a scratch MySQL/MariaDB database.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public required string Name { get; init; }
    public required IDatabaseProvider Provider { get; init; }
    public required bool IsSqlite { get; init; }
    public string? ServerConnectionString { get; init; }
    public string? DatabaseName { get; init; }
    public string? FilePath { get; init; }

    public override string ToString() => Name;

    public async Task<DbConnection> OpenAsync() => await Provider.CreateConnectionAsync();

    /// <summary>Runs the plugin's own migration scripts (MySQL or SQLite set) against this database.</summary>
    public async Task MigrateAsync()
    {
        CS2_SimpleAdmin.DatabaseProvider = Provider;
        var dir = Path.Combine(AppContext.BaseDirectory, "Migrations", IsSqlite ? "Sqlite" : "Mysql");
        if (Provider is MySqlDatabaseProvider mysql)
            await mysql.RunMigrationsAsync(dir); // same path as the plugin (user variables enabled for migrations)
        else
            await new Migration(dir).ExecuteMigrationsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        CS2_SimpleAdmin.DatabaseProvider = null;
        if (IsSqlite)
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            try { File.Delete(FilePath!); } catch { /* best effort */ }
            return;
        }

        await using var conn = new MySqlConnection(ServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{DatabaseName}`";
        await cmd.ExecuteNonQueryAsync();
    }
}

public static class TestDatabases
{
    /// <summary>Scratch servers started from the OSPanel binaries on non-standard ports (see report). Override with SA_TEST_MYSQL="name=host:port;…".</summary>
    private static readonly (string Name, string Host, int Port)[] Servers =
        ParseEnv() ?? [("MySQL-5.7", "127.0.0.1", 33057), ("MySQL-8.0", "127.0.0.1", 33080), ("MariaDB-10.11", "127.0.0.1", 33110)];

    private static (string, string, int)[]? ParseEnv()
    {
        var env = Environment.GetEnvironmentVariable("SA_TEST_MYSQL");
        if (string.IsNullOrWhiteSpace(env)) return null;
        return env.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e =>
        {
            var (name, rest) = (e.Split('=')[0], e.Split('=')[1]);
            var parts = rest.Split(':');
            return (name, parts[0], int.Parse(parts[1]));
        }).ToArray();
    }

    private static readonly Lazy<List<(string Name, string Host, int Port)>> Reachable = new(() =>
        Servers.Where(s =>
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                return c.ConnectAsync(s.Host, s.Port).Wait(500) && c.Connected;
            }
            catch
            {
                return false;
            }
        }).ToList());

    /// <summary>Placeholder theory row used when no MySQL/MariaDB server is reachable: tests turn it into a visible skip.</summary>
    public const string NoServer = "<no MySQL/MariaDB reachable>";

    /// <summary>
    /// CI sets SA_REQUIRE_MYSQL=1 for the dedicated MySQL/MariaDB job: there, an unreachable configured server is a
    /// failure (see <see cref="RequiredServersAreReachable"/>), never a silent SQLite-only pass.
    /// </summary>
    public static bool MySqlRequired => Environment.GetEnvironmentVariable("SA_REQUIRE_MYSQL") == "1";

    public static IEnumerable<string> Unreachable => Servers.Select(s => s.Name).Except(Reachable.Value.Select(s => s.Name));

    public static IEnumerable<object[]> All()
    {
        yield return ["SQLite"];
        foreach (var s in Reachable.Value) yield return [s.Name];
    }

    /// <summary>One row per reachable server, or a single <see cref="NoServer"/> row (never an empty set).</summary>
    public static IEnumerable<object[]> MySqlOnly() =>
        Reachable.Value.Count == 0
            ? [[NoServer]]
            : Reachable.Value.Select(s => new object[] { s.Name });

    /// <summary>Skips the calling test when the row is the "no server" placeholder.</summary>
    public static void SkipIfNoServer(string engine) =>
        Xunit.Skip.If(engine == NoServer, "No MySQL/MariaDB server reachable (set SA_TEST_MYSQL=\"name=host:port;…\"); SQLite-only run does NOT verify MySQL/MariaDB.");

    /// <summary>Fails (not skips) when SA_REQUIRE_MYSQL=1 and a configured server cannot be reached.</summary>
    public static void RequiredServersAreReachable()
    {
        var missing = Unreachable.ToList();
        Assert.True(missing.Count == 0, "Required MySQL/MariaDB servers are not reachable: " + string.Join(", ", missing));
    }

    public static async Task<TestDatabase> CreateAsync(string name, bool migrate = true)
    {
        TestDatabase db;
        if (name == "SQLite")
        {
            var path = Path.Combine(Path.GetTempPath(), $"sa_test_{Guid.NewGuid():N}.sqlite");
            db = new TestDatabase { Name = name, Provider = new SqliteDatabaseProvider(path), IsSqlite = true, FilePath = path };
        }
        else
        {
            var s = Reachable.Value.Single(x => x.Name == name);
            var dbName = $"sa_test_{Guid.NewGuid():N}"[..24];
            var server = new MySqlConnectionStringBuilder
            {
                Server = s.Host, Port = (uint)s.Port, UserID = "root", Pooling = true, ConvertZeroDateTime = true,
                SslMode = MySqlSslMode.None, // loopback test containers; MySQL 5.7 cannot complete a handshake with current OpenSSL
                ConnectionTimeout = 10, DefaultCommandTimeout = 60
            };
            await using (var conn = new MySqlConnection(server.ConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE `{dbName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci";
                await cmd.ExecuteNonQueryAsync();
            }

            var dbConn = new MySqlConnectionStringBuilder(server.ConnectionString) { Database = dbName };
            db = new TestDatabase
            {
                Name = name, Provider = new MySqlDatabaseProvider(dbConn.ConnectionString), IsSqlite = false,
                ServerConnectionString = server.ConnectionString, DatabaseName = dbName
            };
        }

        if (migrate) await db.MigrateAsync();
        return db;
    }
}
