using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API.Modules.Admin;
using System.Diagnostics.CodeAnalysis;
using CS2_SimpleAdmin.Database;

namespace CS2_SimpleAdmin.Managers;

public class PermissionManager(IDatabaseProvider? databaseProvider)
{
    public static readonly ConcurrentDictionary<SteamID, (DateTime? ExpirationTime, List<string> Flags)> AdminCache = new();

    /// <summary>
    /// Retrieves all players' flags and associated data asynchronously.
    /// </summary>
    /// <returns>A list of tuples containing player SteamID, name, flags, immunity, and expiration time.</returns>
    private async Task<List<(ulong, string ,List<string>, int, DateTime?)>> GetAllPlayersFlags()
    {
	    if (databaseProvider == null)
		    return new List<(ulong, string, List<string>, int, DateTime?)>();

	    var now = Time.ActualDateTime();

	    try
	    {
		    await using var connection = await databaseProvider.CreateConnectionAsync();
		    var sql = databaseProvider.GetAdminsQuery();
		    var admins = (await connection.QueryAsync(sql, new { CurrentTime = now, serverid = CS2_SimpleAdmin.ServerId })).ToList();

		    var groupedPlayers = admins
			    .GroupBy(r => new { playerSteamId = r.player_steamid, playerName = r.player_name, r.immunity, r.ends })
			    .Select(g =>
			    {
				    ulong steamId = g.Key.playerSteamId switch
				    {
					    long l => (ulong)l,
					    int i => (ulong)i,
					    string s when ulong.TryParse(s, out var parsed) => parsed,
					    _ => 0UL
				    };

				    int immunity = g.Key.immunity switch
				    {
					    int i => i,
					    string s when int.TryParse(s, out var parsed) => parsed,
					    _ => 0
				    };

				    DateTime? ends = g.Key.ends as DateTime?;

				    string playerName = g.Key.playerName as string ?? string.Empty;

				    // Dapper returns string here, not dynamic
				    var flags = g.Select(r => r.flag as string ?? string.Empty)
					    .Distinct()
					    .ToList();

				    return (steamId, playerName, flags, immunity, ends);
			    })
			    .ToList();

		    return groupedPlayers;
	    }
	    catch (Exception ex)
	    {
		    CS2_SimpleAdmin._logger?.LogError("Unable to load admins from database! {exception}", ex.Message);
		    throw; // an empty list would strip every admin; abort the reload and keep the current permissions
	    }
    }

