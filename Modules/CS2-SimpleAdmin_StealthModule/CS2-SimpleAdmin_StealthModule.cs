using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CS2_SimpleAdminApi;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin_StealthModule;

public class CS2_SimpleAdmin_StealthModule: BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "[CS2-SimpleAdmin] Stealth Module";
    public override string ModuleVersion => "v1.0.3";
    public override string ModuleAuthor => "daffyy";

    private static ICS2_SimpleAdminApi? _sharedApi;
    private readonly PluginCapability<ICS2_SimpleAdminApi> _pluginCapability  = new("simpleadmin:api");
    private bool _subscribedToggleSilent;

    internal static readonly HashSet<CCSPlayerController> Players = [];

    // Compact view of Players for CheckTransmit, rebuilt on the game thread only when marked dirty
    // (connect/disconnect/team/spawn/round, plus a 1 s safety refresh). CheckTransmit itself only reads arrays.
    private const int MaxSlots = 128;
    private int[] _observerIndices = [];
    private int[] _observerOwnerSlots = [];
    private readonly bool[] _isTrackedRecipient = new bool[MaxSlots];
    private bool _indicesDirty = true;
    private int _slotOffset = -1;

    public PluginConfig Config { get; set; } = new();

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);

        try
        {
            // Same gamedata offset CounterStrikeSharp's CCheckTransmitInfoList uses to find the recipient's slot
            _slotOffset = GameData.GetOffset("CheckTransmitPlayerSlot");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CheckTransmitPlayerSlot offset unavailable, using the slower indexer: {Error}", ex.Message);
        }

        // Safety net for pawn changes no event announced; cheap (only flips a flag)
        AddTimer(1.0f, () => _indicesDirty = true, TimerFlags.REPEAT);

        if (hotReload)
        {
            Players.Clear();
            foreach (var player in Utilities.GetPlayers())
                if (player.IsValid && !player.IsBot)
                    Players.Add(player);
            _indicesDirty = true;
        }
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.CheckTransmit>(OnCheckTransmit);
        if (_sharedApi != null && _subscribedToggleSilent)
            _sharedApi.OnAdminToggleSilent -= OnAdminToggleSilent;
        _subscribedToggleSilent = false;
        _sharedApi = null;
        Players.Clear();
    }

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            _sharedApi = _pluginCapability.Get();
            if (_sharedApi == null) throw new NullReferenceException("_sharedApi is null");

            if (Config.BlockStatusCommand)
            {
                _sharedApi.OnAdminToggleSilent += OnAdminToggleSilent;
                _subscribedToggleSilent = true;
            }
        }
        catch (Exception)
        {
            Logger.LogError("CS2-SimpleAdmin SharedApi not found");
            Unload(false);
        }
    }

    private void OnAdminToggleSilent(int slot, bool status)
    {
        Server.ExecuteCommand(status ? $"mm_excludeslot {slot}" : $"mm_removeexcludeslot {slot}");
    }

    /// <summary>
    /// Game thread. Rebuilds the observer-pawn index arrays from the tracked players (native reads happen here,
    /// at most once per dirty period, never per CheckTransmit recipient).
    /// </summary>
    private void RebuildIndices()
    {
        _indicesDirty = false;
        var indices = new List<int>(Players.Count);
        var owners = new List<int>(Players.Count);
        Array.Clear(_isTrackedRecipient);
        foreach (var p in Players)
        {
            if (!p.IsValid) continue; // before touching the pawn handle
            if ((uint)p.Slot < MaxSlots && !p.IsHLTV) _isTrackedRecipient[p.Slot] = true;
            var observer = p.ObserverPawn.Value;
            if (observer is not { IsValid: true }) continue;
            indices.Add((int)observer.Index);
            owners.Add(p.Slot);
        }

        _observerIndices = indices.ToArray();
        _observerOwnerSlots = owners.ToArray();
    }

    /// <summary>
    /// Hides the observer pawns of tracked players from every other tracked recipient while at least one admin is
    /// silent (same semantics as before). Steady state: no LINQ, no anonymous objects, no allocations and no
    /// native entity lookups – only bit operations on the transmit sets.
    /// </summary>
    private void OnCheckTransmit(CCheckTransmitInfoList infolist)
    {
        var api = _sharedApi;
        if (api == null || Players.Count <= 2) return;
        if (api.ListSilentAdminsSlots().Count == 0) return; // snapshot set from the core plugin: no copy per call

        if (_indicesDirty) RebuildIndices();
        var indices = _observerIndices;
        if (indices.Length == 0) return;
        var owners = _observerOwnerSlots;

        if (_slotOffset >= 0)
        {
            FastPath(infolist, indices, owners);
            return;
        }

        // Fallback: CSS indexer (allocates a controller wrapper per recipient)
        for (var i = 0; i < infolist.Count; i++)
        {
            var (info, player) = infolist[i];
            if (player == null || player.IsHLTV) continue;
            Hide(info.TransmitEntities, player.Slot, indices, owners);
        }
    }

    private unsafe void FastPath(CCheckTransmitInfoList infolist, int[] indices, int[] owners)
    {
        // Layout as read by CounterStrikeSharp 1.0.3xx CCheckTransmitInfoList: [0] = CCheckTransmitInfo**, [1] = count
        var inner = (nint*)infolist.Handle;
        var count = (int)*(inner + 1);
        var infos = *(nint**)inner;
        for (var i = 0; i < count; i++)
        {
            var infoPtr = *(infos + i);
            var slot = *(int*)((byte*)infoPtr + _slotOffset);
            if ((uint)slot >= MaxSlots || !_isTrackedRecipient[slot]) continue; // HLTV / untracked (was: null or IsHLTV)
            var info = *(CCheckTransmitInfo*)infoPtr;
            Hide(info.TransmitEntities, slot, indices, owners);
        }
    }

    private static void Hide(CFixedBitVecBase entities, int recipientSlot, int[] indices, int[] owners)
    {
        for (var j = 0; j < indices.Length; j++)
        {
            if (owners[j] == recipientSlot) continue;
            entities.Remove(indices[j]); // clearing an unset bit is a no-op, no Contains needed
        }
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult EventPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        _indicesDirty = true;
        if (ShouldSuppressBroadcast(@event.Userid))
            info.DontBroadcast = true;

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult EventPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        _indicesDirty = true;
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult EventRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _indicesDirty = true;
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult EventPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player?.IsValid != true || player.IsBot) return HookResult.Continue;

        Players.Add(player);
        _indicesDirty = true;

        var steamId = new SteamID(player.SteamID);
        if (!Config.Permissions.Any(permission => AdminManager.PlayerHasPermissions(steamId, permission)))
            return HookResult.Continue;

        if (!Config.HideAdminsOnJoin) return HookResult.Continue;

        AddTimer(0.75f, () =>
        {
            if (player.IsValid) player.ChangeTeam(CsTeam.Spectator);
        });

        AddTimer(1.25f, () =>
        {
            if (player.IsValid) player.ExecuteClientCommandFromServer("css_hide");
        });

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult EventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player?.IsValid != true || player.IsBot) return HookResult.Continue;

        if (Config.BlockStatusCommand && _sharedApi != null && _sharedApi.IsAdminSilent(player))
            Server.ExecuteCommand($"mm_removeexcludeslot {player.Slot}");

        Players.Remove(player);
        _indicesDirty = true;

        if (ShouldSuppressBroadcast(@event.Userid))
                info.DontBroadcast = true;

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult EventPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (ShouldSuppressBroadcast(@event.Userid, true))
            info.DontBroadcast = true;

        return HookResult.Continue;
    }

    private bool ShouldSuppressBroadcast(CCSPlayerController? player, bool checkTeam = false)
    {
        if (player?.IsValid != true || player.IsBot)
            return false;

        if (_sharedApi is not null && _sharedApi.IsAdminSilent(player))
        {
            return true;
        }

        if (checkTeam && player.TeamNum > 1)
            return false;

        var steamId = new SteamID(player.SteamID);
        return Config.Permissions.Any(permission => AdminManager.PlayerHasPermissions(steamId, permission));
    }
}
