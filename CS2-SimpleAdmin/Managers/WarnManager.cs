using CS2_SimpleAdmin.Database;
using CS2_SimpleAdminApi;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin.Managers;

internal class WarnManager(IDatabaseProvider? databaseProvider)
{
    /// <summary>
    /// Adds a warning to a player with an optional issuer and reason.
    /// </summary>
    /// <param name="player">The player who is being warned.</param>
    /// <param name="issuer">The player issuing the warning; null indicates console or system.</param>
    /// <param name="reason">The reason for the warning.</param>
    /// <param name="time">Optional duration of the warning in minutes (0 means permanent).</param>
    /// <returns>The identifier of the inserted warning, or null if the operation failed.</returns>
    public async Task<int?> WarnPlayer(PlayerInfo player, PlayerInfo? issuer, string reason, int time = 0)
    {
        if (databaseProvider == null) return null;

        var now = Time.ActualDateTime();
        var futureTime = now.AddMinutes(time);

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();
            var sql = databaseProvider.GetAddWarnQuery(true);

            var warnId = await connection.ExecuteScalarAsync<int?>(sql, new
            {
                playerSteamid = player.SteamId.SteamId64,
                playerName = player.Name,
                adminSteamid = issuer?.SteamId.SteamId64 ?? 0,
                adminName = issuer?.Name ?? "Console", // fork: literal "Console" in DB, the site matches it
                warnReason = reason,
                duration = time,
                ends = futureTime,
                created = now,
                serverid = CS2_SimpleAdmin.ServerId
            });

            return warnId;
        }
        catch (Exception ex)
        {
            Infrastructure.RateLimitedLog.Error("warns.1", ex, "Warn database operation failed");
            return null;
        }
    }

    /// <summary>
    /// Adds a warning to a player identified by SteamID with optional issuer and reason.
    /// </summary>
    /// <param name="playerSteamId">The SteamID64 of the player being warned.</param>
    /// <param name="issuer">The player issuing the warning; null indicates console or system.</param>
    /// <param name="reason">The reason for the warning.</param>
    /// <param name="time">Optional duration of the warning in minutes (0 means permanent).</param>
    /// <returns>The identifier of the inserted warning, or null if the operation failed.</returns>
    public async Task<int?> AddWarnBySteamid(ulong playerSteamId, PlayerInfo? issuer, string reason, int time = 0)
    {
        if (databaseProvider == null) return null;

        var now = Time.ActualDateTime();
        var futureTime = now.AddMinutes(time);

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();
            var sql = databaseProvider.GetAddWarnQuery(false);

            var warnId = await connection.ExecuteScalarAsync<int?>(sql, new
            {
                playerSteamid = playerSteamId,
                adminSteamid = issuer?.SteamId.SteamId64 ?? 0,
                adminName = issuer?.Name ?? "Console", // fork: literal "Console" in DB, the site matches it
                warnReason = reason,
                duration = time,
                ends = futureTime,
                created = now,
                serverid = CS2_SimpleAdmin.ServerId
            });

            return warnId;
        }
        catch (Exception ex)
        {
            Infrastructure.RateLimitedLog.Error("warns.2", ex, "Warn database operation failed");
            return null;
        }
    }

    /// <summary>
    /// Retrieves a list of warnings for a specific player.
    /// </summary>
    /// <param name="player">The player whose warnings to retrieve.</param>
    /// <param name="active">If true, returns only active (non-expired) warnings; otherwise returns all warnings.</param>
    /// <returns>A list of dynamic objects representing warnings, or an empty list if none found or on failure.</returns>
    public async Task<List<dynamic>> GetPlayerWarns(PlayerInfo player, bool active = true)
    {
        if (databaseProvider == null) return [];

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();

            var sql = databaseProvider.GetPlayerWarnsQuery(active);
            var parameters = new { PlayerSteamID = player.SteamId.SteamId64 };
            var warns = await connection.QueryAsync<dynamic>(sql, parameters);

            return warns.ToList();
        }
        catch (Exception ex)
        {
            Infrastructure.RateLimitedLog.Error("warns.3", ex, "Warn database operation failed");
            return [];
        }
    }

    /// <summary>Warns shown per page of the css_warns menu: the game thread builds at most this many options.</summary>
    internal const int MenuPageSize = 8;

    /// <summary>
    /// One page of a player's warns for the menu (fixed page size, stable order, reason cut in SQL). Runs on a DB
    /// worker. Unlike <see cref="GetPlayerWarns"/> a database error propagates, so the caller can tell the admin.
    /// </summary>
    internal async Task<(int Total, List<Models.WarnMenuRow> Rows)> GetPlayerWarnsPageAsync(ulong steamId,
        int page, int pageSize, CancellationToken ct)
    {
        if (databaseProvider == null) return (0, []);
        page = Math.Max(1, page);
        await using var connection = await databaseProvider.CreateConnectionAsync(ct).ConfigureAwait(false);
        var total = Convert.ToInt32(await connection.ExecuteScalarAsync<object>(new CommandDefinition(
            databaseProvider.GetWarnsMenuCountQuery(), new { PlayerSteamID = steamId },
            cancellationToken: ct)).ConfigureAwait(false));
        var rows = (await connection.QueryAsync<Models.WarnMenuRow>(new CommandDefinition(
            databaseProvider.GetWarnsMenuPageQuery(),
            new { PlayerSteamID = steamId, limit = pageSize, offset = (page - 1) * pageSize },
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
        return (total, rows);
    }

    /// <summary>
    /// Retrieves the count of warnings for a player specified by SteamID.
    /// </summary>
    /// <param name="steamId">The SteamID64 of the player.</param>
    /// <param name="active">If true, counts only active (non-expired) warnings; otherwise counts all warnings.</param>
    /// <returns>The count of warnings as an integer, or 0 if none found or on failure.</returns>
    public async Task<int> GetPlayerWarnsCount(ulong steamId, bool active = true)
    {
        if (databaseProvider == null) return 0;

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();

            var sql = databaseProvider.GetPlayerWarnsCountQuery(active);
            var warnsCount = await connection.ExecuteScalarAsync<int>(sql, new { PlayerSteamID = steamId });
            return warnsCount;
        }
        catch (Exception ex)
        {
            Infrastructure.RateLimitedLog.Error("warns.4", ex, "Warn database operation failed");
            return 0;
        }
    }

    /// <summary>
    /// Removes a specific warning by its identifier from a player's record.
    /// </summary>
    /// <param name="player">The player whose warning will be removed.</param>
    /// <param name="warnId">The identifier of the warning to remove.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UnwarnPlayer(PlayerInfo player, int warnId)
    {
        if (databaseProvider == null) return;

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();

            var sql = databaseProvider.GetUnwarnByIdQuery();
            await connection.ExecuteAsync(sql, new { steamid = player.SteamId.SteamId64, warnId });
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogCritical($"Unable to remove warn + {ex}");
        }
    }
    
    /// <summary>
    /// Removes the most recent warning matching a player pattern (usually SteamID string).
    /// </summary>
    /// <param name="playerPattern">The pattern identifying the player whose last warning should be removed.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UnwarnPlayer(string playerPattern)
    {
        if (databaseProvider == null) return;

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();

            var sql = databaseProvider.GetUnwarnLastQuery();
            await connection.ExecuteAsync(sql, new { steamid = playerPattern });
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogCritical("Unable to remove last warn {exception}", ex.Message);
        }
    }

    /// <summary>
    /// Expires old warnings based on the current time, removing or marking them as inactive.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExpireOldWarns()
    {
        if (databaseProvider == null) return;

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();

            var sql = databaseProvider.GetExpireWarnsQuery();
            await connection.ExecuteAsync(sql, new { CurrentTime = Time.ActualDateTime() });
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogCritical($"Unable to remove expired warns + {ex}");
        }
    }
}