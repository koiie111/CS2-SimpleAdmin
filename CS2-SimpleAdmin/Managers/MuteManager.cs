using CS2_SimpleAdmin.Database;
using CS2_SimpleAdminApi;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin.Managers;

internal class MuteManager(IDatabaseProvider? databaseProvider)
{
    /// <summary>
    /// Adds a mute entry for a specified player with detailed information.
    /// </summary>
    /// <param name="player">Player to be muted.</param>
    /// <param name="issuer">Admin issuing the mute; null if issued from console.</param>
    /// <param name="reason">Reason for muting the player.</param>
    /// <param name="time">Duration of the mute in minutes. Zero means permanent mute.</param>
    /// <param name="type">Mute type: 0 = GAG, 1 = MUTE, 2 = SILENCE.</param>
    /// <returns>Mute ID if successfully added, otherwise null.</returns>
    public async Task<int?> MutePlayer(PlayerInfo player, PlayerInfo? issuer, string reason, int time = 0, int type = 0)
    {
        if (databaseProvider == null) return null;

        var now = Time.ActualDateTime();
        var futureTime = now.AddMinutes(time);

        var muteType = type switch
        {
            1 => "MUTE",
            2 => "SILENCE",
            _ => "GAG"
        };

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();
            var sql = databaseProvider.GetAddMuteQuery(true);

            var muteId = await connection.ExecuteScalarAsync<int?>(sql, new
            {
                playerSteamid = player.SteamId.SteamId64,
                playerName = player.Name,
                adminSteamid = issuer?.SteamId.SteamId64 ?? 0,
                adminName = issuer?.Name ?? "Console", // fork: literal "Console" in DB, the site matches it
                muteReason = reason,
                duration = time,
                ends = futureTime,
                created = now,
                type = muteType,
                serverid = CS2_SimpleAdmin.ServerId
            });

            return muteId;
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogError(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Adds a mute entry for a offline player identified by their SteamID.
    /// </summary>
    /// <param name="playerSteamId">SteamID64 of the player to mute.</param>
    /// <param name="issuer">Admin issuing the mute; can be null if from console.</param>
    /// <param name="reason">Reason for the mute.</param>
    /// <param name="time">Mute duration in minutes; 0 for permanent.</param>
    /// <param name="type">Mute type: 0 = GAG, 1 = MUTE, 2 = SILENCE.</param>
    /// <returns>Mute ID if successful, otherwise null.</returns>
    public async Task<int?> AddMuteBySteamid(ulong playerSteamId, PlayerInfo? issuer, string reason, int time = 0, int type = 0)
    {
        if (databaseProvider == null) return null;

        var now = Time.ActualDateTime();
        var futureTime = now.AddMinutes(time);

        var muteType = type switch
        {
            1 => "MUTE",
            2 => "SILENCE",
            _ => "GAG"
        };

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();
            var sql = databaseProvider.GetAddMuteQuery(false);
            
            var muteId = await connection.ExecuteScalarAsync<int?>(sql, new
            {
                playerSteamid = playerSteamId,
                adminSteamid = issuer?.SteamId.SteamId64 ?? 0,
                adminName = issuer?.Name ?? "Console", // fork: literal "Console" in DB, the site matches it
                muteReason = reason,
                duration = time,
                ends = futureTime,
                created = now,
                type = muteType,
                serverid = CS2_SimpleAdmin.ServerId
            });

            return muteId;
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogError("Unable to add mute for {SteamId}: {Error}", playerSteamId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Checks if a player with the given SteamID currently has any active mutes.
    /// </summary>
    /// <param name="steamId">SteamID64 of the player to check.</param>
    /// <returns>List of active mute records; empty list if none or on error.</returns>
    public async Task<List<dynamic>> IsPlayerMuted(string steamId)
    {
        if (databaseProvider == null) return [];

        if (string.IsNullOrEmpty(steamId))
        {
            return [];
        }

#if DEBUG
        if (CS2_SimpleAdmin._logger != null)
            CS2_SimpleAdmin._logger.LogCritical($"IsPlayerMuted for {steamId}");
#endif

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();
            var currentTime = Time.ActualDateTime();
            
            var sql = databaseProvider.GetIsMutedQuery(CS2_SimpleAdmin.CurrentConfig.OtherSettings.TimeMode);
            
            var parameters = new { PlayerSteamID = steamId, CurrentTime = currentTime };
            var activeMutes = (await connection.QueryAsync(sql, parameters)).ToList();
            return activeMutes;
        }
        catch (Exception ex)
        {
            Infrastructure.RateLimitedLog.Error("mutes.active", ex, "Unable to read active mutes");
            return [];
        }
    }

    /// <summary>
    /// Retrieves counts of total mutes, gags, and silences for a given player.
    /// </summary>
    /// <param name="playerInfo">Information about the player.</param>
    /// <returns>
    /// Tuple containing total mutes, total gags, and total silences respectively.
    /// Returns zeros if no data or on error.
    /// </returns>
    /// <remarks>
    /// Uses the stats query (counts per type). It used to run the unmute lookup query, which needs @pattern and
    /// @muteType, so it always threw and silently returned zeros. Errors now propagate to the caller.
    /// </remarks>
    public async Task<(int TotalMutes, int TotalGags, int TotalSilences)> GetPlayerMutes(PlayerInfo playerInfo)
    {
        if (databaseProvider == null) return (0,0,0);

        await using var connection = await databaseProvider.CreateConnectionAsync();
        var sql = databaseProvider.GetMuteStatsQuery();
        var result = await connection.QuerySingleAsync<Models.MuteStats>(sql, new
        {
            PlayerSteamID = playerInfo.SteamId.SteamId64
        });

        return ((int)result.TotalMutes, (int)result.TotalGags, (int)result.TotalSilences);
    }

    /// <summary>Historic totals (bans, mutes, gags, silences, warns) of a player in one query.</summary>
    internal async Task<Models.PlayerPenaltyStats> GetPlayerPenaltyStatsAsync(ulong steamId, CancellationToken ct)
    {
        if (databaseProvider == null) return new Models.PlayerPenaltyStats();
        await using var connection = await databaseProvider.CreateConnectionAsync(ct);
        return await connection.QuerySingleAsync<Models.PlayerPenaltyStats>(new CommandDefinition(
            databaseProvider.GetPlayerPenaltyStatsQuery(),
            new { PlayerSteamID = steamId }, cancellationToken: ct));
    }

    /// <summary>
    /// Active gags/mutes/silences of a player (typed; same rules as <see cref="IsPlayerMuted"/>), from any server.
    /// A failed read throws: an exception is never the same answer as "no active mutes".
    /// </summary>
    internal async Task<List<Models.ActiveMuteRow>> GetActiveMutesAsync(ulong steamId, int timeMode, DateTime now,
        CancellationToken ct)
    {
        var all = await GetActiveMutesBatchAsync([steamId], timeMode, now, ct).ConfigureAwait(false);
        return all[steamId];
    }

    /// <summary>Players per active-mutes statement (keeps the IN list and the result bounded).</summary>
    internal const int ActiveMutesBatchSize = 64;

    /// <summary>
    /// Active mutes of several players in as few statements as possible (<see cref="ActiveMutesBatchSize"/> per
    /// statement, one connection). Every requested SteamID is a key of the result: an empty list is a <b>successful</b>
    /// "nothing active"; a failed read throws and yields no result at all, so the two can never be confused.
    /// </summary>
    internal async Task<Dictionary<ulong, List<Models.ActiveMuteRow>>> GetActiveMutesBatchAsync(IReadOnlyList<ulong> steamIds,
        int timeMode, DateTime now, CancellationToken ct)
    {
        var result = new Dictionary<ulong, List<Models.ActiveMuteRow>>(steamIds.Count);
        foreach (var id in steamIds) result[id] = [];
        if (databaseProvider == null) throw new InvalidOperationException("no database");
        if (steamIds.Count == 0) return result;

        await using var connection = await databaseProvider.CreateConnectionAsync(ct).ConfigureAwait(false);
        var sql = databaseProvider.GetActiveMutesBatchQuery(timeMode);
        var distinct = result.Keys.ToList();
        for (var i = 0; i < distinct.Count; i += ActiveMutesBatchSize)
        {
            var ids = distinct.GetRange(i, Math.Min(ActiveMutesBatchSize, distinct.Count - i));
            var rows = await connection.QueryAsync<Models.ActiveMuteRow>(new CommandDefinition(sql,
                new { ids, CurrentTime = now }, cancellationToken: ct)).ConfigureAwait(false);
            foreach (var row in rows)
                if (result.TryGetValue((ulong)row.SteamId, out var list))
                    list.Add(row);
        }

        return result;
    }

    internal const int OnlineBatchSize = 64;

    /// <summary>Planned mute updates per compare-and-set statement (keeps statement text bounded).</summary>
    internal const int StepsPerStatement = 200;

    /// <summary>Test seam: called at named points of <see cref="CheckOnlineModeMutesAsync"/> to inject faults.</summary>
    internal Func<string, Task>? FaultHook { get; set; }

    private Task Hit(string point) => FaultHook?.Invoke(point) ?? Task.CompletedTask;

    /// <summary>
    /// TimeMode 0: credits online minutes to the active timed mutes of online players and returns the mutes whose
    /// online time is now used up. Runs on a DB worker; the caller applies the result on the game thread.
    /// <para>
    /// Safe to call again with the <b>same</b> <see cref="OnlineCredit"/> objects after any failure, cancellation or
    /// lost acknowledgement: planning happens once per credit, every write is a compare-and-set against the planned
    /// pre-image (see <see cref="OnlineCredit"/>), so a mute is never credited twice. Set-based: one plan SELECT, one
    /// transaction of CAS updates and one expiry SELECT per batch of <see cref="OnlineBatchSize"/>.
    /// </para>
    /// </summary>
    internal async Task<List<Models.ExpiredOnlineMuteRow>> CheckOnlineModeMutesAsync(
        IReadOnlyList<OnlineCredit> credits, CancellationToken ct)
    {
        var expired = new List<Models.ExpiredOnlineMuteRow>();
        if (databaseProvider == null || credits.Count == 0) return expired;

        await using var connection = await databaseProvider.CreateConnectionAsync(ct);

        // 1) Plan: fix the pre/post image once. A credit that already has a plan keeps it.
        var unplanned = credits.Where(c => c.Plan == null).ToList();
        if (unplanned.Count > 0)
        {
            var planSql = databaseProvider.GetOnlineCreditPlanQuery();
            var ids = unplanned.Select(c => c.SteamId).Distinct().ToList();
            for (var i = 0; i < ids.Count; i += OnlineBatchSize)
            {
                var batchIds = ids.GetRange(i, Math.Min(OnlineBatchSize, ids.Count - i));
                var rows = (await connection.QueryAsync<Models.OnlineCreditPlanRow>(new CommandDefinition(planSql,
                    new { ids = batchIds }, cancellationToken: ct))).AsList();
                foreach (var credit in unplanned)
                {
                    if (!batchIds.Contains(credit.SteamId)) continue;
                    var steps = new List<OnlineCreditStep>();
                    foreach (var row in rows)
                    {
                        if ((ulong)row.SteamId != credit.SteamId || row.Passed >= row.Duration) continue;
                        var minutes = credit.EligibleMinutes(row.Created);
                        if (minutes > 0) steps.Add(new OnlineCreditStep(row.Id, row.Passed, row.Passed + minutes));
                    }

                    credit.Plan = steps;
                }
            }
        }

        await Hit("after-plan");

        // 2) Apply: compare-and-set per planned step, one transaction per batch of credits
        var pending = new List<OnlineCredit>();
        foreach (var credit in credits)
        {
            if (credit.Applied) continue;
            if (credit.Plan!.Count == 0) credit.Applied = true;
            else pending.Add(credit);
        }

        for (var i = 0; i < pending.Count; i += OnlineBatchSize)
        {
            var batch = pending.GetRange(i, Math.Min(OnlineBatchSize, pending.Count - i));
            var steps = batch.SelectMany(c => c.Plan!).ToList();
            await using (var transaction = await connection.BeginTransactionAsync(ct))
            {
                // One compare-and-set statement per StepsPerStatement planned steps (normally one per batch)
                for (var s = 0; s < steps.Count; s += StepsPerStatement)
                    await connection.ExecuteAsync(new CommandDefinition(
                        databaseProvider.GetApplyOnlineCreditQuery(steps.GetRange(s, Math.Min(StepsPerStatement, steps.Count - s))),
                        transaction: transaction, cancellationToken: ct));
                await Hit("before-commit");
                await transaction.CommitAsync(ct);
            }

            // Between commit and this mark the acknowledgement can be lost: the retry replays the CAS steps, which
            // are no-ops for everything that did commit.
            await Hit("after-commit");
            foreach (var credit in batch) credit.Applied = true;
        }

        // 3) Which of these players' mutes are now used up (read-only, repeatable)
        var select = databaseProvider.GetExpiredOnlineMutesBatchQuery();
        var all = credits.Select(c => c.SteamId).Distinct().ToList();
        for (var i = 0; i < all.Count; i += OnlineBatchSize)
        {
            var batch = all.GetRange(i, Math.Min(OnlineBatchSize, all.Count - i));
            expired.AddRange(await connection.QueryAsync<Models.ExpiredOnlineMuteRow>(new CommandDefinition(select,
                new { ids = batch }, cancellationToken: ct)));
        }

        await Hit("after-select");
        return expired;
    }

    /// <summary>
    /// Removes active mutes for players matching the specified pattern.
    /// </summary>
    /// <param name="playerPattern">Pattern to match player names or identifiers.</param>
    /// <param name="adminSteamId">SteamID64 of the admin performing the unmute.</param>
    /// <param name="reason">Reason for unmuting the player(s).</param>
    /// <param name="type">Mute type to remove: 0 = GAG, 1 = MUTE, 2 = SILENCE.</param>
    /// <returns>
    /// <see cref="UnmuteOutcome.Removed"/> / <see cref="UnmuteOutcome.NothingActive"/> when the statements ran,
    /// <see cref="UnmuteOutcome.Failed"/> when the database is unavailable or an exception occurred (logged).
    /// </returns>
    public async Task<UnmuteOutcome> UnmutePlayer(string playerPattern, string adminSteamId, string reason, int type = 0)
    {
        if (databaseProvider == null) return UnmuteOutcome.Failed;

        if (playerPattern.Length <= 1)
        {
            return UnmuteOutcome.NothingActive;
        }

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();
            var muteType = type switch
            {
                1 => "MUTE",
                2 => "SILENCE",
                _ => "GAG"
            };

            var sqlRetrieveMutes =
                databaseProvider.GetRetrieveMutesQuery();
            // Typed on purpose: `dynamic` rows made `int muteId = mute.id` throw on SQLite (INTEGER arrives as Int64), and
            // the swallowed exception meant the penalty was never removed there
            var mutesList = (await connection.QueryAsync<long>(sqlRetrieveMutes,
                new { pattern = playerPattern, muteType })).ToArray();
            if (mutesList.Length == 0)
                return UnmuteOutcome.NothingActive;

            var sqlAdmin = databaseProvider.GetUnmuteAdminIdQuery();
            var sqlInsertUnmute = databaseProvider.GetInsertUnmuteQuery(string.IsNullOrEmpty(reason));

            var sqlAdminId = await connection.ExecuteScalarAsync<int?>(sqlAdmin, new { adminSteamId });
            var adminId = sqlAdminId ?? 0;

            foreach (var muteId in mutesList)
            {

                var unmuteId =
                    await connection.ExecuteScalarAsync<long>(sqlInsertUnmute, new { muteId, adminId, reason });

                var sqlUpdateMute = databaseProvider.GetUpdateMuteStatusQuery();
                await connection.ExecuteAsync(sqlUpdateMute, new { unmuteId, muteId });
            }

            return UnmuteOutcome.Removed;
        }
        catch (Exception ex)
        {
            Infrastructure.RateLimitedLog.Error("unmute.sql", ex, "Unable to remove the penalty");
            return UnmuteOutcome.Failed;
        }
    }

    /// <summary>
    /// Expires all old mutes that have passed their duration according to current time.
    /// </summary>
    /// <returns>Task representing the asynchronous expiration operation.</returns>
    public async Task ExpireOldMutes()
    {
        if (databaseProvider == null) return;

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();
            var sql = databaseProvider.GetExpireMutesQuery(CS2_SimpleAdmin.CurrentConfig.OtherSettings.TimeMode);
            await connection.ExecuteAsync(sql, new { CurrentTime = Time.ActualDateTime() });
        }
        catch (Exception ex)
        {
            Infrastructure.RateLimitedLog.Error("expire.mutes", ex, "Unable to remove expired mutes");
        }
    }
}