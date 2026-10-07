using System.Collections.Immutable;
using System.Data.Common;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin.Managers;

/// <summary>
/// Bans/IP cache used for connect and periodic ban checks.
/// <para>
/// Readers (any thread) read <see cref="Snapshot"/> once per operation and never block.
/// All changes are serialised by one async writer gate and published as a new immutable
/// <see cref="BanCacheSnapshot"/> with a single reference write; nothing reachable from a published snapshot is
/// ever modified. A full rebuild (startup, css_reloadbans) is built next to the current snapshot, which keeps
/// serving checks until the new one is published.
/// </para>
/// <para>
/// Incremental refresh protocol (every periodic pass):
/// <list type="number">
/// <item>Read the DB clock first (<c>dbNow</c>).</item>
/// <item>Ban rows with <c>updated_at</c>/<c>created</c> &gt;= watermark − overlap, paged by id. Any change of any
/// cached column (status, SteamID, IP, ends…) is applied; non-ACTIVE rows leave the active set.</item>
/// <item>Checksum (COUNT, SUM(id)) of ACTIVE rows vs. the new snapshot; a mismatch (deleted rows, SQLite
/// writers that do not touch updated_at, clock skew) triggers a reconcile of ACTIVE ids.</item>
/// <item>IP history rows with <c>used_at</c> &gt;= watermark − overlap, keyset-paged on (used_at, steamid, address);
/// a pass reads at most <see cref="MaxIpPagesPerRefresh"/> pages and continues from the cursor next time.</item>
/// <item>Publish, then advance the watermarks. Any failure keeps the previous snapshot and watermarks.</item>
/// </list>
/// The overlap window re-reads recent rows so commits that become visible late, equal timestamps and coarse
/// timestamp precision are not skipped; re-applying a row is idempotent.
/// </para>
/// </summary>
internal class CacheManager : IDisposable
{
    internal const int BanPageSize = 1000;
    internal const int MaxBanPagesPerRefresh = 50;
    internal const int IpPageSize = 2000;
    internal const int MaxIpPagesPerRefresh = 25;

    /// <summary>Accounts examined per refresh by the stale-IP prune sweep (bounds its CPU/allocation per pass).</summary>
    internal const int IpPruneAccountsPerRefresh = 2000;

    /// <summary>The SQL checksum of sa_players_ips is compared every N refreshes (a COUNT/SUM over the table).</summary>
    internal const int IpChecksumEveryRefreshes = 15;
    internal static readonly TimeSpan Overlap = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _writer = new(1, 1);
    private BanCacheSnapshot _snapshot = BanCacheSnapshot.Empty;
    private bool _disposed;

    // Writer-owned state (only touched while holding _writer)
    private DateTime? _banWatermark;
    private DateTime? _ipWatermark;
    private IpCursor? _ipCursor;
    private DateTime? _ipDrainStartedAt;
    private bool _needsFullRebuild;
    private ulong[]? _pruneAccounts;
    private int _pruneNext;
    private int _refreshesSinceIpChecksum;
    private bool _ipChecksumSuspect;
    internal long IpRebuilds;
    internal long IpPrunedPasses;

    internal readonly record struct IpCursor(DateTime UsedAt, long SteamId, uint Address);

    /// <summary>Current generation. One read per operation; never modified after publication.</summary>
    public BanCacheSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public bool IsInitialized => Snapshot.IsInitialized;

    private void Publish(BanCacheSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);

    // ------------------------------------------------------------------ SQL

    private static string BanColumns =>
        "id AS Id, player_name AS PlayerName, player_steamid AS PlayerSteamId, player_ip AS PlayerIp, " +
        "status AS Status, created AS Created, ends AS Ends, duration AS Duration";

    internal static string ActiveBansPageSql(bool multiServer) =>
        $"SELECT {BanColumns} FROM sa_bans WHERE status = 'ACTIVE'{(multiServer ? "" : " AND server_id = @serverId")} AND id > @afterId ORDER BY id LIMIT @limit";

    internal static string ChangedBansPageSql(bool multiServer) =>
        $"SELECT {BanColumns} FROM sa_bans WHERE (updated_at >= @since OR created >= @since){(multiServer ? "" : " AND server_id = @serverId")} AND id > @afterId ORDER BY id LIMIT @limit";

