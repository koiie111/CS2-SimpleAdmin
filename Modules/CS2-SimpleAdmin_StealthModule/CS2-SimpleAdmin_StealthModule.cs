using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CS2_SimpleAdminApi;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin_StealthModule;

public class CS2_SimpleAdmin_StealthModule : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "[CS2-SimpleAdmin] Stealth Module";
    public override string ModuleVersion => "v1.0.4";
    public override string ModuleAuthor => "daffyy";

    private const int MaxSlots = ObserverTargets.MaxSlots;
    private const uint InvalidHandle = uint.MaxValue;
    private static CS2_SimpleAdmin_StealthModule? _instance;

    private readonly PluginCapability<ICS2_SimpleAdminApi> _pluginCapability = new("simpleadmin:api");
    private ICS2_SimpleAdminApi? _sharedApi;
    private readonly TransmitSubscription _subscription;
    private readonly Listeners.CheckTransmit _checkTransmit;
    private readonly Listeners.OnEntityDeleted _entityDeleted;
    private readonly ObserverTargets _targets = new();
    private readonly HashSet<int> _silentSlots = [];
    private readonly HashSet<int> _disconnectedSilentSlots = [];
    private readonly HashSet<int> _statusExclusions = [];
    private readonly uint[] _controllerHandles = new uint[MaxSlots];
    private readonly uint[] _deletedPawns = new uint[MaxSlots];
    private readonly int[] _connectionGenerations = new int[MaxSlots];
    private readonly bool[] _recipients = new bool[MaxSlots];
    private readonly bool[] _dirty = new bool[MaxSlots];
    private readonly int[] _dirtySlots = new int[MaxSlots];
    private int _dirtyCount;
    private int _playerCount;
    private int _mapGeneration;
    private bool _mapEnding;
    private bool _loaded;
    private bool _reconcileQueued;
    private bool _refreshAll;
    private long _nextRefresh;
    private int _slotOffset = -1;
    private int _maxEdicts;
    private bool _useCssIndexer;
    private string[][] _permissions = [];

    public PluginConfig Config { get; set; } = new();

    public CS2_SimpleAdmin_StealthModule()
    {
        _checkTransmit = OnCheckTransmit;
        _entityDeleted = OnEntityDeleted;
        _subscription = new TransmitSubscription(SubscribeTransmit, UnsubscribeTransmit);
        ClearPlayers();
        OnConfigParsed(Config);
    }

    public override void Load(bool hotReload)
    {
        _loaded = true;
        _mapEnding = false;
        _instance = this;
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientDisconnectPost>(OnClientDisconnectPost);

        try
        {
            _slotOffset = GameData.GetOffset("CheckTransmitPlayerSlot");
            if (_slotOffset < 0)
                throw new InvalidOperationException("CheckTransmitPlayerSlot is negative.");

            // Read the runtime constant. The value inlined by the 1.0.340 compiler target differs on newer CSS.
            _maxEdicts = (int)(typeof(Utilities).GetField(nameof(Utilities.MaxEdicts))?.GetRawConstantValue()
                ?? throw new InvalidOperationException("CSS MaxEdicts is unavailable."));
            // The native adapter mirrors the x64 CSS layout. On other architectures let CSS decode its own list.
            _useCssIndexer = IntPtr.Size != 8;
        }
        catch (Exception ex)
        {
            _slotOffset = -1;
            Logger.LogWarning("Stealth transmit filtering disabled: {Error}", ex.Message);
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        if (!_loaded || _sharedApi is not null) return;

        try
        {
            _sharedApi = _pluginCapability.Get();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CS2-SimpleAdmin SharedApi not found");
            return;
        }

        if (_sharedApi is null)
        {
            Logger.LogError("CS2-SimpleAdmin SharedApi not found");
            return;
        }

        _sharedApi.OnAdminToggleSilent += OnAdminToggleSilent;
        try
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (player.IsValid && !player.IsBot)
                    Track(player);
            }
        }
        catch (NativeException)
        {
            // CSS can load plugins before the first map creates the game globals.
        }

        ReadSilentSlots();
        foreach (var slot in _silentSlots)
            SetStatusExclusion(slot, true);
        SyncTransmit();
    }

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;
        _permissions = config.Permissions.Select(permission => new[] { permission }).ToArray();
    }

    public override void Unload(bool hotReload)
    {
        if (!_loaded) return;
        _loaded = false;
        if (_sharedApi is not null)
            _sharedApi.OnAdminToggleSilent -= OnAdminToggleSilent;
        _sharedApi = null;
        ResetMap();
        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
        RemoveListener<Listeners.OnClientDisconnectPost>(OnClientDisconnectPost);
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private void OnMapStart(string mapName)
    {
        ResetMap();
        _mapEnding = false;
        // The core clears its silent snapshot in its own map-start listener.
        QueueSilentReconciliation();
    }

    private void OnMapEnd()
    {
        _mapEnding = true;
        ResetMap();
    }

    private void ResetMap()
    {
        _mapGeneration++;
        _reconcileQueued = false;
        _silentSlots.Clear();
        _disconnectedSilentSlots.Clear();
        foreach (var slot in _statusExclusions)
            Server.ExecuteCommand($"mm_removeexcludeslot {slot}");
        _statusExclusions.Clear();
        ClearPlayers();
        SyncTransmit();
    }

    private void ClearPlayers()
    {
        Array.Fill(_controllerHandles, InvalidHandle);
        Array.Fill(_deletedPawns, InvalidHandle);
        Array.Clear(_recipients);
        _playerCount = 0;
    }

    private void ReadSilentSlots()
    {
        _silentSlots.Clear();
        if (_sharedApi is not { } api) return;
        var snapshot = api.ListSilentAdminsSlots();
        _disconnectedSilentSlots.IntersectWith(snapshot);
        foreach (var slot in snapshot)
            if (!_disconnectedSilentSlots.Contains(slot))
                _silentSlots.Add(slot);
    }

    private void QueueSilentReconciliation()
    {
        if (!_loaded || _mapEnding || _sharedApi is null || _reconcileQueued) return;
        _reconcileQueued = true;
        var map = _mapGeneration;
        Server.NextWorldUpdate(() =>
        {
            if (!_loaded || map != _mapGeneration) return;
            _reconcileQueued = false;
            ReadSilentSlots();
            SyncTransmit();
        });
    }

    private void OnAdminToggleSilent(int slot, bool status)
    {
        if (!_loaded || _mapEnding) return;
        _disconnectedSilentSlots.Remove(slot);
        ReadSilentSlots();
        SetStatusExclusion(slot, status);
        SyncTransmit();
    }

    private void SetStatusExclusion(int slot, bool status)
    {
        if (!Config.BlockStatusCommand || (uint)slot >= MaxSlots) return;
        if (status ? _statusExclusions.Add(slot) : _statusExclusions.Remove(slot))
            Server.ExecuteCommand(status ? $"mm_excludeslot {slot}" : $"mm_removeexcludeslot {slot}");
    }

    private void SyncTransmit() =>
        _subscription.Sync(_loaded && !_mapEnding && _sharedApi is not null && _slotOffset >= 0,
            _playerCount, _silentSlots.Count);

    private void SubscribeTransmit()
    {
        _refreshAll = true;
        RegisterListener<Listeners.OnEntityDeleted>(_entityDeleted);
        try
        {
            RegisterListener<Listeners.CheckTransmit>(_checkTransmit);
        }
        catch
        {
            RemoveListener<Listeners.OnEntityDeleted>(_entityDeleted);
            throw;
        }
    }

    private void UnsubscribeTransmit()
    {
        RemoveListener<Listeners.CheckTransmit>(_checkTransmit);
        RemoveListener<Listeners.OnEntityDeleted>(_entityDeleted);
        _targets.Reset();
        Array.Clear(_dirty);
        _dirtyCount = 0;
        _refreshAll = false;
        _nextRefresh = 0;
    }

    private void Track(CCSPlayerController player)
    {
        var slot = player.Slot;
        if ((uint)slot >= MaxSlots) return;
        var handle = player.EntityHandle.Raw;
        if (_controllerHandles[slot] != handle)
        {
            if (_controllerHandles[slot] == InvalidHandle) _playerCount++;
            _controllerHandles[slot] = handle;
            _connectionGenerations[slot]++;
            _deletedPawns[slot] = InvalidHandle;
            _targets.Remove(slot);
        }

        _recipients[slot] = !player.IsHLTV;
        MarkDirty(slot);
    }

    private bool Untrack(int slot, uint handle)
    {
        if ((uint)slot >= MaxSlots || _controllerHandles[slot] != handle) return false;
        _controllerHandles[slot] = InvalidHandle;
        _recipients[slot] = false;
        _deletedPawns[slot] = InvalidHandle;
        _connectionGenerations[slot]++;
        _playerCount--;
        _targets.Remove(slot);
        return true;
    }

    internal static IEnumerable<CCSPlayerController> GetTrackedPlayers()
    {
        if (_instance is not { } instance) yield break;
        for (var slot = 0; slot < MaxSlots; slot++)
            if (instance.ResolvePlayer(slot) is { } player)
                yield return player;
    }

    private CCSPlayerController? ResolvePlayer(int slot)
    {
        var handle = _controllerHandles[slot];
        if (handle == InvalidHandle) return null;
        var player = Utilities.GetPlayerFromSlot(slot);
        return player is { IsValid: true } && player.EntityHandle.Raw == handle ? player : null;
    }

    private void MarkDirty(int slot)
    {
        if (!_subscription.Active || _refreshAll || (uint)slot >= MaxSlots || _dirty[slot] ||
            _controllerHandles[slot] == InvalidHandle)
            return;
        _dirty[slot] = true;
        _dirtySlots[_dirtyCount++] = slot;
    }

    private void RefreshSlot(int slot)
    {
        var observerHandle = ResolvePlayer(slot)?.ObserverPawn;
        if (observerHandle is not null && observerHandle.Raw != _deletedPawns[slot] &&
            observerHandle.Value is { IsValid: true } observer && observer.EntityHandle.Raw == observerHandle.Raw)
        {
            var index = observer.Index;
            if (index > 0 && index < _maxEdicts)
            {
                _targets.Set(slot, (int)index, observerHandle.Raw);
                return;
            }
        }

        _targets.Remove(slot);
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        if (_targets.Count == 0) return;
        var handle = entity.EntityHandle;
        if (!_targets.ContainsIndex((int)handle.Index)) return;

        var raw = handle.Raw;
        for (var slot = 0; slot < MaxSlots; slot++)
        {
            if (!_targets.RemoveDeleted(slot, raw)) continue;
            _deletedPawns[slot] = raw;
            MarkDirty(slot);
        }
    }

    private void OnCheckTransmit(CCheckTransmitInfoList infolist)
    {
        var now = Stopwatch.GetTimestamp();
        if (_refreshAll || now >= _nextRefresh)
        {
            for (var slot = 0; slot < MaxSlots; slot++)
                RefreshSlot(slot);
            Array.Clear(_dirty);
            _refreshAll = false;
            _nextRefresh = now + Stopwatch.Frequency;
        }
        else
        {
            for (var i = 0; i < _dirtyCount; i++)
            {
                var slot = _dirtySlots[i];
                _dirty[slot] = false;
                RefreshSlot(slot);
            }
        }

        _dirtyCount = 0;
        if (_targets.Count == 0) return;
        if (_useCssIndexer)
            ApplyUsingCssIndexer(infolist);
        else
            ApplyNative(infolist);
    }

    internal static bool IsNativeRecipient(bool[] recipients, int slot) =>
        (uint)slot < (uint)recipients.Length && recipients[slot];

    internal static bool IsIndexerRecipient(bool hasPlayer, bool isHltv) => hasPlayer && !isHltv;

    private void ApplyUsingCssIndexer(CCheckTransmitInfoList infolist)
    {
        if (infolist.Handle == 0) return;
        var count = infolist.Count;
        if ((uint)count > MaxSlots) return;
        for (var i = 0; i < count; i++)
        {
            var (info, player) = infolist[i];
            if (IsIndexerRecipient(player is not null, player?.IsHLTV == true))
                _targets.Hide(info.TransmitEntities, player!.Slot);
        }
    }

    private unsafe void ApplyNative(CCheckTransmitInfoList infolist)
    {
        // CSS x64 list layout: [CCheckTransmitInfo**, count]. Borrowed pointers are valid only in this callback.
        var inner = (nint*)infolist.Handle;
        if (inner == null) return;
        var count = (int)inner[1];
        var infos = (nint*)inner[0];
        if (infos == null || (uint)count > MaxSlots) return;
        for (var i = 0; i < count; i++)
        {
            var info = (byte*)infos[i];
            if (info == null) continue;
            var slot = *(int*)(info + _slotOffset);
            if (IsNativeRecipient(_recipients, slot))
                _targets.Hide(((CCheckTransmitInfo*)info)->TransmitEntities, slot);
        }
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult EventPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true } || player.IsBot) return HookResult.Continue;
        if (_subscription.Active)
            MarkDirty(player.Slot);
        if (ShouldSuppressBroadcast(player))
            info.DontBroadcast = true;
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult EventPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (_subscription.Active && @event.Userid is { IsValid: true } player)
            MarkDirty(player.Slot);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult EventRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_subscription.Active)
            _refreshAll = true;
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult EventPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true } || player.IsBot || _mapEnding) return HookResult.Continue;

        var slot = player.Slot;
        if ((uint)slot >= MaxSlots) return HookResult.Continue;
        Track(player);
        SyncTransmit();

        if (!Config.HideAdminsOnJoin || !HasConfiguredPermission(player))
            return HookResult.Continue;

        var map = _mapGeneration;
        var connection = _connectionGenerations[slot];
        AddTimer(0.75f, () => ResolveJoinPlayer(slot, map, connection)?.ChangeTeam(CsTeam.Spectator),
            TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(1.25f, () => ResolveJoinPlayer(slot, map, connection)?.ExecuteClientCommandFromServer("css_hide"),
            TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private CCSPlayerController? ResolveJoinPlayer(int slot, int map, int connection)
    {
        if (!_loaded || _mapGeneration != map || _connectionGenerations[slot] != connection ||
            _sharedApi is not { } api || ResolvePlayer(slot) is not { } player || api.IsAdminSilent(player))
            return null;
        return player;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult EventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true } || player.IsBot) return HookResult.Continue;

        var slot = player.Slot;
        var silent = _sharedApi?.IsAdminSilent(player) == true;
        if (silent || HasConfiguredPermission(player))
            info.DontBroadcast = true;

        var tracked = Untrack(slot, player.EntityHandle.Raw);
        if (tracked || silent)
            FinishDisconnect(slot);

        return HookResult.Continue;
    }

    private void OnClientDisconnectPost(int slot)
    {
        // Some disconnect events have no usable controller. The native callback still carries the slot.
        if ((uint)slot < MaxSlots && _controllerHandles[slot] != InvalidHandle &&
            Untrack(slot, _controllerHandles[slot]))
            FinishDisconnect(slot);
    }

    private void FinishDisconnect(int slot)
    {
        SetStatusExclusion(slot, false);
        if (_silentSlots.Remove(slot))
        {
            // The core handles player_disconnect in Post and can miss it if the controller is already gone.
            _disconnectedSilentSlots.Add(slot);
            QueueSilentReconciliation();
        }
        SyncTransmit();
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult EventPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true } || player.IsBot) return HookResult.Continue;
        if (_subscription.Active)
            MarkDirty(player.Slot);
        if (ShouldSuppressBroadcast(player, checkTeam: true))
            info.DontBroadcast = true;
        return HookResult.Continue;
    }

    private bool ShouldSuppressBroadcast(CCSPlayerController player, bool checkTeam = false) =>
        _sharedApi?.IsAdminSilent(player) == true ||
        ((!checkTeam || player.TeamNum <= 1) && HasConfiguredPermission(player));

    private bool HasConfiguredPermission(CCSPlayerController player)
    {
        if (_permissions.Length == 0) return false;
        var steamId = new SteamID(player.SteamID);
        foreach (var flags in _permissions)
            if (AdminManager.PlayerHasPermissions(steamId, flags))
                return true;
        return false;
    }
}
