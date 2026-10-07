using CounterStrikeSharp.API.Modules.Cvars;
using CS2_SimpleAdmin.Infrastructure;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin.Managers;

public class ServerManager
{
    private int _getIpTryCount;
    private static readonly TimeSpan[] StepRetryDelays = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];

    /// <summary>
    /// Checks whether the server setting <c>sv_hibernate_when_empty</c> is enabled.
    /// Logs an error if this setting is true, since it prevents the plugin from working properly.
    /// </summary>
    public static void CheckHibernationStatus()
    {
        var convar = ConVar.Find("sv_hibernate_when_empty");
        if (convar == null || !convar.GetPrimitiveValue<bool>())
            return;

        CS2_SimpleAdmin._logger?.LogError("Detected setting \"sv_hibernate_when_empty true\", set false to make plugin work properly");
    }

    /// <summary>
    /// Reads the server address/hostname/RCON password on the game thread (retrying while the IP is not known yet),
    /// then resolves the server row, builds the bans cache and loads admins in the background. Only after all of that
    /// succeeded is the plugin marked Ready (and OnSimpleAdminReady raised, once per plugin lifetime).
    /// On failure the loading flag is released so the next map start retries.
    /// </summary>
    public void LoadServerData()
    {
        CS2_SimpleAdmin.Instance.AddTimer(1.0f, () =>
        {
            if (CS2_SimpleAdmin.ServerLoaded || CS2_SimpleAdmin.DatabaseProvider == null) return;

            // Optimization: Get server IP once and reuse
            var serverIp = Helper.GetServerIp();
            var isInvalidIp = string.IsNullOrEmpty(serverIp) || serverIp.StartsWith("0.0.0");

            // Check if we've exceeded retry limit with invalid IP
            if (_getIpTryCount > 32 && isInvalidIp)
            {
                CS2_SimpleAdmin._logger?.LogError("Unable to load server data - can't fetch ip address!");
                _ = LoadWithoutServerRowAsync(); // still load global admins and renames
                return;
            }

            // Optimization: Cache ConVar lookups
            var ipConVar = ConVar.Find("ip");
            var ipAddress = ipConVar?.StringValue;

            // Use Helper IP if ConVar IP is invalid
            if (string.IsNullOrEmpty(ipAddress) || ipAddress.StartsWith("0.0.0"))
            {
                ipAddress = serverIp;

                // Retry if still invalid and under retry limit
                if (_getIpTryCount <= 32 && isInvalidIp)
                {
                    _getIpTryCount++;
                    LoadServerData();
                    return;
                }
            }

            // Native ConVar reads happen here, on the game thread; the background only gets these strings.
            var hostportConVar = ConVar.Find("hostport");
            var hostnameConVar = ConVar.Find("hostname");
            var rconPasswordConVar = ConVar.Find("rcon_password");

            var address = $"{ipAddress}:{hostportConVar?.GetPrimitiveValue<int>()}";
            var hostname = hostnameConVar?.StringValue ?? CS2_SimpleAdmin._localizer?["sa_unknown"] ?? "Unknown";
            var rconPassword = rconPasswordConVar?.StringValue ?? "";
            CS2_SimpleAdmin.IpAddress = address;

            _ = LoadAsync(address, ipAddress, hostname, rconPassword);
        });
    }

    private static async Task LoadAsync(string address, string? ipAddress, string hostname, string rconPassword)
    {
        var plugin = CS2_SimpleAdmin.Instance;
        // Startup belongs to the lifetime that began it: everything below (queue, game-thread posts, server id, state)
        // goes through this context, so after a restart it cannot reach into the new lifetime.
        var context = Runtime.Context;
        var token = context.Token;
        try
        {
            if (!await plugin.DatabaseInitTask.ConfigureAwait(false))
            {
                await ReleaseLoadingAsync(context).ConfigureAwait(false);
                return;
            }

            var serverId = await WithRetries(context, "server-row", ct => ResolveServerIdAsync(address, hostname, rconPassword, ct))
                .ConfigureAwait(false);
            if (!Runtime.TrySetServerId(context, serverId)) throw new OperationCanceledException(token);
            CS2_SimpleAdmin._logger?.LogInformation("Loaded server with ip {ip}", ipAddress);

            var cache = plugin.CacheManager;
            if (cache != null)
            {
                var config = plugin.Config;
                await WithRetries(context, "cache-init", async ct =>
                {
                    await cache.InitializeCacheAsync(config, serverId, ct).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }

            await LoadRenamesAsync(context).ConfigureAwait(false);
            // A failed admin load is not a ready plugin: retried a bounded number of times, then startup fails
            await ReloadAdminsWithRetriesAsync(plugin.ReloadAdminsAsync, context).ConfigureAwait(false);

            await context.PostAsync(plugin.MarkReady).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // unloaded meanwhile: nothing to apply
        }
        catch (Exception ex)
        {
            CS2_SimpleAdmin._logger?.LogCritical("Unable to load server data: " + ex.Message);
            if (!Runtime.TrySetState(context, PluginState.Failed, ex.Message)) return; // a newer lifetime owns the state now
            try
            {
                // Same fallback as before: global admins and renames still work without a server row
                await LoadRenamesAsync(context).ConfigureAwait(false);
                var fallback = await plugin.ReloadAdminsAsync(context).ConfigureAwait(false);
                if (fallback != AdminReloadResult.Success)
                    CS2_SimpleAdmin._logger?.LogError("Fallback admin load ended with {result}; existing permissions are kept", fallback);
            }
            catch (Exception inner)
            {
                RateLimitedLog.Error("init.fallback", inner, "Fallback admin load failed");
            }

            await ReleaseLoadingAsync(context).ConfigureAwait(false);
            return;
        }

        if (plugin.Config.EnableMetrics)
        {
            var queryString = $"?address={Uri.EscapeDataString(address)}&hostname={Uri.EscapeDataString(hostname)}";
            Runtime.Http?.TryEnqueue("metrics", async ct =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await CS2_SimpleAdmin.HttpClient.GetAsync($"https://api.daffyy.dev/index.php{queryString}", cts.Token)
                    .ConfigureAwait(false);
            });
        }
    }

    private static async Task LoadWithoutServerRowAsync()
    {
        var plugin = CS2_SimpleAdmin.Instance;
        var context = Runtime.Context;
        try
        {
            if (!await plugin.DatabaseInitTask.ConfigureAwait(false)) return;
            await LoadRenamesAsync(context).ConfigureAwait(false);
            await ReloadAdminsWithRetriesAsync(plugin.ReloadAdminsAsync, context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RateLimitedLog.Error("init.no-server-row", ex, "Unable to load admins without a server row");
        }
    }

    private static async Task<int> ResolveServerIdAsync(string address, string hostname, string rconPassword, CancellationToken ct)
    {
        var provider = CS2_SimpleAdmin.DatabaseProvider ?? throw new InvalidOperationException("no database provider");
        await using var connection = await provider.CreateConnectionAsync(ct).ConfigureAwait(false);
        var serverId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT id FROM sa_servers WHERE address = @address", new { address }, cancellationToken: ct)).ConfigureAwait(false);

        if (serverId == null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO sa_servers (address, hostname, rcon_password) VALUES (@address, @hostname, @rconPassword)",
                new { address, hostname, rconPassword }, cancellationToken: ct)).ConfigureAwait(false);

            serverId = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT id FROM sa_servers WHERE address = @address", new { address }, cancellationToken: ct)).ConfigureAwait(false);
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE sa_servers SET hostname = @hostname, rcon_password = @rconPassword WHERE address = @address",
                new { address, hostname, rconPassword }, cancellationToken: ct)).ConfigureAwait(false);
        }

        return serverId.Value;
    }

    private static async Task LoadRenamesAsync(RuntimeContext context)
    {
        var rows = await (context.TryQueueDb<List<(ulong SteamId, string Name)>>("renames-load",
                              CS2_SimpleAdmin.PlayerManager.LoadRenamedPlayersAsync)
                          ?? throw StartupRefused(context)).ConfigureAwait(false);
        await context.PostAsync(() =>
        {
            foreach (var (steamId, name) in rows)
                CS2_SimpleAdmin.RenamedPlayers[steamId] = name;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup step: reload admins until it succeeds, a bounded number of times. Canceled → cancellation; still failing
    /// after the retries → exception (startup then reports Failed instead of declaring the plugin ready).
    /// </summary>
    internal static async Task ReloadAdminsWithRetriesAsync(Func<RuntimeContext, Task<AdminReloadResult>> reload,
        RuntimeContext context, IReadOnlyList<TimeSpan>? delays = null)
    {
        delays ??= StepRetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            // The startup belongs to `context`: a stale one must not start (or keep waiting for) a reload, and the
            // reload is handed this context explicitly instead of resolving "the current runtime" by itself.
            if (!context.IsCurrent) throw new OperationCanceledException(context.Token);
            var result = await reload(context).ConfigureAwait(false);
            if (!context.IsCurrent) throw new OperationCanceledException(context.Token);
            switch (result)
            {
                case AdminReloadResult.Success:
                    return;
                case AdminReloadResult.Canceled:
                    throw new OperationCanceledException(context.Token);
            }

            if (attempt >= delays.Count)
                throw new InvalidOperationException("admins/groups could not be loaded (see log); permissions were not replaced");
            RateLimitedLog.Warning("init.admins", $"Admin reload failed (attempt {attempt + 1}), retrying");
            await Task.Delay(delays[attempt], context.Token).ConfigureAwait(false);
        }
    }

    private static Exception StartupRefused(RuntimeContext context) =>
        context.IsCurrent ? new InvalidOperationException("database queue full") : new OperationCanceledException(context.Token);

    /// <summary>Runs a startup step on the DB queue with a small bounded number of retries.</summary>
    private static async Task<T> WithRetries<T>(RuntimeContext context, string operation, Func<CancellationToken, Task<T>> step)
    {
        for (var attempt = 0; ; attempt++)
        {
            var job = context.TryQueueDb(operation, step) ?? throw StartupRefused(context);
            try
            {
                return await job.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < StepRetryDelays.Length)
            {
                RateLimitedLog.Warning($"init.{operation}", $"{operation} failed (attempt {attempt + 1}), retrying: {ex.Message}");
                await Task.Delay(StepRetryDelays[attempt], context.Token).ConfigureAwait(false);
            }
        }
    }

    private static Task ReleaseLoadingAsync(RuntimeContext context) =>
        context.PostAsync(() => CS2_SimpleAdmin.Instance.ReleaseServerLoading());
}