    internal static string ActiveChecksumSql(bool multiServer) =>
        $"SELECT COUNT(*) AS Cnt, COALESCE(SUM(id), 0) AS IdSum FROM sa_bans WHERE status = 'ACTIVE'{(multiServer ? "" : " AND server_id = @serverId")}";

    internal static string ActiveIdsSql(bool multiServer) =>
        $"SELECT id FROM sa_bans WHERE status = 'ACTIVE'{(multiServer ? "" : " AND server_id = @serverId")}";

    internal static string BansByIdsSql => $"SELECT {BanColumns} FROM sa_bans WHERE id IN @ids";

    internal const string IpHistoryPageSql =
        "SELECT steamid, name, address, used_at FROM sa_players_ips " +
        "WHERE used_at > @t OR (used_at = @t AND (steamid > @s OR (steamid = @s AND address > @a))) " +
        "ORDER BY used_at, steamid, address LIMIT @limit";

    /// <summary>
    /// COUNT/SUM over the IP history newer than @cutoff: compared with <see cref="IpHistoryIndex.Checksum"/>.
    /// Both sides use the same bounded arithmetic: every SteamID64 term is reduced modulo
    /// <see cref="IpHistoryIndex.SteamModulus"/> (2^31-1) before it is summed, so each term is below 2^32 (addresses
    /// are UInt32) and a sum of up to 2^31-1 rows stays below Int64.MaxValue on SQLite, MySQL and MariaDB alike
    /// (a raw SUM(steamid) overflows at 121 real SteamID64 rows). Exact, no floating point.
    /// </summary>
    internal const string IpChecksumSql =
        "SELECT COUNT(*), COALESCE(SUM(steamid % 2147483647), 0), COALESCE(SUM(address), 0) FROM sa_players_ips WHERE used_at > @cutoff";

