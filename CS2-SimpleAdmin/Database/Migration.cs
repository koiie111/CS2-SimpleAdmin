using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin.Database;

public class Migration(string migrationsPath, Func<Task<DbConnection>>? connectionFactory = null, bool useNamedLock = false)
{
    /// <summary>MySQL named lock that serialises migrations of several servers sharing one database.</summary>
    internal const string LockName = "cs2_simpleadmin_migrations";

    /// <summary>Delay before the single retry of a failed script (another server may be applying it right now).</summary>
    internal static TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Executes all migration scripts found in the configured migrations path that have not been applied yet.
    /// Creates a migration tracking table if it does not exist.
    /// Applies migration scripts in filename order and logs successes or failures.
    /// A failed script stops the run and is rethrown as <see cref="MigrationFailedException"/>, so the caller does not
    /// treat a partially migrated database as ready (later scripts and queries may depend on the failed one).
    /// </summary>
    public async Task ExecuteMigrationsAsync()
    {
        if (CS2_SimpleAdmin.DatabaseProvider == null) return;
        var files = Directory.GetFiles(migrationsPath, "*.sql").OrderBy(f => f).ToList();
        if (files.Count == 0) return;

        await using var connection = connectionFactory != null
            ? await connectionFactory()
            : await CS2_SimpleAdmin.DatabaseProvider.CreateConnectionAsync();
        await using (var cmd = connection.CreateCommand())
        {
            if (migrationsPath.Contains("sqlite", StringComparison.CurrentCultureIgnoreCase))
            {
                cmd.CommandText = """
                                                  CREATE TABLE IF NOT EXISTS sa_migrations (
                                                      id INTEGER PRIMARY KEY AUTOINCREMENT,
                                                      version TEXT NOT NULL
                                                  );
                                              
                                  """;
            }
            else
            {
                cmd.CommandText = """
                                      CREATE TABLE IF NOT EXISTS sa_migrations (
                                          id INT PRIMARY KEY AUTO_INCREMENT,
                                          version VARCHAR(128) NOT NULL
                                      );
                                  """;
            }

            await cmd.ExecuteNonQueryAsync();
        }

        // Upstream scripts are not all re-runnable (e.g. 005 ADD COLUMN). Without this lock two servers starting on the
        // same database could both apply the same script and one of them fail. The version is read after the lock,
        // so the second server skips what the first one applied. Released explicitly and when the session ends.
        if (useNamedLock)
        {
            await using var lockCmd = connection.CreateCommand();
            lockCmd.CommandText = $"SELECT GET_LOCK('{LockName}', 600)";
            var acquired = Convert.ToInt64(await lockCmd.ExecuteScalarAsync() ?? 0L);
            if (acquired != 1) throw new MigrationFailedException("(lock)", new TimeoutException("could not acquire the migration lock"));
        }

        try
        {
            await ApplyPendingAsync(connection, files);
        }
        finally
        {
            if (useNamedLock)
            {
                await using var unlockCmd = connection.CreateCommand();
                unlockCmd.CommandText = $"SELECT RELEASE_LOCK('{LockName}')";
                try { await unlockCmd.ExecuteScalarAsync(); } catch { /* session end releases it anyway */ }
            }
        }
    }

    private async Task ApplyPendingAsync(DbConnection connection, List<string> files)
    {
        var lastAppliedVersion = await GetLastAppliedVersionAsync(connection);

        foreach (var file in files)
        {
            var version = Path.GetFileNameWithoutExtension(file);
            if (string.Compare(version, lastAppliedVersion, StringComparison.OrdinalIgnoreCase) <= 0)
                continue;

            var sqlScript = await File.ReadAllTextAsync(file);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using (var cmdMigration = connection.CreateCommand())
                    {
                        cmdMigration.CommandText = sqlScript;
                        await cmdMigration.ExecuteNonQueryAsync();
                    }

                    await UpdateLastAppliedVersionAsync(connection, version);

                    CS2_SimpleAdmin._logger?.LogInformation($"Migration \"{version}\" successfully applied.");
                    break;
                }
                catch (Exception ex) when (attempt == 1)
                {
                    // Two servers starting together can race on the same script (e.g. "duplicate key name" between the
                    // existence check and ALTER). The fork's scripts are idempotent, so one retry settles it.
                    CS2_SimpleAdmin._logger?.LogWarning($"Migration \"{version}\" failed ({ex.Message}), retrying once.");
                    await Task.Delay(RetryDelay);
                }
                catch (Exception ex)
                {
                    CS2_SimpleAdmin._logger?.LogError(ex, $"Error applying migration \"{version}\".");
                    throw new MigrationFailedException(version, ex);
                }
            }
        }
    }

    /// <summary>
    /// Retrieves the version string of the last applied migration from the database.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <returns>The version string of the last applied migration, or empty string if none.</returns>
    private static async Task<string> GetLastAppliedVersionAsync(DbConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM sa_migrations ORDER BY id DESC LIMIT 1;";
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Inserts a record tracking the successful application of a migration version.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="version">The version string of the migration applied.</param>
    private static async Task UpdateLastAppliedVersionAsync(DbConnection connection, string version)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO sa_migrations (version) VALUES (@Version);";

        var param = cmd.CreateParameter();
        param.ParameterName = "@Version";
        param.Value = version;
        cmd.Parameters.Add(param);

        await cmd.ExecuteNonQueryAsync();
    }
}


public sealed class MigrationFailedException(string version, Exception inner)
    : Exception($"Migration \"{version}\" failed: {inner.Message}", inner)
{
    public string Version { get; } = version;
}