    /// <summary>
    /// Retrieves all groups' data including flags and immunity asynchronously.
    /// </summary>
    /// <returns>A dictionary with group names as keys and tuples of flags and immunity as values.</returns>
    private async Task<Dictionary<string, (List<string>, int)>> GetAllGroupsData()
    {
	    if (databaseProvider == null) return [];

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();
            var sql = databaseProvider.GetGroupsQuery();
            // Async: this runs on a DB worker, a synchronous Query would block it for the whole round trip
            var groupData = (await connection.QueryAsync(sql, new { serverid = CS2_SimpleAdmin.ServerId })).ToList();
            if (groupData.Count == 0)
            {
                return [];
            }

            var groupInfoDictionary = new Dictionary<string, (List<string>, int)>();
            foreach (var row in groupData)
            {
                var groupName = (string)row.group_name;
                var flag = (string)row.flag;
                var immunity = (int)row.immunity;

                if (!groupInfoDictionary.TryGetValue(groupName, out (List<string>, int) value))
                {
                    value = ([], immunity);
                    groupInfoDictionary[groupName] = value;
                }

                value.Item1.Add(flag);
            }

            return groupInfoDictionary;
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogError("Unable to load groups from database! {exception}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Creates a JSON file containing groups data asynchronously.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    /// <returns>True when at least one group was written (the caller then loads the file into CSS).</returns>
    public async Task<bool> CreateGroupsJsonFile()
    {
        var (json, count) = await ReadGroupsJsonAsync();
        var filePath = Path.Combine(CS2_SimpleAdmin.Instance.ModuleDirectory, "data", "groups.json");
        await WriteAtomicallyAsync(filePath, json);
        return count > 0;
    }

    /// <summary>Reads the groups from the database and serialises them; writes nothing.</summary>
    private async Task<(string Json, int Count)> ReadGroupsJsonAsync()
    {
        var groupsData = await GetAllGroupsData();
        var jsonData = new Dictionary<string, object>();

        foreach (var kvp in groupsData)
        {
            var groupData = new Dictionary<string, object>
            {
                ["flags"] = kvp.Value.Item1,
                ["immunity"] = kvp.Value.Item2
            };

            jsonData[kvp.Key] = groupData;
        }

        var options = new JsonSerializerOptions
        {
	        WriteIndented = true,
	        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        return (JsonSerializer.Serialize(jsonData, options), groupsData.Count);
    }

    /// <summary>
    /// Everything one admin reload needs, read from the database <b>before</b> any file is touched: if reading fails
    /// nothing was replaced, so the files on disk and the permissions in CounterStrikeSharp stay as they were.
    /// </summary>
    internal sealed record PreparedAdminReload(string GroupsJson, bool GroupsWritten, string AdminsJson, bool AdminsWritten,
        List<(SteamID steamId, DateTime? ends, List<string> flags)> Admins);

    /// <summary>Reads groups and admins (throws on any database error); no file is written.</summary>
    internal async Task<PreparedAdminReload> PrepareAdminReloadAsync()
    {
        var (groupsJson, groupCount) = await ReadGroupsJsonAsync();
        var (adminsJson, admins) = await ReadAdminsJsonAsync();
        // The old code loaded a non-empty file ("{}" included); keep that behaviour for both files
        return new PreparedAdminReload(groupsJson, groupCount > 0, adminsJson, adminsJson.Length > 0, admins);
    }

    /// <summary>
    /// Raised when a failed pair commit or recovery could not put the pair back completely: groups.json and admins.json
    /// may be from different versions until the next commit repairs them from the journal. Nothing is applied in that case.
    /// </summary>
    internal sealed class AdminFilesInconsistentException(string message, Exception inner) : IOException(message, inner);

    /// <summary>
    /// Raised when an interrupted commit left a journal / backups that cannot be interpreted safely (malformed or
    /// truncated journal, a required backup that is missing or does not match, files that match neither version).
    /// <b>Nothing was changed</b>: the journal, the backups and both files stay exactly as found, as evidence, and every
    /// following reload fails with this same error until an operator resolves it (the message names the journal).
    /// The permissions that are already in force are not touched.
    /// </summary>
    internal sealed class AdminFilesRecoveryException(string message, Exception? inner = null) : IOException(message, inner);

    /// <summary>Test seam: replaces <c>source</c> over <c>target</c>. Production: <see cref="File.Move(string,string,bool)"/>.</summary>
    internal static Action<string, string> ReplaceFile { get; set; } = static (source, target) => File.Move(source, target, true);

    /// <summary>
    /// Test seam: called at named points of commit and recovery (<c>temp-written</c>, <c>journal-published:prepared</c>,
    /// <c>replaced:groups</c>, <c>journal-published:committed</c>, <c>cleanup:backup-deleted:admins</c>, ...). Tests use it
    /// to take a snapshot of the directory at that instant, which is exactly the state a crash there would leave.
    /// </summary>
    internal Func<string, Task>? FaultHook { get; set; }

    private Task Hit(string stage) => FaultHook?.Invoke(stage) ?? Task.CompletedTask;

    private const string PairJournalName = "admin-pair.journal";
    private const string PairLockName = "admin-pair.lock";
    private const string ManifestMagic = "cs2sa-admin-pair 2";

    /// <summary>How long a reload waits for another reload (or an old plugin generation) that is committing the pair.</summary>
    internal static TimeSpan PairLockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>What <see cref="RecoverAdminFilesAsync"/> found and did.</summary>
    internal enum PairRecovery
    {
        /// <summary>No journal: nothing was interrupted.</summary>
        None,

        /// <summary>A commit that was not declared complete was undone: both files are the original pair.</summary>
        RolledBack,

        /// <summary>A commit that was declared complete was only missing its cleanup: both files are the new pair.</summary>
        RolledForward
    }

    private enum PairState { Prepared, Committed }

    /// <summary>The parsed journal. Version-2 journals carry hashes of every file involved; the old two-line format does not.</summary>
    private sealed record PairManifest(PairState State, bool Legacy, bool[] Existed, string?[] BackupHash, string?[] NewHash);

    private static readonly UTF8Encoding Utf8 = new(false);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string[] PairNames => ["groups", "admins"];

    /// <summary>
    /// Replaces groups.json and admins.json as <b>one version</b>. Two renames are not an atomic operation, so the pair
    /// is made transactional with a journal (manifest) and backups. Protocol, in order:
    /// <list type="number">
    /// <item>take the pair lock (an exclusive lock file, awaited asynchronously; it also fences an older plugin
    /// generation or hot-reloaded copy that is still committing; the game thread never takes it);</item>
    /// <item>recover an interrupted earlier commit first (<see cref="RecoverAdminFilesAsync"/>): roll back an undeclared
    /// commit, finish the cleanup of a declared one; if that is not provably safe, fail without changing anything;</item>
    /// <item>write both new files as temp files and flush them; copy both originals to <c>.bak</c> and flush;</item>
    /// <item>publish the manifest, state <c>prepared</c>, atomically (temp file, flush, rename over the journal): a
    /// journal either does not exist or is complete and checksummed, it is never written in place. It lists which
    /// originals existed and the SHA-256 of every backup and new file;</item>
    /// <item>cancellation / lifetime end is honoured up to here (the commit is abandoned cleanly); <b>from the first
    /// replacement on both files are replaced or both are restored</b>, the token is no longer consulted;</item>
    /// <item>replace groups.json, replace admins.json; if either fails, validate and restore from the backups, delete the
    /// journal, rethrow the original error; if the restore itself fails keep the journal and backups,
    /// throw <see cref="AdminFilesInconsistentException"/> (the next reload repairs the pair);</item>
    /// <item>only now publish the manifest again, state <c>committed</c>, atomically: this is the durable "both new files
    /// are in place" decision. A crash before it rolls the pair back to the originals, a crash after it rolls it forward;
    /// never a mix;</item>
    /// <item>cleanup: delete the backups, then the journal last. A failure here is only logged (the pair is correct;
    /// the next reload finishes the cleanup from the <c>committed</c> journal).</item>
    /// </list>
    /// Only after this method returns normally do both files on disk belong to the same prepared version; the apply step
    /// reads exactly these files.
    /// <para>
    /// What is and is not promised: file contents are flushed to the device (<c>Flush(true)</c>), journal publication and
    /// each replacement are single renames. .NET offers no portable directory flush, so the order of the renames is not
    /// guaranteed to be persisted across a power loss or a kernel crash; the protocol guarantees a consistent pair
    /// after a <i>process</i> crash or kill, and detects (instead of silently interpreting) states it cannot prove safe.
    /// </para>
    /// </summary>
    internal async Task CommitAdminFilesAsync(PreparedAdminReload prepared, string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        var targets = PairNames.Select(n => Path.Combine(dataDirectory, n + ".json")).ToArray();
        var backups = targets.Select(t => t + ".bak").ToArray();
        var temps = targets.Select(t => t + ".tmp").ToArray();
        var journalPath = Path.Combine(dataDirectory, PairJournalName);

        await using var pairLock = await PairLock.AcquireAsync(dataDirectory, cancellationToken).ConfigureAwait(false);
        await RecoverLockedAsync(dataDirectory, cancellationToken).ConfigureAwait(false);

        try
        {
            byte[][] fresh = [Utf8.GetBytes(prepared.GroupsJson), Utf8.GetBytes(prepared.AdminsJson)];
            for (var i = 0; i < targets.Length; i++)
            {
                await WriteDurablyAsync(temps[i], fresh[i], cancellationToken).ConfigureAwait(false);
                await Hit("temp-written:" + PairNames[i]).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var existed = new bool[targets.Length];
            var backupHash = new string?[targets.Length];
            for (var i = 0; i < targets.Length; i++)
            {
                if (!File.Exists(targets[i])) continue;
                var original = await File.ReadAllBytesAsync(targets[i], cancellationToken).ConfigureAwait(false);
                existed[i] = true;
                backupHash[i] = Hash(original);
                await WriteDurablyAsync(backups[i], original, cancellationToken).ConfigureAwait(false);
                await Hit("backup-written:" + PairNames[i]).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var newHash = fresh.Select(Hash).ToArray();
            var preparedManifest = new PairManifest(PairState.Prepared, false, existed, backupHash, newHash);
            await PublishManifestAsync(journalPath, preparedManifest, cancellationToken).ConfigureAwait(false);
            await Hit("journal-published:prepared").ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                // Nothing was replaced yet: abandon. The journal goes first; if the backups cannot be deleted they are
                // litter (a journal-less backup is never trusted), if the journal cannot be deleted it describes an
                // untouched pair and the next recovery is a no-op.
                File.Delete(journalPath);
                throw new OperationCanceledException(cancellationToken);
            }

            // ---- point of no return for cancellation: both replaced or both restored ----
            try
            {
                for (var i = 0; i < targets.Length; i++)
                {
                    ReplaceFile(temps[i], targets[i]);
                    await Hit("replaced:" + PairNames[i]).ConfigureAwait(false);
                }

                await PublishManifestAsync(journalPath, preparedManifest with { State = PairState.Committed }, CancellationToken.None)
                    .ConfigureAwait(false);
                await Hit("journal-published:committed").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    await RollBackAsync(preparedManifest, dataDirectory, "commit failed: " + ex.Message).ConfigureAwait(false);
                }
                catch (Exception restoreEx)
                {
                    throw new AdminFilesInconsistentException(
                        "groups.json/admins.json could not be committed and could not be restored; the journal and backups were " +
                        "kept and the next reload repairs the pair: " + restoreEx.Message, ex);
                }

                throw;
            }

            await CleanUpCommittedAsync(dataDirectory, warnOnly: true).ConfigureAwait(false);
        }
        finally
        {
            foreach (var temp in temps.Append(journalPath + ".tmp"))
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // Backups are meaningful only together with a journal; ones left without a journal are just litter
            if (!File.Exists(journalPath))
                foreach (var backup in backups)
                    try { File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Repairs a commit that was interrupted by a crash or a failed restore, under the pair lock. Safe to call at any
    /// time; <see cref="CommitAdminFilesAsync"/> runs it first. Throws <see cref="AdminFilesRecoveryException"/> (nothing
    /// changed) when the state cannot be proven safe, <see cref="AdminFilesInconsistentException"/> when a restore fails
    /// half way (journal and backups kept, retry later).
    /// </summary>
    internal async Task<PairRecovery> RecoverAdminFilesAsync(string dataDirectory, CancellationToken cancellationToken = default)
    {
        await using var pairLock = await PairLock.AcquireAsync(dataDirectory, cancellationToken).ConfigureAwait(false);
        return await RecoverLockedAsync(dataDirectory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PairRecovery> RecoverLockedAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        var journalPath = Path.Combine(dataDirectory, PairJournalName);
        // A journal that was still being published is not a journal (publication is the rename)
        try { File.Delete(journalPath + ".tmp"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (!File.Exists(journalPath)) return PairRecovery.None;

        PairManifest manifest;
        try
        {
            manifest = ParseManifest(await File.ReadAllTextAsync(journalPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AdminFilesRecoveryException(
                $"The admin file journal '{journalPath}' is unreadable or incomplete ({ex.Message}). groups.json, admins.json, " +
                "their .bak copies and the journal were left exactly as found; the permissions in force are unchanged. " +
                "Check them by hand, then delete the journal (and .bak files) to allow reloads again.", ex);
        }

        if (manifest.State == PairState.Committed)
        {
            await VerifyPairAsync(manifest, journalPath, dataDirectory, cancellationToken).ConfigureAwait(false);
            await CleanUpCommittedAsync(dataDirectory, warnOnly: false).ConfigureAwait(false);
            return PairRecovery.RolledForward;
        }

        await RollBackAsync(manifest, dataDirectory, "interrupted commit", journalPath).ConfigureAwait(false);
        return PairRecovery.RolledBack;
    }

    /// <summary>A committed journal is trusted only if both files really are the committed version.</summary>
    private static async Task VerifyPairAsync(PairManifest manifest, string journalPath, string dataDirectory, CancellationToken ct)
    {
        for (var i = 0; i < PairNames.Length; i++)
        {
            var path = Path.Combine(dataDirectory, PairNames[i] + ".json");
            var ok = File.Exists(path) && Hash(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)) == manifest.NewHash[i];
            if (!ok)
                throw new AdminFilesRecoveryException(
                    $"The admin file journal '{journalPath}' says the new pair was committed, but {PairNames[i]}.json is missing or " +
                    "differs from the committed version. Nothing was changed (journal, backups and files kept as evidence); " +
                    "check the files by hand, then delete the journal (and .bak files) to allow reloads again.");
        }
    }

    /// <summary>
    /// Puts the originals back, after validating <b>everything</b> first: no file is touched unless every required backup
    /// exists and matches the hash the journal recorded, and every target is either the original or the new version.
    /// Legacy (two-line) journals have no hashes, so there only presence of the required backups can be checked.
    /// Order at the end: the journal is deleted first, then the backups. Once the originals are verified back in place the
    /// journal has no further value, and removing it first means an interrupted cleanup can only leave harmless
    /// journal-less backups, never a journal that points at backups that are gone.
    /// </summary>
    private async Task RollBackAsync(PairManifest manifest, string dataDirectory, string why, string? journalPath = null)
    {
        journalPath ??= Path.Combine(dataDirectory, PairJournalName);
        var targets = PairNames.Select(n => Path.Combine(dataDirectory, n + ".json")).ToArray();
        var restores = new (string Target, byte[] Content)[PairNames.Length];
        var deletes = new bool[PairNames.Length];

        // ---- validate, change nothing ----
        for (var i = 0; i < targets.Length; i++)
        {
            var backup = targets[i] + ".bak";
            string? current = File.Exists(targets[i]) ? Hash(await File.ReadAllBytesAsync(targets[i]).ConfigureAwait(false)) : null;

            if (!manifest.Existed[i])
            {
                // There was no original: whatever is there now must be what this commit wrote, and goes
                if (current == null) continue;
                if (!manifest.Legacy && current != manifest.NewHash[i])
                    throw Unsafe(journalPath, $"{PairNames[i]}.json did not exist before the commit and is not the file it wrote");
                deletes[i] = true;
                continue;
            }

            if (!manifest.Legacy)
            {
                if (current == manifest.BackupHash[i]) continue; // already the original
                if (current != null && current != manifest.NewHash[i])
                    throw Unsafe(journalPath, $"{PairNames[i]}.json is neither the original nor the committed version");
            }

            if (!File.Exists(backup))
                throw Unsafe(journalPath, $"the backup {PairNames[i]}.json.bak is missing, so the original cannot be restored");
            var content = await File.ReadAllBytesAsync(backup).ConfigureAwait(false);
            if (!manifest.Legacy && Hash(content) != manifest.BackupHash[i])
                throw Unsafe(journalPath, $"the backup {PairNames[i]}.json.bak does not match the journal");
            restores[i] = (targets[i], content);
        }

        await Hit("rollback-validated").ConfigureAwait(false);

        // ---- act ----
        try
        {
            for (var i = 0; i < targets.Length; i++)
            {
                if (deletes[i]) File.Delete(targets[i]);
                else if (restores[i].Target != null) await RestoreFileAsync(restores[i].Target, restores[i].Content).ConfigureAwait(false);
                await Hit("rolled-back:" + PairNames[i]).ConfigureAwait(false);
            }

            File.Delete(journalPath);
            await Hit("rollback-journal-deleted").ConfigureAwait(false);
            foreach (var target in targets)
                File.Delete(target + ".bak");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AdminFilesInconsistentException($"Restoring the original admin files failed ({why}): {ex.Message}", ex);
        }
    }

    private static AdminFilesRecoveryException Unsafe(string journalPath, string problem) =>
        new($"The admin file journal '{journalPath}' cannot be applied safely: {problem}. Nothing was changed (journal, backups and files " +
            "kept as evidence); the permissions in force are unchanged. Check the files by hand, then delete the journal " +
            "(and .bak files) to allow reloads again.");

    /// <summary>Copy-then-rename so a restore is never visible half written.</summary>
    private static async Task RestoreFileAsync(string target, byte[] content)
    {
        var temp = target + ".restore.tmp";
        try
        {
            await WriteDurablyAsync(temp, content, CancellationToken.None).ConfigureAwait(false);
            File.Move(temp, target, true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The committed pair needs only its recovery material removed: backups first, journal last, so the journal (the
    /// proof that the new pair was committed) outlives every step that could be interrupted.
    /// </summary>
    private async Task CleanUpCommittedAsync(string dataDirectory, bool warnOnly)
    {
        try
        {
            foreach (var name in PairNames)
            {
                File.Delete(Path.Combine(dataDirectory, name + ".json.bak"));
                await Hit("cleanup:backup-deleted:" + name).ConfigureAwait(false);
            }

            File.Delete(Path.Combine(dataDirectory, PairJournalName));
            await Hit("cleanup:journal-deleted").ConfigureAwait(false);
        }
        catch (Exception ex) when (warnOnly && ex is IOException or UnauthorizedAccessException)
        {
            Infrastructure.RateLimitedLog.Warning("admins.pair-cleanup",
                $"The admin files were committed, but their recovery files could not be removed ({ex.Message}); the next reload removes them");
        }
    }

    // ---------------------------------------------------------------- journal format

    private static string SerializeManifest(PairManifest m)
    {
        var body = new StringBuilder().Append(ManifestMagic).Append('\n')
            .Append("state ").Append(m.State == PairState.Committed ? "committed" : "prepared").Append('\n');
        for (var i = 0; i < PairNames.Length; i++)
            body.Append(PairNames[i]).Append(" existed ").Append(m.Existed[i] ? '1' : '0')
                .Append(" backup ").Append(m.BackupHash[i] ?? "-")
                .Append(" new ").Append(m.NewHash[i]).Append('\n');
        var text = body.ToString();
        return text + "checksum " + Hash(Utf8.GetBytes(text)) + "\n";
    }

    /// <summary>Strict: anything that is not exactly a version-2 journal or an old complete two-line journal throws.</summary>
    private static PairManifest ParseManifest(string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length == PairNames.Length && lines.All(l => l is "0" or "1"))
            return new PairManifest(PairState.Prepared, true, lines.Select(l => l == "1").ToArray(),
                new string?[PairNames.Length], new string?[PairNames.Length]);

        if (lines.Length != PairNames.Length + 3 || lines[0] != ManifestMagic)
            throw new FormatException("not a complete journal");

        var checksum = lines[^1];
        var bodyText = string.Join('\n', lines[..^1]) + "\n";
        if (checksum != "checksum " + Hash(Utf8.GetBytes(bodyText)))
            throw new FormatException("checksum mismatch");

        var state = lines[1] switch
        {
            "state prepared" => PairState.Prepared,
            "state committed" => PairState.Committed,
            _ => throw new FormatException("unknown state")
        };

        var existed = new bool[PairNames.Length];
        var backupHash = new string?[PairNames.Length];
        var newHash = new string?[PairNames.Length];
        for (var i = 0; i < PairNames.Length; i++)
        {
            var parts = lines[2 + i].Split(' ');
            if (parts.Length != 7 || parts[0] != PairNames[i] || parts[1] != "existed" || parts[3] != "backup" || parts[5] != "new"
                || parts[2] is not ("0" or "1") || !IsHash(parts[6]) || !(parts[4] == "-" ? parts[2] == "0" : IsHash(parts[4]) && parts[2] == "1"))
                throw new FormatException($"bad line for {PairNames[i]}");
            existed[i] = parts[2] == "1";
            backupHash[i] = parts[4] == "-" ? null : parts[4];
            newHash[i] = parts[6];
        }

        return new PairManifest(state, false, existed, backupHash, newHash);
    }

    private static bool IsHash(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    /// <summary>Temp file, flush, rename over the journal: readers and recovery see the old journal or the new one, never a part.</summary>
    private static async Task PublishManifestAsync(string journalPath, PairManifest manifest, CancellationToken ct)
    {
        var temp = journalPath + ".tmp";
        await WriteDurablyAsync(temp, Utf8.GetBytes(SerializeManifest(manifest)), ct).ConfigureAwait(false);
        File.Move(temp, journalPath, true);
    }

    private static async Task WriteDurablyAsync(string path, byte[] content, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(content, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(true); // to the device, not only to the OS cache
    }

    // ---------------------------------------------------------------- serialisation of commits

    /// <summary>
    /// Exclusive lock file in the data directory. It serialises commits and recoveries of the pair between concurrent
    /// reloads, plugin generations that overlap during a restart, and hot-reloaded copies of the plugin (which do not share
    /// statics, so an in-process lock could not do it). It is acquired by polling with <c>Task.Delay</c>: no thread is blocked, the
    /// wait honours cancellation and a timeout, and the game thread never takes it (the commit runs in a database
    /// queue job). The file stays in the directory; deleting a lock file is racy, keeping it is not.
    /// </summary>
    private sealed class PairLock : IAsyncDisposable
    {
        private FileStream? _stream;

        public static async Task<PairLock> AcquireAsync(string dataDirectory, CancellationToken ct)
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, PairLockName);
            var deadline = DateTime.UtcNow + PairLockTimeout;
            while (true)
            {
                try
                {
                    return new PairLock
                    {
                        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1)
                    };
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(15, ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"Another reload is still committing the admin files ('{path}' stayed locked for {PairLockTimeout.TotalSeconds:F0}s); this reload was skipped.", ex);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _stream?.Dispose();
            _stream = null;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Writes to a temp file and renames it over the target, so CSS (reading on the game thread) never sees a
    /// half-written file. The content length decides whether there is anything to load, no read-back needed.
    /// </summary>
    private static async Task WriteAtomicallyAsync(string path, string content)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content);
        File.Move(temp, path, true);
    }

    /// <summary>
    /// Creates a JSON file containing admins data asynchronously.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    public async Task<List<(SteamID steamId, DateTime? ends, List<string> flags)>> CreateAdminsJsonFile() =>
        (await CreateAdminsJsonFileWithStatus()).Admins;

    /// <returns>Admins for the in-memory cache, and whether the written file contains any admin.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    public async Task<(List<(SteamID steamId, DateTime? ends, List<string> flags)> Admins, bool Written)> CreateAdminsJsonFileWithStatus()
    {
        var (json, newCache) = await ReadAdminsJsonAsync();
        var filePath = Path.Combine(CS2_SimpleAdmin.Instance.ModuleDirectory, "data", "admins.json");
        await WriteAtomicallyAsync(filePath, json);

        // The old code loaded admins.json whenever the file was non-empty ("{}" included); keep that behaviour
        return (newCache, json.Length > 0);
    }

    /// <summary>Reads the admins from the database and serialises them; writes nothing.</summary>
    private async Task<(string Json, List<(SteamID steamId, DateTime? ends, List<string> flags)> Admins)> ReadAdminsJsonAsync()
    {
        List<(ulong identity, string name, List<string> flags, int immunity, DateTime? ends)> allPlayers = await GetAllPlayersFlags();
        var validPlayers = allPlayers
            .Where(player => SteamID.TryParse(player.identity.ToString(), out _))
            .ToList();

		var jsonData = validPlayers
			.GroupBy(player => player.name)
			.ToDictionary(
				group => group.Key,
				object (group) =>
				{
					var consolidatedData = group.Aggregate(
						new
						{
							identity = string.Empty,
							immunity = 0,
							flags = new List<string>(),
							groups = new List<string>()
						},
						(acc, player) =>
						{
							if (string.IsNullOrEmpty(acc.identity) && !string.IsNullOrEmpty(player.identity.ToString()))
							{
								acc = acc with { identity = player.identity.ToString() };
							}

							acc = acc with { immunity = Math.Max(acc.immunity, player.immunity) };

							acc = acc with
							{
								flags = acc.flags.Concat(player.flags.Where(flag => flag.StartsWith($"@"))).Distinct().ToList(),
								groups = acc.groups.Concat(player.flags.Where(flag => flag.StartsWith($"#"))).Distinct().ToList()
							};

							return acc;
						});

					return consolidatedData;
				});

		var newCache = validPlayers.Select(player => (new SteamID(player.identity), player.ends, player.flags)).ToList();

		var options = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};

        return (JsonSerializer.Serialize(jsonData, options), newCache);
    }

    /// <summary>
    /// Removes permissions of all previously cached admins and replaces the cache with the given admins.
    /// Must run on the main thread, right before AdminManager.LoadAdminData so admins are not left stripped.
    /// </summary>
    /// <param name="admins">Admins loaded from the database.</param>
    public static void ApplyAdminCache(List<(SteamID steamId, DateTime? ends, List<string> flags)> admins)
    {
        foreach (var steamId in AdminCache.Keys.ToList())
        {
            if (!AdminCache.TryRemove(steamId, out var cached)) continue;

            var data = AdminManager.GetPlayerAdminData(steamId);
            if (data == null) continue;

            var flagsArray = cached.Flags.ToArray();
            AdminManager.RemovePlayerPermissions(steamId, flagsArray);
            AdminManager.RemovePlayerFromGroup(steamId, true, flagsArray);

            data = AdminManager.GetPlayerAdminData(steamId);
            if (data == null || (data.Flags.Count != 0 && data.Groups.Count != 0)) continue;

            AdminManager.ClearPlayerPermissions(steamId);
            AdminManager.RemovePlayerAdminData(steamId);
        }

        foreach (var (steamId, ends, flags) in admins)
        {
            AdminCache.TryAdd(steamId, (ends, flags));
        }
    }

    /// <summary>
    /// Deletes an admin by their SteamID from the database asynchronously.
    /// </summary>
    /// <param name="playerSteamId">The SteamID of the admin to delete.</param>
    /// <param name="globalDelete">Whether to delete the admin globally or only for the current server.</param>
    public async Task DeleteAdminBySteamId(string playerSteamId, bool globalDelete = false)
    {
	    if (databaseProvider == null) return;
        if (string.IsNullOrEmpty(playerSteamId)) return;

        try
        {
	        await using var connection = await databaseProvider.CreateConnectionAsync();
            var sql = databaseProvider.GetDeleteAdminQuery(globalDelete);
            await connection.ExecuteAsync(sql, new { PlayerSteamID = playerSteamId, CS2_SimpleAdmin.ServerId });
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogError(ex.Message);
        }
    }

    /// <summary>
    /// Adds a new admin with specified details asynchronously.
    /// </summary>
    /// <param name="playerSteamId">SteamID of the admin.</param>
    /// <param name="playerName">Name of the admin.</param>
    /// <param name="flagsList">List of flags assigned to the admin.</param>
    /// <param name="immunity">Immunity level.</param>
    /// <param name="time">Duration in minutes for admin expiration; 0 means permanent.</param>
    /// <param name="globalAdmin">Whether the admin is global or server-specific.</param>
    public async Task AddAdminBySteamId(string playerSteamId, string playerName, List<string> flagsList, int immunity = 0, int time = 0, bool globalAdmin = false)
    {
	    if (databaseProvider == null) return;

        if (string.IsNullOrEmpty(playerSteamId) || flagsList.Count == 0) return;

        var now = Time.ActualDateTime();
        DateTime? futureTime = time != 0 ? now.AddMinutes(time) : null;

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();

            var insertAdminSql = databaseProvider.GetAddAdminQuery();
            var adminId = await connection.ExecuteScalarAsync<int>(insertAdminSql, new
            {
                playerSteamId,
                playerName,
                immunity,
                ends = futureTime,
                created = now,
                serverid = globalAdmin ? null : CS2_SimpleAdmin.ServerId
            });

            foreach (var flag in flagsList)
            {
                var insertFlagsSql = databaseProvider.GetAddAdminFlagsQuery();
                await connection.ExecuteAsync(insertFlagsSql, new
                {
                    adminId,
                    flag
                });
            }

            _ = CS2_SimpleAdmin.Instance.ReloadAdminsAsync();
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogError(ex.ToString());
        }
    }

    /// <summary>
    /// Adds a new group with flags and immunity asynchronously.
    /// </summary>
    /// <param name="groupName">Name of the group.</param>
    /// <param name="flagsList">List of flags assigned to the group.</param>
    /// <param name="immunity">Immunity level of the group.</param>
    /// <param name="globalGroup">Whether the group is global or server-specific.</param>
    public async Task AddGroup(string groupName, List<string> flagsList, int immunity = 0, bool globalGroup = false)
    {
	    if (databaseProvider == null) return;

        if (string.IsNullOrEmpty(groupName) || flagsList.Count == 0) return;

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();

            var insertGroup = databaseProvider.GetAddGroupQuery();
            var groupId = await connection.ExecuteScalarAsync<int>(insertGroup, new
            {
                groupName,
                immunity
            });

            foreach (var flag in flagsList)
            {
	            var insertFlagsSql = databaseProvider.GetAddGroupFlagsQuery();
                await connection.ExecuteAsync(insertFlagsSql, new
                {
                    groupId,
                    flag
                });
            }

            var insertGroupServer = databaseProvider.GetAddGroupServerQuery();
            await connection.ExecuteAsync(insertGroupServer, new { groupId, server_id = globalGroup ? null : CS2_SimpleAdmin.ServerId });
            _ = CS2_SimpleAdmin.Instance.ReloadAdminsAsync();
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogError("Problem with loading admins: {exception}", ex.Message);
        }
    }

    /// <summary>
    /// Deletes a group by name asynchronously.
    /// </summary>
    /// <param name="groupName">Name of the group to delete.</param>
    public async Task DeleteGroup(string groupName)
    {
	    if (databaseProvider == null) return;

        if (string.IsNullOrEmpty(groupName)) return;

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();
	        var sql = databaseProvider.GetDeleteGroupQuery();
            await connection.ExecuteAsync(sql, new { groupName });
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogError(ex.ToString());
        }
    }

    /// <summary>
    /// Deletes admins whose permissions have expired asynchronously.
    /// </summary>
    public async Task DeleteOldAdmins()
    {
	    if (databaseProvider == null) return;

        try
        {
            await using var connection = await databaseProvider.CreateConnectionAsync();

            var sql = databaseProvider.GetDeleteOldAdminsQuery();
            await connection.ExecuteAsync(sql, new { CurrentTime = Time.ActualDateTime() });
        }
        catch (Exception ex)
        {
            Infrastructure.RateLimitedLog.Error("expire.admins", ex, "Unable to remove expired admins");
        }
    }
}