    private static async Task<(long Count, long IdSum)> ReadChecksumAsync(DbConnection connection, bool multiServer,
        int? serverId, CancellationToken ct)
    {
        // Read as objects: MySQL returns BIGINT/DECIMAL, SQLite INTEGER
        await using var reader = await connection.ExecuteReaderAsync(new CommandDefinition(ActiveChecksumSql(multiServer),
            new { serverId }, cancellationToken: ct)).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return (0, 0);
        return (Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)));
    }

    // ------------------------------------------------------------------ full build

    /// <summary>
    /// Builds the cache from scratch and publishes it. Runs on a DB worker. The previous snapshot keeps serving
    /// readers until the new one is complete; on failure it stays in place and the exception propagates.
    /// </summary>
    public async Task InitializeCacheAsync(CS2_SimpleAdminConfig config, int? serverId, CancellationToken ct)
    {
        if (CS2_SimpleAdmin.DatabaseProvider == null || _disposed) return;
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await BuildAndPublishAsync(config, serverId, ct).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>css_reloadbans: same as the initial build, serialised with refreshes.</summary>
    public Task ForceReInitializeCacheAsync(CS2_SimpleAdminConfig config, int? serverId, CancellationToken ct) =>
        InitializeCacheAsync(config, serverId, ct);

    private async Task BuildAndPublishAsync(CS2_SimpleAdminConfig config, int? serverId, CancellationToken ct)
    {
        var start = LatencyHistogram.Now();
        await using var connection = await CS2_SimpleAdmin.DatabaseProvider!.CreateConnectionAsync(ct).ConfigureAwait(false);
        var dbNow = await GetDatabaseTimeAsync(connection, ct).ConfigureAwait(false);
        var multiServer = config.MultiServerMode;

        var bans = new List<BanRecord>();
        var afterId = 0;
        while (true)
        {
            var page = (await connection.QueryAsync<BanRecord>(new CommandDefinition(ActiveBansPageSql(multiServer),
                new { serverId, afterId, limit = BanPageSize }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
            bans.AddRange(page);
            if (page.Count < BanPageSize) break;
            afterId = page[^1].Id;
        }

        var ipHistory = IpHistoryIndex.Empty;
        if (config.OtherSettings.CheckMultiAccountsByIp)
            ipHistory = await ReadIpHistoryIndexAsync(connection, ct).ConfigureAwait(false);

        var ignored = new List<uint>();
        foreach (var ip in config.OtherSettings.IgnoredIps)
            if (IpHelper.TryConvertIpToUint(ip, out var value))
                ignored.Add(value);

        Publish(BanCacheSnapshot.Create(bans, ipHistory, ignored));
        _banWatermark = dbNow;
        _ipWatermark = dbNow;
        _ipCursor = null;
        _ipDrainStartedAt = null;
        _needsFullRebuild = false;
        PluginMetrics.CacheFullBuild.RecordSince(start);
    }

    /// <summary>
    /// Reads the whole IP history in keyset pages (async I/O, bounded transient memory) into a fresh index. Used by the
    /// full build and by the checksum-triggered rebuild, so both give the same result.
    /// </summary>
    private static async Task<IpHistoryIndex> ReadIpHistoryIndexAsync(DbConnection connection, CancellationToken ct)
    {
        var builder = new IpIndexBuilder(UnknownName());
        var cursor = new IpCursor(DateTime.MinValue, -1, 0);
        while (true)
        {
            var page = (await connection.QueryAsync<IpHistoryRow>(new CommandDefinition(IpHistoryPageSql,
                new { t = cursor.UsedAt, s = cursor.SteamId, a = cursor.Address, limit = IpPageSize },
                cancellationToken: ct)).ConfigureAwait(false)).AsList();
            builder.Add(page);
            if (page.Count < IpPageSize) break;
            var last = page[^1];
            cursor = new IpCursor(last.Used_at, last.Steamid, last.Address);
        }

        return builder.Build();
    }

    // ------------------------------------------------------------------ incremental refresh

    /// <summary>Applies database changes since the last successful refresh (see class remarks).</summary>
    public async Task RefreshCacheAsync(CS2_SimpleAdminConfig config, int? serverId, CancellationToken ct)
    {
        if (CS2_SimpleAdmin.DatabaseProvider == null || _disposed) return;
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Snapshot.IsInitialized || _needsFullRebuild || _banWatermark == null)
            {
                await BuildAndPublishAsync(config, serverId, ct).ConfigureAwait(false);
                return;
            }

            var start = LatencyHistogram.Now();
            await using var connection = await CS2_SimpleAdmin.DatabaseProvider.CreateConnectionAsync(ct).ConfigureAwait(false);
            var dbNow = await GetDatabaseTimeAsync(connection, ct).ConfigureAwait(false);
            var multiServer = config.MultiServerMode;
            var snapshot = Snapshot;

            // 1) changed bans, paged by id
            var since = _banWatermark.Value - Overlap;
            var changed = new List<BanRecord>();
            var afterId = 0;
            for (var pageNo = 0; ; pageNo++)
            {
                if (pageNo >= MaxBanPagesPerRefresh)
                {
                    // Mass change (import, bulk edit on the site): a bounded full rebuild is cheaper and exact
                    _needsFullRebuild = true;
                    RateLimitedLog.Warning("cache.refresh.mass", $"More than {MaxBanPagesPerRefresh * BanPageSize} changed bans; scheduling a full rebuild");
                    return;
                }

                var page = (await connection.QueryAsync<BanRecord>(new CommandDefinition(ChangedBansPageSql(multiServer),
                    new { since, serverId, afterId, limit = BanPageSize }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
                changed.AddRange(page);
                if (page.Count < BanPageSize) break;
                afterId = page[^1].Id;
            }

            var next = snapshot.WithBans(FilterRealChanges(snapshot, changed));

            // 2) checksum of the ACTIVE set → reconcile deletions / invisible changes
            var (dbCount, dbIdSum) = await ReadChecksumAsync(connection, multiServer, serverId, ct).ConfigureAwait(false);
            if (dbCount != next.ActiveCount || dbIdSum != next.ActiveIdSum)
                next = await ReconcileAsync(connection, next, multiServer, serverId, ct).ConfigureAwait(false);

            // 3) IP history: delta in, stale/deleted links out
            var ipDrained = true;
            DateTime? lastIpUsedAt = null;
            var ipRebuilt = false;
            if (config.OtherSettings.CheckMultiAccountsByIp)
            {
                (next, ipDrained, lastIpUsedAt) = await RefreshIpHistoryAsync(connection, next, ct).ConfigureAwait(false);
                next = PruneStaleIpHistory(next, config.OtherSettings.ExpireOldIpBans);
                (next, ipRebuilt) = await ReconcileIpHistoryAsync(connection, next, config.OtherSettings.ExpireOldIpBans, ct)
                    .ConfigureAwait(false);
            }

            // 4) publish, then advance watermarks
            Publish(next);
            _banWatermark = dbNow;
            if (ipRebuilt)
            {
                // The index was rebuilt from everything SQL holds right now: nothing older is pending
                _ipWatermark = dbNow;
                _ipDrainStartedAt = null;
                _ipCursor = null;
            }
            else if (config.OtherSettings.CheckMultiAccountsByIp)
            {
                if (ipDrained)
                {
                    // Fully caught up: everything up to the time this drain began has been read
                    _ipWatermark = _ipDrainStartedAt ?? dbNow;
                    _ipDrainStartedAt = null;
                    _ipCursor = null;
                }
                else
                {
                    _ipDrainStartedAt ??= dbNow;
                    if (lastIpUsedAt != null) _ipWatermark = lastIpUsedAt;
                }
            }
            else
            {
                _ipWatermark = dbNow;
            }

            PluginMetrics.CacheRefresh.RecordSince(start);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Rows whose cached columns actually differ (or that enter/leave the active set).</summary>
    private static IEnumerable<BanRecord> FilterRealChanges(BanCacheSnapshot snapshot, List<BanRecord> rows)
    {
        foreach (var row in rows)
        {
            var active = row.StatusEnum == BanStatus.ACTIVE;
            if (snapshot.ActiveBans.TryGetValue(row.Id, out var cached))
            {
                if (!active || cached != row) yield return row; // record equality covers SteamID/IP/name/ends/duration
            }
            else if (active)
            {
                yield return row;
            }
        }
    }

    private async Task<BanCacheSnapshot> ReconcileAsync(DbConnection connection, BanCacheSnapshot next, bool multiServer,
        int? serverId, CancellationToken ct)
    {
        Interlocked.Increment(ref PluginMetrics.CacheReconciles);
        var dbIds = (await connection.QueryAsync<int>(new CommandDefinition(ActiveIdsSql(multiServer), new { serverId },
            cancellationToken: ct)).ConfigureAwait(false)).ToHashSet();

        var removed = new List<int>();
        foreach (var id in next.ActiveBans.Keys)
            if (!dbIds.Contains(id)) removed.Add(id);

        var missing = new List<int>();
        foreach (var id in dbIds)
            if (!next.ActiveBans.ContainsKey(id)) missing.Add(id);

        var added = new List<BanRecord>();
        for (var i = 0; i < missing.Count; i += 500)
        {
            var batch = missing.GetRange(i, Math.Min(500, missing.Count - i));
            added.AddRange(await connection.QueryAsync<BanRecord>(new CommandDefinition(BansByIdsSql, new { ids = batch },
                cancellationToken: ct)).ConfigureAwait(false));
        }

        return next.WithBans(added, removed);
    }

    private async Task<(BanCacheSnapshot, bool Drained, DateTime? LastUsedAt)> RefreshIpHistoryAsync(DbConnection connection,
        BanCacheSnapshot next, CancellationToken ct)
    {
        // Continue strictly after the cursor of an unfinished drain; otherwise start at watermark − overlap.
        var cursor = _ipCursor ?? new IpCursor((_ipWatermark ?? DateTime.MinValue) - Overlap, -1, 0);
        var unknown = UnknownName();
        DateTime? lastUsedAt = null;
        for (var pageNo = 0; pageNo < MaxIpPagesPerRefresh; pageNo++)
        {
            var page = (await connection.QueryAsync<IpHistoryRow>(new CommandDefinition(IpHistoryPageSql,
                new { t = cursor.UsedAt, s = cursor.SteamId, a = cursor.Address, limit = IpPageSize },
                cancellationToken: ct)).ConfigureAwait(false)).AsList();
            if (page.Count > 0)
            {
                next = next.WithIpHistory(page, unknown);
                var last = page[^1];
                cursor = new IpCursor(last.Used_at, last.Steamid, last.Address);
                lastUsedAt = last.Used_at;
            }

            if (page.Count < IpPageSize)
            {
                _ipCursor = null;
                return (next, true, lastUsedAt);
            }
        }

        _ipCursor = cursor;
        return (next, false, lastUsedAt);
    }

    /// <summary>
    /// Removes links older than ExpireOldIpBans days (the SQL job deletes those rows) from memory, a bounded slice of
    /// accounts per refresh, so the index shrinks like the table without a full scan. Correctness of lookups does not
    /// wait for this: lookups apply the cutoff themselves (see <see cref="BanCacheSnapshot"/>).
    /// </summary>
    private BanCacheSnapshot PruneStaleIpHistory(BanCacheSnapshot next, int expireOldIpBansDays)
    {
        if (expireOldIpBansDays <= 0)
        {
            _pruneAccounts = null;
            return next;
        }

        if (_pruneAccounts == null || _pruneNext >= _pruneAccounts.Length)
        {
            _pruneAccounts = next.IpHistory.AccountIds();
            _pruneNext = 0;
        }

        var take = Math.Min(IpPruneAccountsPerRefresh, _pruneAccounts.Length - _pruneNext);
        if (take <= 0) return next;
        var cutoff = Time.ActualDateTime().AddDays(-expireOldIpBansDays);
        var pruned = next.WithIpPrune(cutoff, _pruneAccounts.AsSpan(_pruneNext, take));
        _pruneNext += take;
        if (!ReferenceEquals(pruned, next)) IpPrunedPasses++;
        return pruned;
    }

    /// <summary>
    /// Detects IP-history rows deleted outside the expiry rule (site clean-up, manual DELETE) by comparing a COUNT/SUM
    /// of SQL with the same triple over the index, every <see cref="IpChecksumEveryRefreshes"/> refreshes. A mismatch
    /// must repeat on the next refresh (rows inserted while counting would otherwise look like a difference) before the
    /// index is rebuilt from SQL, in the background, in bounded pages.
    /// </summary>
    private async Task<(BanCacheSnapshot, bool Rebuilt)> ReconcileIpHistoryAsync(DbConnection connection, BanCacheSnapshot next,
        int expireOldIpBansDays, CancellationToken ct)
    {
        if (!_ipChecksumSuspect && ++_refreshesSinceIpChecksum < IpChecksumEveryRefreshes) return (next, false);
        _refreshesSinceIpChecksum = 0;

        var cutoff = expireOldIpBansDays > 0 ? Time.ActualDateTime().AddDays(-expireOldIpBansDays) : DateTime.MinValue;
        await using var reader = await connection.ExecuteReaderAsync(new CommandDefinition(IpChecksumSql, new { cutoff },
            cancellationToken: ct)).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return (next, false);
        var sql = (Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2)));
        await reader.CloseAsync().ConfigureAwait(false);

        if (next.IpHistory.Checksum(cutoff) == sql)
        {
            _ipChecksumSuspect = false;
            return (next, false);
        }

        if (!_ipChecksumSuspect)
        {
            _ipChecksumSuspect = true; // confirm on the next refresh
            return (next, false);
        }

        _ipChecksumSuspect = false;
        IpRebuilds++;
        Interlocked.Increment(ref PluginMetrics.CacheReconciles);
        RateLimitedLog.Warning("cache.ip.rebuild", "sa_players_ips differs from the cached IP history (rows removed outside expiry?); rebuilding it");
        var rebuilt = await ReadIpHistoryIndexAsync(connection, ct).ConfigureAwait(false);
        _pruneAccounts = null;
        return (next.WithIpIndex(rebuilt), true);
    }

    private static async Task<DateTime> GetDatabaseTimeAsync(DbConnection connection, CancellationToken ct)
    {
        // CURRENT_TIMESTAMP is in the session time zone on MySQL and UTC on SQLite – the same clocks
        // updated_at/used_at are written with. SQLite returns text, so convert explicitly.
        var value = await connection.ExecuteScalarAsync<object>(new CommandDefinition("SELECT CURRENT_TIMESTAMP",
            cancellationToken: ct)).ConfigureAwait(false);
        return PlayerManager.ToDateTime(value) ?? throw new InvalidOperationException($"Unexpected CURRENT_TIMESTAMP value '{value}'");
    }

    private static string UnknownName() => CS2_SimpleAdmin._localizer?["sa_unknown"] ?? "Unknown";

    // ------------------------------------------------------------------ immediate updates

    /// <summary>
    /// Applies an unban immediately (the periodic refresh would otherwise keep rejecting the player for up to a
    /// minute). Serialised with refreshes; publishes a new snapshot.
    /// </summary>
    public async Task SetBanStatusAsync(IEnumerable<int> banIds, BanStatus status, CancellationToken ct = default)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = Snapshot;
            var changes = new List<BanRecord>();
            foreach (var id in banIds)
                if (snapshot.ActiveBans.TryGetValue(id, out var ban) && ban.StatusEnum != status)
                    changes.Add(ban with { Status = status.ToString() });
            if (changes.Count > 0) Publish(snapshot.WithBans(changes));
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Adds a ban that was just written by this server, so a reconnect is rejected before the next refresh.</summary>
    public async Task AddOrUpdateBanAsync(BanRecord ban, CancellationToken ct = default)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Snapshot.IsInitialized) Publish(Snapshot.WithBans([ban]));
        }
        finally
        {
            _writer.Release();
        }
    }

    // ------------------------------------------------------------------ lookups (any thread, no waiting)

    /// <summary>Active bans of a SteamID (a copy). Not a history count: use the DB for totals.</summary>
    public List<BanRecord> GetPlayerBansBySteamId(ulong steamId) =>
        Snapshot.BySteamId.TryGetValue(steamId, out var bans) ? [..bans] : [];

    public List<BanRecord> GetActiveBans() => [..Snapshot.ActiveBans.Values];

    public List<(ulong SteamId, DateTime UsedAt, string PlayerName)> GetAccountsByIp(string ipAddress, DateTime now,
        int expireOldIpBansDays) =>
        IpHelper.TryConvertIpToUint(ipAddress, out var ip) ? [..Snapshot.GetAccountsByIp(ip, now, expireOldIpBansDays)] : [];

    public bool HasIpForPlayer(ulong steamId, string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || !IpHelper.TryConvertIpToUint(ipAddress, out var ip)) return false;
        if (!Snapshot.IpHistory.TryGetIps(steamId, out var records)) return false;
        foreach (var r in records)
            if (r.Ip == ip) return true;
        return false;
    }

    /// <summary>
    /// Ban check for a connecting/online player against one snapshot. Also returns whether the matched ban row is
    /// missing the player's IP/SteamID/name (the caller then queues <see cref="UpdatePlayerDataAsync"/>, as before).
    /// </summary>
    public BanCheckResult CheckBan(CS2_SimpleAdminConfig config, ulong steamId, string? ipAddress, DateTime now)
    {
        var other = config.OtherSettings;
        var snapshot = Snapshot;
        return other.BanType switch
        {
            0 => snapshot.CheckPlayer(steamId, null, 0, other.ExpireOldIpBans, now),
            _ => other.CheckMultiAccountsByIp
                ? snapshot.CheckPlayerOrAnyIp(steamId, ipAddress, other.BanType, other.ExpireOldIpBans, true, now)
                : snapshot.CheckPlayer(steamId, ipAddress, other.BanType, other.ExpireOldIpBans, now)
        };
    }

    /// <summary>Same conditions as the original code for back-filling ban rows with the player's data.</summary>
    internal static bool NeedsPlayerDataUpdate(BanCheckResult result, bool multiAccountPath, string? ipAddress)
    {
        if (!result.IsBanned || result.Ban == null) return false;
        var ban = result.Ban;
        return result.Match switch
        {
            BanMatch.SteamId when multiAccountPath => string.IsNullOrEmpty(ban.PlayerName) ||
                                                    string.IsNullOrEmpty(ban.PlayerIp) && !string.IsNullOrEmpty(ipAddress),
            BanMatch.SteamId => string.IsNullOrEmpty(ban.PlayerIp) && !string.IsNullOrEmpty(ipAddress) || !ban.PlayerSteamId.HasValue,
            BanMatch.Ip when multiAccountPath => true,
            BanMatch.Ip => string.IsNullOrEmpty(ban.PlayerIp) || !ban.PlayerSteamId.HasValue,
            BanMatch.SharedIp or BanMatch.SharedIpHistory => true,
            _ => false
        };
    }

    /// <summary>
    /// Fills missing player_ip/player_name on the player's active ban rows in the database (DB worker), then
    /// mirrors the change into a new snapshot (no in-place mutation of cached records).
    /// </summary>
    public async Task UpdatePlayerDataAsync(CS2_SimpleAdminConfig config, int? serverId, string? playerName, ulong? steamId,
        string? ipAddress, CancellationToken ct)
    {
        if (CS2_SimpleAdmin.DatabaseProvider == null) return;

        var baseSql = """
                          UPDATE sa_bans
                          SET
                              player_ip = COALESCE(player_ip, @PlayerIP),
                              player_name = COALESCE(player_name, @PlayerName)
                          WHERE
                              (player_steamid = @PlayerSteamID OR player_ip = @PlayerIP)
                              AND status = 'ACTIVE'
                              AND (duration = 0 OR ends > @CurrentTime)
                      """;

        if (!config.MultiServerMode)
            baseSql += " AND server_id = @ServerId;";

        var other = config.OtherSettings;
        var playerIp = other.BanType == 0 || string.IsNullOrEmpty(ipAddress) || other.IgnoredIps.Contains(ipAddress)
            ? null
            : ipAddress;
        var parameters = new
        {
            PlayerSteamID = steamId,
            PlayerIP = playerIp,
            PlayerName = string.IsNullOrEmpty(playerName) ? string.Empty : playerName,
            CurrentTime = Time.ActualDateTime(),
            ServerId = serverId
        };

        await using (var connection = await CS2_SimpleAdmin.DatabaseProvider.CreateConnectionAsync(ct).ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(baseSql, parameters, cancellationToken: ct)).ConfigureAwait(false);
        }

        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = Snapshot;
            var patched = new List<BanRecord>();
            uint ip = 0;
            var hasIp = !string.IsNullOrEmpty(ipAddress) && IpHelper.TryConvertIpToUint(ipAddress, out ip);
            foreach (var ban in snapshot.ActiveBans.Values)
            {
                var bySteam = steamId.HasValue && ban.PlayerSteamId == steamId;
                var byIp = hasIp && !string.IsNullOrEmpty(ban.PlayerIp) && IpHelper.TryConvertIpToUint(ban.PlayerIp, out var banIp) && banIp == ip;
                if (!bySteam && !byIp) continue;
                var updated = ban with
                {
                    PlayerIp = string.IsNullOrEmpty(ban.PlayerIp) && playerIp != null ? playerIp : ban.PlayerIp,
                    PlayerName = string.IsNullOrEmpty(ban.PlayerName) && !string.IsNullOrEmpty(playerName) ? playerName : ban.PlayerName,
                    PlayerSteamId = ban.PlayerSteamId ?? (byIp ? steamId : null)
                };
                if (updated != ban) patched.Add(updated);
            }

            if (patched.Count > 0) Publish(snapshot.WithBans(patched));
        }
        finally
        {
            _writer.Release();
        }
    }

    public string Describe()
    {
        var s = Snapshot;
        return $"cache: initialized={s.IsInitialized} activeBans={s.ActiveCount} steamKeys={s.BySteamId.Count} ipKeys={s.ByIp.Count} " +
               $"ipHistoryAccounts={s.IpAccountCount} ipHistoryAddresses={s.IpAddressCount} ipOverlay={s.IpHistory.OverlayCount} ipRebuilds={IpRebuilds} ipPrunePasses={IpPrunedPasses} banWatermark={_banWatermark:O} ipWatermark={_ipWatermark:O}\n";
    }

    /// <summary>
    /// Marks the cache as disposed. Refresh/initialize calls made afterwards (late background work of an
    /// unloaded plugin) do nothing; the snapshot is replaced by an empty one.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Publish(BanCacheSnapshot.Empty);
    }
}

public class IpRecordComparer : IEqualityComparer<IpRecord>
{
    public bool Equals(IpRecord x, IpRecord y)
        => x.Ip == y.Ip;

    public int GetHashCode(IpRecord obj)
        => obj.Ip.GetHashCode();
}
