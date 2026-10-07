using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// R9: an admin reload ends as Success, Failed or Canceled (never a failure disguised as success); startup retries a
/// bounded number of times and then fails instead of declaring the plugin ready; the old permissions and files stay
/// intact when a read or a file write fails; the coalescing gate is released on every outcome.
/// </summary>
public class R9_AdminReloadTests
{
    private static RuntimeContext Current() => Runtime.Context;

    private static async Task<T> Within<T>(Task<T> task, int ms = 3000)
    {
        var finished = await Task.WhenAny(task, Task.Delay(ms));
        Assert.True(ReferenceEquals(finished, task), "reload did not complete");
        return await task;
    }

    [Fact]
    public async Task SuccessFailureAndCancellationAreDistinguishable()
    {
        using var world = new TestWorld();
        var outcome = 0;
        var errors = new List<Exception>();
        var coordinator = new AdminReloadCoordinator(_ => outcome switch
        {
            0 => Task.CompletedTask,
            1 => Task.FromException(new IOException("groups.json is locked")),
            _ => Task.FromCanceled(new CancellationToken(true))
        }, errors.Add);

        Assert.Equal(AdminReloadResult.Success, await Within(coordinator.RequestAsync(Current())));
        outcome = 1;
        Assert.Equal(AdminReloadResult.Failed, await Within(coordinator.RequestAsync(Current())));
        Assert.Single(errors);
        outcome = 2;
        Assert.Equal(AdminReloadResult.Canceled, await Within(coordinator.RequestAsync(Current())));
        Assert.False(coordinator.IsBusy); // every outcome released the gate
        outcome = 0;
        Assert.Equal(AdminReloadResult.Success, await Within(coordinator.RequestAsync(Current()))); // and it still works
    }

