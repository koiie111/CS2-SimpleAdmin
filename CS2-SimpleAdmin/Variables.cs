using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CS2_SimpleAdmin.Models;
using CS2_SimpleAdminApi;
using MenuManager;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using CS2_SimpleAdmin.Database;
using CS2_SimpleAdmin.Managers;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CS2_SimpleAdmin;

public partial class CS2_SimpleAdmin
{
    // Config
    public CS2_SimpleAdminConfig Config { get; set; } = new();

    // HttpClient
    internal static readonly HttpClient HttpClient = new();

    // Paths
    internal static string ConfigDirectory =>
        Path.Combine(Application.RootDirectory, "configs/plugins/CS2-SimpleAdmin");

    // Localization
    public static IStringLocalizer? _localizer;

    // Voting System
    public static readonly Dictionary<string, int> VoteAnswers = [];
    public static bool VoteInProgress;

    // Command and Server Settings
    public static bool UnlockedCommands => CoreConfig.UnlockConCommands;
    internal static string IpAddress = string.Empty;
    // Written on the game thread (MarkReady), read from background work
    internal static volatile bool ServerLoaded;
    private static int _serverIdRaw = -1;
    /// <summary>sa_servers.id of this server, or null before it is resolved. Atomic for cross-thread reads.</summary>
    internal static int? ServerId
    {
        get
        {
            var value = Volatile.Read(ref _serverIdRaw);
            return value < 0 ? null : value;
        }
        set => Volatile.Write(ref _serverIdRaw, value ?? -1);
    }
    internal static readonly HashSet<ulong> AdminDisabledJoinComms = [];

    // Player Management
    internal static readonly HashSet<int> SilentPlayers = [];
    private static HashSet<int> _silentSnapshot = [];
    /// <summary>
    /// Copy of <see cref="SilentPlayers"/> handed out through the API (ListSilentAdminsSlots). Replaced, never mutated,
    /// whenever the silent set changes, so callers can poll it every frame without allocations and cannot corrupt
    /// the plugin's own set.
    /// </summary>
    internal static HashSet<int> SilentSnapshot => Volatile.Read(ref _silentSnapshot);
    internal static void PublishSilentSnapshot() => Volatile.Write(ref _silentSnapshot, [..SilentPlayers]);
    internal static readonly Dictionary<ulong, string> RenamedPlayers = [];
    internal static readonly ConcurrentDictionary<ulong, PlayerInfo> PlayersInfo = [];
    internal static readonly List<CCSPlayerController> CachedPlayers = [];
    internal static readonly List<CCSPlayerController> BotPlayers = [];
    private static readonly List<DisconnectedPlayer> DisconnectedPlayers = [];

    // Discord Integration
    internal static DiscordManager? DiscordWebhookClientLog;

    // Database Settings
    internal string DbConnectionString = string.Empty;
    // internal static Database.Database? Database;
    internal static IDatabaseProvider? DatabaseProvider;

    // Logger
    internal static ILogger? _logger;

    // Memory Function (Game-related)
    private static MemoryFunctionVoid<CBasePlayerController, CCSPlayerPawn, bool, bool>?
        _cBasePlayerControllerSetPawnFunc;

    // Menu API and Capabilities
    internal static IMenuApi? MenuApi;
    private static readonly PluginCapability<IMenuApi> MenuCapability = new("menu:nfcore");

    // Shared API
    internal static Api.CS2_SimpleAdminApi? SimpleAdminApi { get; private set; }

    // Managers
    internal PermissionManager PermissionManager = new(DatabaseProvider);
    internal BanManager BanManager = new(DatabaseProvider);
    internal MuteManager MuteManager = new(DatabaseProvider);
    internal WarnManager WarnManager = new(DatabaseProvider);
    internal CacheManager? CacheManager = new();
    internal static readonly PlayerManager PlayerManager = new();

    // Timers
    internal Timer? PlayersTimer = null;
    
    // Funny list
    private readonly List<string> _requiredPlugins = ["MenuManagerCore", "PlayerSettings"];
    private readonly List<string> _requiredShared = ["MenuManagerApi", "PlayerSettingsApi", "AnyBaseLib", "CS2-SimpleAdminApi"];
}