using System.Reflection;
using System.Runtime.CompilerServices;
using CS2_SimpleAdmin.Database;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;

namespace CS2_SimpleAdmin.Tests;

/// <summary>The native engine does not exist in unit tests: console/chat output defaults to harmless no-ops.</summary>
internal static class TestEngineDefaults
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        CallerRef.PrintToConsole = static _ => { };
        CallerRef.PrintToChat = static (_, _) => { };
        CallerRef.Lookup = static _ => null;
    }
}

/// <summary>
/// A fresh runtime for one test: queues + a dispatcher that only runs when the test pumps it (the test thread plays
/// the game thread). Disposing stops the runtime and clears everything the tests touch statically.
/// </summary>
internal sealed class TestWorld : IDisposable
{
    public FakeWorldUpdates World { get; } = new();

    public TestWorld(bool sqlite = true, PluginState state = PluginState.Ready, int? serverId = 1, int dbCapacity = Runtime.DbQueueCapacity)
    {
        Runtime.ResetForTests(sqlite, new GameDispatcher(World.Schedule), dbCapacity);
        Runtime.State = state;
        CS2_SimpleAdmin.GlobalServerId = serverId;
        Runtime.Sessions.Clear();
        CS2_SimpleAdmin.PlayersInfo.Clear();
        PlayerPenaltyManager.RemoveAllPenalties();
    }

    public void Dispose()
    {
        Runtime.Stop();
        CS2_SimpleAdmin.GlobalServerId = null;
        CS2_SimpleAdmin.DatabaseProvider = null;
        PlayerManager.RetryScheduler = static (_, _) => { };
        PlayerManager.ControllerAvailable = static s => PlayerManager.ResolveController(s) != null;
    }

    /// <summary>Pumps game-thread work until <paramref name="done"/> (background work may still be producing it).</summary>
    public Task PumpUntil(Func<bool> done, int timeoutMs = 5_000) => World.PumpUntil(done, timeoutMs);

    public static async Task WaitUntil(Func<bool> done, int timeoutMs = 5_000)
    {
        var limit = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > limit) throw new TimeoutException("condition not reached");
            await Task.Delay(2);
        }
    }
}

/// <summary>
/// Installs a plugin object without running BasePlugin's native constructor, so code paths that use
/// <c>CS2_SimpleAdmin.Instance</c> (managers, config) work in unit tests. Restores the previous instance on dispose.
/// </summary>
internal sealed class TestPlugin : IDisposable
{
    private static readonly FieldInfo InstanceField =
        typeof(CS2_SimpleAdmin).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;

    private readonly object? _previous;

    public CS2_SimpleAdmin Plugin { get; }

    public TestPlugin(CS2_SimpleAdminConfig config, IDatabaseProvider provider)
    {
        _previous = InstanceField.GetValue(null);
        Plugin = (CS2_SimpleAdmin)RuntimeHelpers.GetUninitializedObject(typeof(CS2_SimpleAdmin));
        Plugin.Config = config;
        Plugin.CacheManager = new CacheManager();
        Plugin.MuteManager = new MuteManager(provider);
        Plugin.BanManager = new BanManager(provider);
        Plugin.WarnManager = new WarnManager(provider);
        Plugin.PermissionManager = new PermissionManager(provider);
        InstanceField.SetValue(null, Plugin);
        CS2_SimpleAdmin.DatabaseProvider = provider;
    }

    public void Dispose()
    {
        Plugin.CacheManager?.Dispose();
        InstanceField.SetValue(null, _previous);
        CS2_SimpleAdmin.DatabaseProvider = null;
    }
}

/// <summary>Provider that fails to open connections (a database outage).</summary>
internal sealed class OutageProvider : FakeProviderBase
{
    public int Attempts;

    public override Task<System.Data.Common.DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Attempts);
        return Task.FromException<System.Data.Common.DbConnection>(new IOException("temporary DB outage"));
    }
}

/// <summary>
/// Real provider (SQLite or MySQL) whose individual queries can be broken on demand, to inject a failure into one
/// specific step of an operation.
/// </summary>
internal sealed class FlakyQueriesProvider(IDatabaseProvider inner) : FakeProviderBase
{
    public volatile bool FailStats;
    public volatile bool FailMutes;
    public volatile bool FailExpiredRead;
    public bool FailPlanRead { get; set; }
    public volatile bool FailAdmins;
    public volatile bool FailGroups;
    public int Connections;

    private const string Broken = "SELECT * FROM deliberately_missing_table";

    public override Task<System.Data.Common.DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Connections);
        return inner.CreateConnectionAsync(cancellationToken);
    }

    public override string GetPlayerPenaltyStatsQuery() => FailStats ? Broken : inner.GetPlayerPenaltyStatsQuery();
    public override string GetIsMutedQuery(int timeMode) => FailMutes ? Broken : inner.GetIsMutedQuery(timeMode);
    public override string GetActiveMutesBatchQuery(int timeMode) => FailMutes ? Broken : inner.GetActiveMutesBatchQuery(timeMode);
    public volatile bool FailSteamBans;
    public volatile bool FailIpSave;
    public override string GetUpsertPlayerIpQuery() => FailIpSave ? Broken : inner.GetUpsertPlayerIpQuery();
    public override string GetActiveSteamBansQuery() => FailSteamBans ? Broken : inner.GetActiveSteamBansQuery();
    public override string GetActiveSteamBansBatchQuery() => inner.GetActiveSteamBansBatchQuery();
    public override string GetExpiredOnlineMutesBatchQuery() => FailExpiredRead ? Broken : inner.GetExpiredOnlineMutesBatchQuery();
    public override string GetOnlineCreditPlanQuery() => FailPlanRead ? Broken : inner.GetOnlineCreditPlanQuery();
    public override string GetApplyOnlineCreditQuery(IReadOnlyList<OnlineCreditStep> steps) => inner.GetApplyOnlineCreditQuery(steps);
    public override string GetAdminsQuery() => FailAdmins ? Broken : inner.GetAdminsQuery();
    public override string GetGroupsQuery() => FailGroups ? Broken : inner.GetGroupsQuery();
    public override string GetAddMuteQuery(bool includePlayerName) => inner.GetAddMuteQuery(includePlayerName);
    public override string GetAddBanQuery() => inner.GetAddBanQuery();
    public override string GetAddBanBySteamIdQuery() => inner.GetAddBanBySteamIdQuery();
}