    [Fact]
    public async Task RequestsDuringAReloadShareOneFollowUpAndEachGetsTheOutcomeOfItsOwnReload()
    {
        using var world = new TestWorld();
        var runs = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AdminReloadCoordinator(async _ =>
        {
            var n = Interlocked.Increment(ref runs);
            if (n == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
                throw new IOException("first reload fails");
            }
        });

        var first = coordinator.RequestAsync(Current());
        await firstStarted.Task;
        var coalesced = Enumerable.Range(0, 20).Select(_ => coordinator.RequestAsync(Current())).ToList();
        releaseFirst.SetResult();

        Assert.Equal(AdminReloadResult.Failed, await Within(first));
        foreach (var task in coalesced) Assert.Equal(AdminReloadResult.Success, await Within(task)); // served by the follow-up
        Assert.Equal(2, runs); // 21 requests, 2 reloads
        Assert.Equal(20, coordinator.Coalesced);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task AThrowingErrorCallbackCannotLeaveTheGateBusy()
    {
        using var world = new TestWorld();
        var coordinator = new AdminReloadCoordinator(_ => throw new InvalidOperationException("x"),
            _ => throw new InvalidOperationException("the logger failed too"));
        Assert.Equal(AdminReloadResult.Failed, await Within(coordinator.RequestAsync(Current())));
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task ARequestFromAStaleContextIsCanceledWithoutRunningAReload()
    {
        using var world = new TestWorld();
        var stale = Current();
        using var newWorld = new TestWorld();
        var ran = 0;
        var coordinator = new AdminReloadCoordinator(_ => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        Assert.Equal(AdminReloadResult.Canceled, await Within(coordinator.RequestAsync(stale)));
        Assert.Equal(0, ran);
    }

    // ---- startup ----
    [Fact]
    public async Task StartupRetriesAFailedReloadAndThenSucceeds()
    {
        using var world = new TestWorld();
        var results = new Queue<AdminReloadResult>([AdminReloadResult.Failed, AdminReloadResult.Failed, AdminReloadResult.Success]);
        var calls = 0;
        await ServerManager.ReloadAdminsWithRetriesAsync(() => { calls++; return Task.FromResult(results.Dequeue()); },
            Current(), [TimeSpan.Zero, TimeSpan.Zero]);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task StartupGivesUpAfterABoundedNumberOfFailuresInsteadOfBecomingReady()
    {
        using var world = new TestWorld();
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerManager.ReloadAdminsWithRetriesAsync(
            () => { calls++; return Task.FromResult(AdminReloadResult.Failed); }, Current(), [TimeSpan.Zero, TimeSpan.Zero]));
        Assert.Equal(3, calls); // 1 + 2 retries, then it stops
    }

    [Fact]
    public async Task StartupTreatsCancellationAsCancellationNotAsSuccess()
    {
        using var world = new TestWorld();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServerManager.ReloadAdminsWithRetriesAsync(
            () => Task.FromResult(AdminReloadResult.Canceled), Current(), [TimeSpan.Zero]));
    }

    // ---- files and permissions stay intact ----
    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db;
        public required FlakyQueriesProvider Provider;
        public required PermissionManager Manager;
        public required string Dir;

        public string Groups => Path.Combine(Dir, "groups.json");
        public string Admins => Path.Combine(Dir, "admins.json");

        public static async Task<Fixture> Create()
        {
            var db = await TestDatabases.CreateAsync("SQLite");
            var provider = new FlakyQueriesProvider(db.Provider);
            var dir = Path.Combine(Path.GetTempPath(), "sa_admin_reload_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "groups.json"), "{\"old-group\":{}}");
            await File.WriteAllTextAsync(Path.Combine(dir, "admins.json"), "{\"old-admin\":{}}");
            await using (var c = await db.OpenAsync())
            {
                await c.ExecuteAsync("INSERT INTO sa_admins (player_name, player_steamid, immunity, server_id) VALUES ('new-admin', '76561198000000001', 5, NULL)");
                await c.ExecuteAsync("INSERT INTO sa_admins_flags (admin_id, flag) VALUES (1, '@css/ban')");
                await c.ExecuteAsync("INSERT INTO sa_groups (name, immunity) VALUES ('new-group', 3)");
                await c.ExecuteAsync("INSERT INTO sa_groups_servers (group_id, server_id) VALUES (1, NULL)");
                await c.ExecuteAsync("INSERT INTO sa_groups_flags (group_id, flag) VALUES (1, '@css/kick')");
            }

            TestConfig.Use(c => { c.DatabaseConfig.DatabaseType = "SQLite"; c.MultiServerMode = true; });
            return new Fixture { Db = db, Provider = provider, Manager = new PermissionManager(provider), Dir = dir };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try { Directory.Delete(Dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SuccessfulPrepareAndCommitReplaceBothFiles()
    {
        await using var f = await Fixture.Create();
        var prepared = await f.Manager.PrepareAdminReloadAsync();
        Assert.True(prepared.AdminsWritten);
        Assert.True(prepared.GroupsWritten);
        Assert.Single(prepared.Admins);
        await f.Manager.CommitAdminFilesAsync(prepared, f.Dir);
        Assert.Contains("new-admin", await File.ReadAllTextAsync(f.Admins));
        Assert.Contains("new-group", await File.ReadAllTextAsync(f.Groups));
        Assert.False(File.Exists(f.Admins + ".tmp"));
    }

    [Fact]
    public async Task AdminsReadFailureLeavesBothFilesAsTheyWere()
    {
        await using var f = await Fixture.Create();
        f.Provider.FailAdmins = true;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Manager.PrepareAdminReloadAsync()); // groups were readable, admins were not
        Assert.Equal("{\"old-group\":{}}", await File.ReadAllTextAsync(f.Groups)); // the groups file was NOT rewritten
        Assert.Equal("{\"old-admin\":{}}", await File.ReadAllTextAsync(f.Admins));
    }

    [Fact]
    public async Task GroupsReadFailureLeavesBothFilesAsTheyWere()
    {
        await using var f = await Fixture.Create();
        f.Provider.FailGroups = true;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Manager.PrepareAdminReloadAsync());
        Assert.Equal("{\"old-group\":{}}", await File.ReadAllTextAsync(f.Groups));
        Assert.Equal("{\"old-admin\":{}}", await File.ReadAllTextAsync(f.Admins));
    }

    [Fact]
    public async Task AFileWriteFailureLeavesTheOriginalsInPlace()
    {
        await using var f = await Fixture.Create();
        var prepared = await f.Manager.PrepareAdminReloadAsync();
        Directory.CreateDirectory(f.Admins + ".tmp"); // the admins temp file cannot be created
        await Assert.ThrowsAnyAsync<Exception>(() => f.Manager.CommitAdminFilesAsync(prepared, f.Dir));
        Assert.Equal("{\"old-group\":{}}", await File.ReadAllTextAsync(f.Groups)); // not half-replaced
        Assert.Equal("{\"old-admin\":{}}", await File.ReadAllTextAsync(f.Admins));
    }

    [Fact]
    public async Task ARecoveredDatabaseAllowsTheNextReloadToSucceed()
    {
        await using var f = await Fixture.Create();
        f.Provider.FailAdmins = true;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Manager.PrepareAdminReloadAsync());
        f.Provider.FailAdmins = false;
        var prepared = await f.Manager.PrepareAdminReloadAsync();
        await f.Manager.CommitAdminFilesAsync(prepared, f.Dir);
        Assert.Contains("new-admin", await File.ReadAllTextAsync(f.Admins));
    }
}
