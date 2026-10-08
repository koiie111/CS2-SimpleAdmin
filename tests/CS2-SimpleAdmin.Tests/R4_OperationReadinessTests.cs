using CounterStrikeSharp.API.Modules.Entities;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdminApi;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// R4: "database connected" is not "ready for server-scoped operations". Mandatory server-scoped writes need Ready AND a
/// resolved server id, and the id is captured when the write is accepted. Deliberately global writes are an explicit,
/// separate contract (<see cref="OperationScope.Global"/>).
/// </summary>
public class R4_OperationReadinessTests
{
    // ---- ported review probe (R4) ----
    [Fact]
    public void DatabaseReadyWithoutServerIdMustRefusePenaltyWrites()
    {
        using var world = new TestWorld(state: PluginState.DatabaseReady, serverId: null);
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        Assert.False(CS2_SimpleAdmin.EnsureDatabaseReady(null), "DatabaseReady permits writes while server_id is still null");
    }

    [Fact]
    public void ReadyWithoutResolvedServerIdIsStillRefused()
    {
        using var world = new TestWorld(state: PluginState.Ready, serverId: null);
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        Assert.False(CS2_SimpleAdmin.EnsureDatabaseReady(null));
        Assert.True(CS2_SimpleAdmin.EnsureDatabaseReady(null, OperationScope.Global)); // connected is enough for global work
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("Failed")]
    public void StartingAndFailedRefuseEveryScope(string stateName)
    {
        using var world = new TestWorld(state: Enum.Parse<PluginState>(stateName), serverId: 1);
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        Assert.False(CS2_SimpleAdmin.EnsureDatabaseReady(null));
        Assert.False(CS2_SimpleAdmin.EnsureDatabaseReady(null, OperationScope.Global));
    }

    [Fact]
    public void ReadyWithServerIdAcceptsBothScopes()
    {
        using var world = new TestWorld(state: PluginState.Ready, serverId: 4);
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        Assert.True(CS2_SimpleAdmin.EnsureDatabaseReady(null));
        Assert.True(CS2_SimpleAdmin.EnsureDatabaseReady(null, OperationScope.Global));
    }

    [Fact]
    public async Task RefusedWriteIsNeitherQueuedNorRunNorChangesState()
    {
        using var world = new TestWorld(state: PluginState.DatabaseReady, serverId: null);
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        var ran = 0;
        var accepted = CS2_SimpleAdmin.TryQueuePenaltyWork(CallerRef.Console, null, "ban-write",
            _ => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        await Task.Delay(30);

        Assert.False(accepted);
        Assert.Equal(0, ran);
        Assert.Equal(0, Runtime.Db!.Pending);
        Assert.Equal(0, Runtime.Db.Completed);
        Assert.Equal(PluginState.DatabaseReady, Runtime.State);
        Assert.Null(CS2_SimpleAdmin.GlobalServerId);
    }

    [Fact]
    public async Task RefusedWriteNotifiesTheCallerOnTheCallingThread()
    {
        using var world = new TestWorld(state: PluginState.DatabaseReady, serverId: null);
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        var messages = new List<string>();
        var oldPrint = CallerRef.PrintToConsole;
        CallerRef.PrintToConsole = messages.Add;
        try
        {
            Assert.False(CS2_SimpleAdmin.TryQueuePenaltyWork(CallerRef.Console, null, "mute-write", _ => Task.CompletedTask));
            Assert.Contains(messages, m => m.Contains("NOT saved"));
        }
        finally { CallerRef.PrintToConsole = oldPrint; }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task GlobalScopeIsAcceptedBeforeTheServerRowIsKnown()
    {
        using var world = new TestWorld(state: PluginState.DatabaseReady, serverId: null);
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = CS2_SimpleAdmin.TryQueuePenaltyWork(CallerRef.Console, null, "rename-save",
            _ => { ran.SetResult(); return Task.CompletedTask; }, OperationScope.Global);
        Assert.True(accepted);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<(TestDatabase Db, PlayerInfo Player)> Sqlite()
    {
        var db = await TestDatabases.CreateAsync("SQLite");
        TestConfig.Use(c =>
        {
            c.DatabaseConfig.DatabaseType = "SQLite";
            c.MultiServerMode = false;
        });
        return (db, new PlayerInfo(1, 1, new SteamID(76561198000000007), "target", null));
    }

    [Fact]
    public async Task AcceptedWriteLandsWithTheServerIdCapturedAtAcceptanceAndIsFoundAfterReconnect()
    {
        var (db, player) = await Sqlite();
        await using var _ = db;
        using var world = new TestWorld(state: PluginState.Ready, serverId: 7);
        using var plugin = new TestPlugin(TestConfig.Use(c => { c.DatabaseConfig.DatabaseType = "SQLite"; c.MultiServerMode = false; }), db.Provider);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(CS2_SimpleAdmin.TryQueuePenaltyWork(CallerRef.Console, null, "mute-write", async _ =>
        {
            await gate.Task; // the write is delayed...
            var id = await plugin.Plugin.MuteManager.MutePlayer(player, null, "reason", 60);
            done.SetResult();
            Assert.NotNull(id);
        }));
        CS2_SimpleAdmin.GlobalServerId = 8; // ...and the global server id changes meanwhile (e.g. a re-resolve)
        gate.SetResult();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await using var c = await db.OpenAsync();
        Assert.Equal(7, await c.ExecuteScalarAsync<int>("SELECT server_id FROM sa_mutes WHERE player_steamid = '76561198000000007'"));

        // "after reconnect": found whatever server id is recorded in the row (it is information, not a filter)
        var active = await plugin.Plugin.MuteManager.GetActiveMutesAsync(76561198000000007, 1, DateTime.UtcNow, default);
        Assert.Single(active);
    }

    [Fact]
    public async Task WriteAttemptedWhileOnlyDatabaseReadyLeavesNoRowWithNullServerId()
    {
        var (db, player) = await Sqlite();
        await using var _ = db;
        using var world = new TestWorld(state: PluginState.DatabaseReady, serverId: null);
        using var plugin = new TestPlugin(TestConfig.Use(c => { c.DatabaseConfig.DatabaseType = "SQLite"; c.MultiServerMode = false; }), db.Provider);

        var accepted = CS2_SimpleAdmin.TryQueuePenaltyWork(CallerRef.Console, null, "mute-write",
            _ => plugin.Plugin.MuteManager.MutePlayer(player, null, "reason", 60));
        await Task.Delay(50);

        Assert.False(accepted);
        await using var c = await db.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sa_mutes"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sa_mutes WHERE server_id IS NULL"));
    }
}
