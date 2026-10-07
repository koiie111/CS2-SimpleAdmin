using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CS2_SimpleAdminApi;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin_RedisInform;

public class PluginConfig : IBasePluginConfig
{
    [JsonPropertyName("ConfigVersion")] public int Version { get; set; } = 2;
    [JsonPropertyName("RedisConnectionString")] public string RedisConnectionString { get; set; } = "172.18.0.1";
    [JsonPropertyName("RedisPassword")] public string RedisPassword { get; set; } = "";

    /// <summary>
    /// Channel this server publishes to and the one it listens on. v1 published to "cs2-simpleadmin_events" and
    /// listened on "cs2-simpleadmin_events1", so servers never heard each other without an external relay. Both now
    /// default to the same channel; set them to the old values to keep using such a relay.
    /// </summary>
    [JsonPropertyName("PublishChannel")] public string PublishChannel { get; set; } = "cs2-simpleadmin_events";
    [JsonPropertyName("SubscribeChannel")] public string SubscribeChannel { get; set; } = "cs2-simpleadmin_events";
}

public class CS2_SimpleAdmin_RedisInform: BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "[CS2-SimpleAdmin] Redis Inform";
    public override string ModuleVersion => "v1.1.0";
    public override string ModuleAuthor => "daffyy";

    // Set by the constructor (a static "= new()" would build an extra BasePlugin that registers listeners)
    internal static CS2_SimpleAdmin_RedisInform Instance = null!;
    public PluginConfig Config { get; set; } = new();
    internal static ICS2_SimpleAdminApi? SharedApi;
    private readonly PluginCapability<ICS2_SimpleAdminApi> _pluginCapability  = new("simpleadmin:api");

    private RedisSubscriber? _redisSubscriber;

    public CS2_SimpleAdmin_RedisInform()
    {
        Instance = this;
    }

    public override void Load(bool hotReload)
    {
        Instance = this;
    }

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;
    }

    public override void Unload(bool hotReload)
    {
        if (SharedApi != null)
            SharedApi.OnAdminShowActivity -= OnAdminShowActivity;
        SharedApi = null;
        StopSubscriber();
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        SharedApi = _pluginCapability.Get();

        if (SharedApi == null)
        {
            Logger.LogError("CS2-SimpleAdmin SharedApi not found");
            Unload(false);
            return;
        }

        SharedApi.OnAdminShowActivity -= OnAdminShowActivity; // never twice
        SharedApi.OnAdminShowActivity += OnAdminShowActivity;

        StopSubscriber();
        _redisSubscriber = new RedisSubscriber(Guid.NewGuid().ToString("N"), Config, Logger);
        _redisSubscriber.Start();
    }

    private void StopSubscriber()
    {
        var subscriber = _redisSubscriber;
        _redisSubscriber = null;
        // Disposal is async network work: never waited for on the game thread
        subscriber?.Stop();
    }

    private void OnAdminShowActivity(string messageKey, string? callerName, bool dontPublish, object messageArgs)
    {
        // Messages received from Redis are re-shown with dontPublish = true; publishing them again would bounce
        // them between servers forever once both sides listen on the same channel.
        if (dontPublish) return;
        _redisSubscriber?.Publish(messageKey, callerName, messageArgs);
    }
}
