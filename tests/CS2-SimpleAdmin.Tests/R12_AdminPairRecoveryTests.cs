using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// Third review, T3 and T4: the groups.json / admins.json pair and its journal. The first three tests are the
/// reviewer's reproductions (assertions unchanged); the rest walk every state a process crash can leave behind.
/// <para>
/// Crash states are not simulated by killing a process. The commit has a hook that is called at each named stage; a test
/// copies the directory at that instant (an exact image of what a kill there would leave on disk), lets the commit
/// finish, and then runs recovery on every image. This covers "after every replacement" and "after every cleanup stage"
/// without relying on private methods.
/// </para>
/// </summary>
public class R12_AdminPairRecoveryTests
{
    private const string Journal = "admin-pair.journal";

    private static PermissionManager.PreparedAdminReload NewPair() => new("new-groups", true, "new-admins", true, []);

    private sealed class PairDir : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "sa_pair_r12_" + Guid.NewGuid().ToString("N"));
        public string Groups => Path.Combine(Dir, "groups.json");
        public string Admins => Path.Combine(Dir, "admins.json");
        private readonly Action<string, string> _savedReplace = PermissionManager.ReplaceFile;
        private readonly TimeSpan _savedTimeout = PermissionManager.PairLockTimeout;

        public PairDir(bool withOriginals = true)
        {
            Directory.CreateDirectory(Dir);
            if (!withOriginals) return;
            File.WriteAllText(Groups, "old-groups");
            File.WriteAllText(Admins, "old-admins");
        }

        public PairDir(Dictionary<string, byte[]> image)
        {
            Directory.CreateDirectory(Dir);
            foreach (var (name, bytes) in image) File.WriteAllBytes(Path.Combine(Dir, name), bytes);
        }

        /// <summary>The permanent lock file is not litter.</summary>
        public string[] Files => Directory.GetFiles(Dir).Select(f => Path.GetFileName(f)!).Where(f => f != "admin-pair.lock").Order().ToArray();

        public string? Read(string name) => File.Exists(Path.Combine(Dir, name)) ? File.ReadAllText(Path.Combine(Dir, name)) : null;

        /// <summary>Exact copy of every file except the lock (held by the running commit).</summary>
        public Dictionary<string, byte[]> Snapshot() =>
            Directory.GetFiles(Dir).Where(f => Path.GetFileName(f) != "admin-pair.lock")
                .ToDictionary(f => Path.GetFileName(f)!, File.ReadAllBytes);

        public void Dispose()
        {
            PermissionManager.ReplaceFile = _savedReplace;
            PermissionManager.PairLockTimeout = _savedTimeout;
            try { Directory.Delete(Dir, true); } catch { /* best effort */ }
        }
    }

    private static async Task<Exception?> RecoverFailureAsync(string dir) =>
        await Record.ExceptionAsync(() => new PermissionManager(null).RecoverAdminFilesAsync(dir));

    // =====================================================================================================
    // The reviewer's reproductions (ported from third-probes/ThirdProbes.cs)
    // =====================================================================================================

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    public async Task IncompleteJournalMustNotDeleteOriginalPermissionFiles(string journal)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sa_third_journal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in new[] { "groups", "admins" })
            {
                await File.WriteAllTextAsync(Path.Combine(dir, name + ".json"), "old-" + name);
                await File.WriteAllTextAsync(Path.Combine(dir, name + ".json.bak"), "old-" + name);
            }
            // Crash/cancellation after journal creation or a partial write, before replacements.
            await File.WriteAllTextAsync(Path.Combine(dir, "admin-pair.journal"), journal);
            await TryRecoveryWithoutInstallingNewPair(dir);
            Assert.True(File.Exists(Path.Combine(dir, "admins.json")), "Incomplete journal caused existing admins.json to be deleted");
            Assert.Equal("old-admins", await File.ReadAllTextAsync(Path.Combine(dir, "admins.json")));
            Assert.Equal("old-groups", await File.ReadAllTextAsync(Path.Combine(dir, "groups.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task InterruptedCleanupMustNotRestoreOnlyHalfOfPermissionPair()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sa_third_cleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "groups.json"), "new-groups");
            await File.WriteAllTextAsync(Path.Combine(dir, "admins.json"), "new-admins");
            await File.WriteAllTextAsync(Path.Combine(dir, "admins.json.bak"), "old-admins");
            await File.WriteAllTextAsync(Path.Combine(dir, "admin-pair.journal"), "1\n1");
            // Crash during DeleteJournalAndBackups: groups backup gone, admins backup + journal remain.
            await TryRecoveryWithoutInstallingNewPair(dir);
            var groups = await File.ReadAllTextAsync(Path.Combine(dir, "groups.json"));
            var admins = await File.ReadAllTextAsync(Path.Combine(dir, "admins.json"));
            Assert.True(groups.StartsWith("new-") == admins.StartsWith("new-"),
                $"Recovery produced a mixed permission pair: {groups} / {admins}; journal exists: {File.Exists(Path.Combine(dir, "admin-pair.journal"))}");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static async Task TryRecoveryWithoutInstallingNewPair(string dir)
    {
        // A real path obstacle fails the next temp write, after recovery. This exposes the recovered state
        // without relying on implementation-private methods or cancellation before recovery even starts.
        Directory.CreateDirectory(Path.Combine(dir, "groups.json.tmp"));
        var error = await Record.ExceptionAsync(() => new PermissionManager(null).CommitAdminFilesAsync(
            new PermissionManager.PreparedAdminReload("next-groups", true, "next-admins", true, []), dir));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected an I/O failure");
    }

    // =====================================================================================================
    // Every crash state of a commit recovers to one consistent version
    // =====================================================================================================

    private static async Task<List<(string Stage, Dictionary<string, byte[]> Image)>> CommitWithImagesAsync(PairDir d)
    {
        var images = new List<(string, Dictionary<string, byte[]>)>();
        var manager = new PermissionManager(null)
        {
            FaultHook = stage =>
            {
                images.Add((stage, d.Snapshot()));
                return Task.CompletedTask;
            }
        };
        await manager.CommitAdminFilesAsync(NewPair(), d.Dir);
        return images;
    }

    /// <summary>From the committed marker on, a crash must roll forward; everything before it rolls back.</summary>
    private static bool IsDeclaredCommitted(string stage) => stage == "journal-published:committed" || stage.StartsWith("cleanup:");

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // first ever commit: the originals do not exist
    public async Task EveryCrashStateRecoversToExactlyOneVersionOfBothFiles(bool withOriginals)
    {
        using var d = new PairDir(withOriginals);
        var images = await CommitWithImagesAsync(d);

        // the stages that matter exist: temp files, backups, both journal states, both replacements, every cleanup step
        var stages = images.Select(i => i.Stage).ToList();
        foreach (var expected in new[]
                 {
                     "temp-written:groups", "temp-written:admins", "journal-published:prepared", "replaced:groups", "replaced:admins",
                     "journal-published:committed", "cleanup:backup-deleted:groups", "cleanup:backup-deleted:admins", "cleanup:journal-deleted"
                 })
            Assert.Contains(expected, stages);
        if (withOriginals) Assert.Contains("backup-written:admins", stages);

        foreach (var (stage, image) in images)
        {
            using var crashed = new PairDir(image);
            var recovered = await new PermissionManager(null).RecoverAdminFilesAsync(crashed.Dir);

            var (groups, admins) = (crashed.Read("groups.json"), crashed.Read("admins.json"));
            if (IsDeclaredCommitted(stage))
            {
                Assert.Equal(("new-groups", "new-admins"), (groups, admins));
            }
            else if (withOriginals)
            {
                Assert.True(("old-groups", "old-admins") == (groups, admins), $"after a crash at '{stage}': {groups} / {admins}");
            }
            else
            {
                Assert.True((null, null) == (groups, admins), $"after a crash at '{stage}': {groups} / {admins}");
            }

            // a crash before the journal exists has nothing to repair; any later one repairs and removes its material
            var expectedResult = stage switch
            {
                _ when IsDeclaredCommitted(stage) && stage != "cleanup:journal-deleted" => PermissionManager.PairRecovery.RolledForward,
                _ when images.FindIndex(i => i.Stage == stage) < images.FindIndex(i => i.Stage == "journal-published:prepared") => PermissionManager.PairRecovery.None,
                "cleanup:journal-deleted" => PermissionManager.PairRecovery.None,
                _ => PermissionManager.PairRecovery.RolledBack
            };
            Assert.True(expectedResult == recovered, $"recovery result after a crash at '{stage}'");

            if (recovered != PermissionManager.PairRecovery.None)
                Assert.DoesNotContain(crashed.Files, f => f.EndsWith(".bak") || f == Journal); // recovery removed its own material

            // Recovery is idempotent, and the next commit works from whatever state it left
            Assert.Equal(PermissionManager.PairRecovery.None, await new PermissionManager(null).RecoverAdminFilesAsync(crashed.Dir));
            await new PermissionManager(null).CommitAdminFilesAsync(new PermissionManager.PreparedAdminReload("next-groups", true, "next-admins", true, []), crashed.Dir);
            Assert.Equal(("next-groups", "next-admins"), (crashed.Read("groups.json"), crashed.Read("admins.json")));
            Assert.Equal(["admins.json", "groups.json"], crashed.Files);
        }
    }

    [Fact]
    public async Task ACrashDuringRecoveryItselfIsRecoveredToo()
    {
        using var d = new PairDir();
        var images = await CommitWithImagesAsync(d);
        // groups already replaced, admins still old: the classic half-way state
        var halfWay = images.First(i => i.Stage == "replaced:groups").Image;

        using var first = new PairDir(halfWay);
        var recoveryImages = new List<(string Stage, Dictionary<string, byte[]> Image)>();
        await new PermissionManager(null)
        {
            FaultHook = stage =>
            {
                recoveryImages.Add((stage, first.Snapshot()));
                return Task.CompletedTask;
            }
        }.RecoverAdminFilesAsync(first.Dir);
        Assert.Contains(recoveryImages, i => i.Stage == "rolled-back:groups");
        Assert.Contains(recoveryImages, i => i.Stage == "rollback-journal-deleted");

        foreach (var (stage, image) in recoveryImages)
        {
            using var again = new PairDir(image);
            await new PermissionManager(null).RecoverAdminFilesAsync(again.Dir);
            Assert.True(("old-groups", "old-admins") == (again.Read("groups.json"), again.Read("admins.json")), $"crash during recovery at '{stage}'");
        }
    }

    [Fact]
    public async Task ATruncatedJournalOfAnyLengthIsRefusedAndNothingIsChanged()
    {
        using var d = new PairDir();
        var images = await CommitWithImagesAsync(d);
        // the state right after the journal was published: temp files, backups, journal, both originals
        var published = images.First(i => i.Stage == "journal-published:prepared").Image;
        var complete = published[Journal];
        Assert.True(complete.Length > 100);

        // every strict prefix a non-atomic write could have left; only the missing final newline is still a complete journal
        for (var length = 0; length < complete.Length - 1; length++)
        {
            var image = new Dictionary<string, byte[]>(published) { [Journal] = complete[..length] };
            using var crashed = new PairDir(image);
            var before = crashed.Snapshot();
            var error = await RecoverFailureAsync(crashed.Dir);

            var failure = Assert.IsType<PermissionManager.AdminFilesRecoveryException>(error);
            Assert.Contains(Journal, failure.Message);
            AssertUnchanged(before, crashed.Snapshot(), $"journal truncated to {length} bytes");
        }
    }

    private static void AssertUnchanged(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after, string why)
    {
        Assert.True(before.Keys.Order().SequenceEqual(after.Keys.Order()), $"{why}: the set of files changed: [{string.Join(", ", before.Keys)}] -> [{string.Join(", ", after.Keys)}]");
        foreach (var (name, bytes) in before)
            Assert.True(bytes.SequenceEqual(after[name]), $"{why}: {name} changed");
    }

    [Fact]
    public async Task AJournalThatWasNeverPublishedIsIgnored()
    {
        using var d = new PairDir();
        File.WriteAllText(Path.Combine(d.Dir, Journal + ".tmp"), "cs2sa-admin-pair 2\nstate prep"); // died while writing it
        var result = await new PermissionManager(null).RecoverAdminFilesAsync(d.Dir);
        Assert.Equal(PermissionManager.PairRecovery.None, result);
        Assert.Equal(("old-groups", "old-admins"), (d.Read("groups.json"), d.Read("admins.json")));
        Assert.DoesNotContain(Journal + ".tmp", d.Files);
    }

    // =====================================================================================================
    // Malformed, missing and mismatching recovery material: explicit failure, evidence kept
    // =====================================================================================================

    private static async Task<Dictionary<string, byte[]>> PreparedImageAsync(string stage)
    {
        using var d = new PairDir();
        return (await CommitWithImagesAsync(d)).First(i => i.Stage == stage).Image;
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("checksum")]
    [InlineData("state")]
    [InlineData("extra-line")]
    [InlineData("binary")]
    public async Task AMalformedManifestIsRefusedAndTheEvidenceStaysUntouched(string damage)
    {
        var image = await PreparedImageAsync("replaced:groups");
        var text = System.Text.Encoding.UTF8.GetString(image[Journal]);
        image[Journal] = damage switch
        {
            "magic" => System.Text.Encoding.UTF8.GetBytes(text.Replace("cs2sa-admin-pair 2", "cs2sa-admin-pair 3")),
            "checksum" => System.Text.Encoding.UTF8.GetBytes(text[..^2] + (text[^2] == '0' ? '1' : '0') + "\n"),
            "state" => System.Text.Encoding.UTF8.GetBytes(text.Replace("state prepared", "state finished")),
            "extra-line" => System.Text.Encoding.UTF8.GetBytes(text + "garbage\n"),
            _ => [0xFF, 0xFE, 0x00, 0x01, 0x02]
        };

        using var crashed = new PairDir(image);
        var before = crashed.Snapshot();
        var error = await RecoverFailureAsync(crashed.Dir);
        Assert.IsType<PermissionManager.AdminFilesRecoveryException>(error);
        AssertUnchanged(before, crashed.Snapshot(), damage);

        // a commit refuses too (no mixed pair, no false success) until somebody resolves it; then everything works
        var commit = await Record.ExceptionAsync(() => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), crashed.Dir));
        Assert.IsType<PermissionManager.AdminFilesRecoveryException>(commit);
        Assert.Equal(("new-groups", "old-admins"), (crashed.Read("groups.json"), crashed.Read("admins.json"))); // untouched: the half-way state as found
        Assert.True(File.Exists(Path.Combine(crashed.Dir, Journal)));

        File.Delete(Path.Combine(crashed.Dir, Journal));
        foreach (var bak in Directory.GetFiles(crashed.Dir, "*.bak")) File.Delete(bak);
        await new PermissionManager(null).CommitAdminFilesAsync(NewPair(), crashed.Dir);
        Assert.Equal(("new-groups", "new-admins"), (crashed.Read("groups.json"), crashed.Read("admins.json")));
    }

    [Fact]
    public async Task ACommittedJournalWhoseFilesDoNotMatchIsRefused()
    {
        var image = await PreparedImageAsync("journal-published:committed");
        image["admins.json"] = System.Text.Encoding.UTF8.GetBytes("somebody-edited-this");
        using var crashed = new PairDir(image);
        var before = crashed.Snapshot();
        Assert.IsType<PermissionManager.AdminFilesRecoveryException>(await RecoverFailureAsync(crashed.Dir));
        AssertUnchanged(before, crashed.Snapshot(), "committed journal, edited file");
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("admins")]
    public async Task AMissingRequiredBackupIsRefusedAndNothingIsRestoredHalfWay(string missing)
    {
        // groups already replaced by the new file, admins still old; one backup has disappeared
        var image = await PreparedImageAsync("replaced:groups");
        image.Remove(missing + ".json.bak");
        using var crashed = new PairDir(image);
        var before = crashed.Snapshot();

        if (missing == "admins")
        {
            // admins.json still equals the recorded original, so the pair is provably restorable from the groups backup alone
            await new PermissionManager(null).RecoverAdminFilesAsync(crashed.Dir);
            Assert.Equal(("old-groups", "old-admins"), (crashed.Read("groups.json"), crashed.Read("admins.json")));
            return;
        }

        // groups.json is the new file and its backup is gone: the original cannot be restored, so nothing is touched
        var error = await RecoverFailureAsync(crashed.Dir);
        Assert.IsType<PermissionManager.AdminFilesRecoveryException>(error);
        Assert.Contains("groups.json.bak", error!.Message);
        AssertUnchanged(before, crashed.Snapshot(), "missing groups backup");
        Assert.Equal(("new-groups", "old-admins"), (crashed.Read("groups.json"), crashed.Read("admins.json")));
    }

    [Fact]
    public async Task ABackupThatDoesNotMatchTheJournalIsRefused()
    {
        var image = await PreparedImageAsync("replaced:admins"); // both replaced
        image["groups.json.bak"] = System.Text.Encoding.UTF8.GetBytes("corrupted-backup");
        using var crashed = new PairDir(image);
        var before = crashed.Snapshot();
        var error = await RecoverFailureAsync(crashed.Dir);
        Assert.IsType<PermissionManager.AdminFilesRecoveryException>(error);
        Assert.Contains("does not match", error!.Message);
        AssertUnchanged(before, crashed.Snapshot(), "corrupted backup");
        Assert.Equal(("new-groups", "new-admins"), (crashed.Read("groups.json"), crashed.Read("admins.json")));
    }

    [Fact]
    public async Task AFileThatIsNeitherVersionIsNotOverwritten()
    {
        var image = await PreparedImageAsync("replaced:groups");
        image["admins.json"] = System.Text.Encoding.UTF8.GetBytes("hand-edited");
        using var crashed = new PairDir(image);
        var before = crashed.Snapshot();
        Assert.IsType<PermissionManager.AdminFilesRecoveryException>(await RecoverFailureAsync(crashed.Dir));
        AssertUnchanged(before, crashed.Snapshot(), "hand edited admins.json");
    }

    [Fact]
    public async Task WithoutOriginalsAFileThatIsNotOurNewOneIsNotDeleted()
    {
        using var source = new PairDir(withOriginals: false);
        var image = (await CommitWithImagesAsync(source)).First(i => i.Stage == "replaced:groups").Image;
        image["groups.json"] = System.Text.Encoding.UTF8.GetBytes("an-operator-wrote-this");
        using var crashed = new PairDir(image);
        var before = crashed.Snapshot();
        Assert.IsType<PermissionManager.AdminFilesRecoveryException>(await RecoverFailureAsync(crashed.Dir));
        AssertUnchanged(before, crashed.Snapshot(), "foreign groups.json");
    }

    [Fact]
    public async Task ARecoveryFailureNeverReachesTheApplyStepAndIsReportedAsFailed()
    {
        using var world = new TestWorld();
        var image = await PreparedImageAsync("replaced:groups");
        using var crashed = new PairDir(image);
        File.WriteAllText(Path.Combine(crashed.Dir, Journal), "1"); // truncated

        var applied = 0;
        var coordinator = new AdminReloadCoordinator(async _ =>
        {
            await new PermissionManager(null).CommitAdminFilesAsync(NewPair(), crashed.Dir);
            Interlocked.Increment(ref applied); // what ReloadAdminsOnceAsync does after the commit
        });
        var result = await coordinator.RequestAsync(Runtime.Context);
        Assert.Equal(AdminReloadResult.Failed, result);
        Assert.Equal(0, applied);
    }

    // =====================================================================================================
    // Failure and rollback during a commit
    // =====================================================================================================

    [Fact]
    public async Task RollbackFailureKeepsTheEvidenceAndTheNextCommitRepairsThePair()
    {
        using var d = new PairDir();
        var calls = 0;
        PermissionManager.ReplaceFile = (source, target) =>
        {
            if (++calls == 2)
            {
                // the second replacement fails AND the first file cannot be put back: a directory sits there
                File.Delete(d.Groups);
                Directory.CreateDirectory(d.Groups);
                throw new IOException("injected");
            }

            File.Move(source, target, true);
        };
        var failure = await Assert.ThrowsAsync<PermissionManager.AdminFilesInconsistentException>(
            () => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir));
        Assert.IsType<IOException>(failure.InnerException);
        Assert.True(File.Exists(Path.Combine(d.Dir, Journal)));
        Assert.True(File.Exists(d.Groups + ".bak"));
        Assert.True(File.Exists(d.Admins + ".bak"));

        // the obstacle stays: recovery cannot restore either, and again says so (journal kept)
        var again = await Record.ExceptionAsync(() => new PermissionManager(null).RecoverAdminFilesAsync(d.Dir));
        Assert.IsAssignableFrom<IOException>(again);
        Assert.True(File.Exists(Path.Combine(d.Dir, Journal)));

        Directory.Delete(d.Groups);
        PermissionManager.ReplaceFile = static (source, target) => File.Move(source, target, true);
        await new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir); // retry succeeds
        Assert.Equal(("new-groups", "new-admins"), (d.Read("groups.json"), d.Read("admins.json")));
        Assert.Equal(["admins.json", "groups.json"], d.Files);
    }

    [Fact]
    public async Task AFailureToPublishTheCommittedMarkerRestoresTheOriginals()
    {
        using var d = new PairDir();
        var manager = new PermissionManager(null)
        {
            // after both replacements, make the next journal publication impossible (a directory where its temp file goes)
            FaultHook = stage =>
            {
                if (stage == "replaced:admins") Directory.CreateDirectory(Path.Combine(d.Dir, Journal + ".tmp"));
                return Task.CompletedTask;
            }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => manager.CommitAdminFilesAsync(NewPair(), d.Dir));
        Assert.Equal(("old-groups", "old-admins"), (d.Read("groups.json"), d.Read("admins.json")));
        Assert.False(File.Exists(Path.Combine(d.Dir, Journal)));
        Assert.DoesNotContain(d.Files, f => f.EndsWith(".bak"));
    }

    [Fact]
    public async Task CancellationAfterTheJournalButBeforeTheFirstReplacementAbandonsCleanly()
    {
        using var d = new PairDir();
        using var cts = new CancellationTokenSource();
        var manager = new PermissionManager(null)
        {
            FaultHook = stage =>
            {
                if (stage == "journal-published:prepared") cts.Cancel();
                return Task.CompletedTask;
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.CommitAdminFilesAsync(NewPair(), d.Dir, cts.Token));
        Assert.Equal(("old-groups", "old-admins"), (d.Read("groups.json"), d.Read("admins.json")));
        Assert.Equal(["admins.json", "groups.json"], d.Files); // no journal, backup or temp file
    }

    [Theory]
    [InlineData("replaced:groups")]
    [InlineData("replaced:admins")]
    [InlineData("journal-published:committed")]
    [InlineData("cleanup:backup-deleted:groups")]
    public async Task CancellationFromTheFirstReplacementOnStillCompletesTheWholePair(string cancelAt)
    {
        using var d = new PairDir();
        using var cts = new CancellationTokenSource();
        var manager = new PermissionManager(null)
        {
            FaultHook = stage =>
            {
                if (stage == cancelAt) cts.Cancel();
                return Task.CompletedTask;
            }
        };
        await manager.CommitAdminFilesAsync(NewPair(), d.Dir, cts.Token);
        Assert.Equal(("new-groups", "new-admins"), (d.Read("groups.json"), d.Read("admins.json")));
        Assert.Equal(["admins.json", "groups.json"], d.Files);
    }

    [Fact]
    public async Task ACleanupFailureAfterTheCommitIsNotAFailureAndIsFinishedByTheNextReload()
    {
        using var d = new PairDir();
        // a stray directory where a backup is deleted makes File.Delete fail, once
        var manager = new PermissionManager(null)
        {
            FaultHook = stage =>
            {
                if (stage == "journal-published:committed")
                {
                    File.Delete(d.Groups + ".bak");
                    Directory.CreateDirectory(d.Groups + ".bak");
                }

                return Task.CompletedTask;
            }
        };
        await manager.CommitAdminFilesAsync(NewPair(), d.Dir); // the pair is correct; leftovers are only logged
        Assert.Equal(("new-groups", "new-admins"), (d.Read("groups.json"), d.Read("admins.json")));
        Assert.True(File.Exists(Path.Combine(d.Dir, Journal))); // still says "committed"

        Directory.Delete(d.Groups + ".bak");
        Assert.Equal(PermissionManager.PairRecovery.RolledForward, await new PermissionManager(null).RecoverAdminFilesAsync(d.Dir));
        Assert.Equal(["admins.json", "groups.json"], d.Files);
        Assert.Equal(("new-groups", "new-admins"), (d.Read("groups.json"), d.Read("admins.json")));
    }

    [Fact]
    public async Task SuccessfulRetryAfterAFailedCommitProducesTheNewPair()
    {
        using var d = new PairDir();
        PermissionManager.ReplaceFile = (_, _) => throw new IOException("injected");
        await Assert.ThrowsAsync<IOException>(() => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir));
        Assert.Equal(("old-groups", "old-admins"), (d.Read("groups.json"), d.Read("admins.json")));
        PermissionManager.ReplaceFile = static (source, target) => File.Move(source, target, true);
        await new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir);
        Assert.Equal(("new-groups", "new-admins"), (d.Read("groups.json"), d.Read("admins.json")));
    }

    // =====================================================================================================
    // Serialisation between reloads and plugin generations
    // =====================================================================================================

    [Fact]
    public async Task ConcurrentCommitsNeverMixVersions()
    {
        using var d = new PairDir();
        var commits = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            new PermissionManager(null).CommitAdminFilesAsync(
                new PermissionManager.PreparedAdminReload($"groups-{i}", true, $"admins-{i}", true, []), d.Dir))).ToArray();
        await Task.WhenAll(commits).WaitAsync(TimeSpan.FromSeconds(30));

        var (groups, admins) = (d.Read("groups.json")!, d.Read("admins.json")!);
        Assert.Equal(groups["groups-".Length..], admins["admins-".Length..]); // same generation number in both files
        Assert.Equal(["admins.json", "groups.json"], d.Files);
    }

    [Fact]
    public async Task ACommitWaitsAsynchronouslyForTheLockHolderAndThenProceeds()
    {
        using var d = new PairDir();
        var lockStream = new FileStream(Path.Combine(d.Dir, "admin-pair.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task commit;
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            commit = new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir);
            Assert.True(started.ElapsedMilliseconds < 500, "the call returned a pending task instead of blocking its caller");
            await Task.Delay(150);
            Assert.False(commit.IsCompleted);
            Assert.Equal(("old-groups", "old-admins"), (d.Read("groups.json"), d.Read("admins.json"))); // nothing happens while the lock is held
        }
        finally { lockStream.Dispose(); }

        await commit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("new-groups", "new-admins"), (d.Read("groups.json"), d.Read("admins.json")));
    }

    [Fact]
    public async Task ALockThatIsNeverReleasedFailsTheReloadWithoutTouchingTheFiles()
    {
        using var d = new PairDir();
        PermissionManager.PairLockTimeout = TimeSpan.FromMilliseconds(200);
        using var lockStream = new FileStream(Path.Combine(d.Dir, "admin-pair.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var error = await Record.ExceptionAsync(() => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir));
        Assert.IsAssignableFrom<IOException>(error);
        Assert.Contains("still committing", error!.Message);
        Assert.Equal(("old-groups", "old-admins"), (d.Read("groups.json"), d.Read("admins.json")));
        Assert.Equal(["admins.json", "groups.json"], d.Files);
    }

    [Fact]
    public async Task WaitingForTheLockHonoursCancellation()
    {
        using var d = new PairDir();
        using var lockStream = new FileStream(Path.Combine(d.Dir, "admin-pair.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PermissionManager(null).CommitAdminFilesAsync(NewPair(), d.Dir, cts.Token));
    }
}
