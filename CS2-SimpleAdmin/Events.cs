using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;
using CS2_SimpleAdminApi;
using Microsoft.Extensions.Logging;
using System.Text;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.UserMessages;

namespace CS2_SimpleAdmin;

public partial class CS2_SimpleAdmin
{
    private bool _serverLoading;
    private bool _adminsReloadAfterCoreScheduled;

    /// <summary>At most one pending re-apply timer; the reload itself is coalesced as well.</summary>
    private void ScheduleAdminsReloadAfterCoreReload()
    {
        if (_adminsReloadAfterCoreScheduled)
        {
            Interlocked.Increment(ref PluginMetrics.ReloadAdminsCoalesced);
            return;
        }

        _adminsReloadAfterCoreScheduled = true;
        AddTimer(1.0f, () =>
        {
            _adminsReloadAfterCoreScheduled = false;
            ReloadAdmins(null);
        });
    }

    private void RegisterEvents()
    {
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        // RegisterListener<Listeners.OnClientConnect>(OnClientConnect);
        // RegisterListener<Listeners.OnClientConnect>(OnClientConnect);
        RegisterListener<Listeners.OnClientConnected>(OnClientConnected);
        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
        if (Config.OtherSettings.UserMessageGagChatType)
            HookUserMessage(118, HookUmChat);
        
        AddCommandListener(null, ComamndListenerHandler);
        // AddCommandListener("callvote", OnCommandCallVote);
        // AddCommandListener("say", OnCommandSay);
        // AddCommandListener("say_team", OnCommandTeamSay);
    }
    
    private void UnregisterEvents()
    {
        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        RemoveListener<Listeners.OnClientConnect>(OnClientConnect);
        RemoveListener<Listeners.OnClientConnected>(OnClientConnected);
        RemoveListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
        if (Config.OtherSettings.UserMessageGagChatType)
            UnhookUserMessage(118, HookUmChat);
        
        RemoveCommandListener(null!, ComamndListenerHandler, HookMode.Pre);
        // AddCommandListener("callvote", OnCommandCallVote);
        // AddCommandListener("say", OnCommandSay);
        // AddCommandListener("say_team", OnCommandTeamSay);
    }

    
    // private HookResult OnCommandCallVote(CCSPlayerController? caller, CommandInfo info)
    // {
    //     var voteType = info.GetArg(1).ToLower();
    //     
    //     if (voteType != "kick")
    //         return HookResult.Continue;
    //
    //     var target = int.TryParse(info.GetArg(2), out var userId) 
    //         ? Utilities.GetPlayerFromUserid(userId) 
    //         : null;
    //     
    //     if (target == null || !target.IsValid || target.Connected != PlayerConnectedState.PlayerConnected)
    //         return HookResult.Continue;
    //
    //     return !AdminManager.CanPlayerTarget(caller, target) ? HookResult.Stop : HookResult.Continue;
    // }

    private void OnGameServerSteamAPIActivated()
    {
        if (ServerLoaded || _serverLoading)
            return;

        // A failed startup (DB down, migration error) is retried here, i.e. on the next map start at the latest
        if (Runtime.State == PluginState.Failed && DatabaseProvider != null && _databaseInit.IsCompleted)
            StartDatabaseInitialization();

        _serverLoading = true;
        new ServerManager().LoadServerData();
    }

    /// <summary>Game thread: a failed server load releases the flag so the next map start tries again.</summary>
    internal void ReleaseServerLoading()
    {
        if (!ServerLoaded) _serverLoading = false;
    }

    [GameEventHandler]
    public HookResult OnClientDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Reason is 149 or 6)
            info.DontBroadcast = true;

        var player = @event.Userid;

#if DEBUG
        Logger.LogCritical("[OnClientDisconnect] Before");
#endif

        if (player == null || !player.IsValid || player.IsHLTV)
            return HookResult.Continue;
        
        CachedPlayers.Remove(player);
        BotPlayers.Remove(player);
        if (SilentPlayers.Remove(player.Slot))
            PublishSilentSnapshot();
        // Every background result for this connection becomes stale from here on
        Runtime.Sessions.End(player.Slot);

        if (player.IsBot)
        {
            return HookResult.Continue;
        }

#if DEBUG
        Logger.LogCritical("[OnClientDisconnect] After Check");
#endif
        try
        {
            if (DisconnectedPlayers.Count >= Config.OtherSettings.DisconnectedPlayersHistoryCount)
                DisconnectedPlayers.RemoveAt(0);

            var steamId = new SteamID(player.SteamID);
            var disconnectedPlayer = DisconnectedPlayers.FirstOrDefault(p => p.SteamId == steamId);

            if (disconnectedPlayer != null)
            {
                disconnectedPlayer.Name = player.PlayerName;
                disconnectedPlayer.IpAddress = player.IpAddress?.Split(":")[0];
                disconnectedPlayer.DisconnectTime = Time.ActualDateTime();
            }
            else
            {
                DisconnectedPlayers.Add(new DisconnectedPlayer(steamId, player.PlayerName,
                    player.IpAddress?.Split(":")[0], Time.ActualDateTime()));
            }
            
            PlayerPenaltyManager.RemoveAllPenalties(player.Slot);
            
            if (player.UserId.HasValue)
                PlayersInfo.TryRemove(player.SteamID, out _);

            if (!PermissionManager.AdminCache.TryGetValue(steamId, out var data)
                || !(data.ExpirationTime <= Time.ActualDateTime()))
            {
                return HookResult.Continue;
            }

            AdminManager.RemovePlayerPermissions(steamId, PermissionManager.AdminCache[steamId].Flags.ToArray());
            AdminManager.RemovePlayerFromGroup(steamId, true, PermissionManager.AdminCache[steamId].Flags.ToArray());
            var adminData = AdminManager.GetPlayerAdminData(steamId);

            if (adminData == null || data.Flags.ToList().Count != 0 && adminData.Groups.ToList().Count != 0)
                return HookResult.Continue;

            AdminManager.ClearPlayerPermissions(steamId);
            AdminManager.RemovePlayerAdminData(steamId);

            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            Logger.LogError($"An error occurred in OnClientDisconnect: {ex.Message}");
            return HookResult.Continue;
        }
    }

    private void OnClientConnect(int playerslot, string name, string ipAddress)
    {
#if DEBUG
        Logger.LogCritical("[OnClientConnect]");
#endif

        var player = Utilities.GetPlayerFromSlot(playerslot);
        if (player == null || !player.IsValid || player.IsBot)
            return;
        
        PlayerManager.LoadPlayerData(player);
    }
    
    private void OnClientConnected(int playerslot)
    {
#if DEBUG
        Logger.LogCritical("[OnClientConnected]");
#endif

        var start = LatencyHistogram.Now();
        var player = Utilities.GetPlayerFromSlot(playerslot);
        if (player == null || !player.IsValid || player.IsBot)
            return;

        if (!CachedPlayers.Contains(player))
            CachedPlayers.Add(player);

        PlayerManager.LoadPlayerData(player);
        PluginMetrics.ConnectHandler.RecordSince(start);
    }

//     private void OnClientConnect(int playerslot, string name, string ipaddress)
//     {
// #if DEBUG
//         Logger.LogCritical("[OnClientConnect]");
// #endif
//         if (Config.OtherSettings.BanType == 0)
//             return;
//         
//         if (Instance.CacheManager != null && !Instance.CacheManager.IsPlayerBanned(null, ipaddress.Split(":")[0]))
//                 return;
//         
//         var testPlayer = Utilities.GetPlayerFromSlot(playerslot);
//         if (testPlayer == null)
//             return;
//         Logger.LogInformation($"Gracz {testPlayer.PlayerName} ({testPlayer.SteamID.ToString()}) Czas: {DateTime.Now}");
//
//         Server.NextFrame((() =>
//         {
//             var player = Utilities.GetPlayerFromSlot(playerslot);
//             if (player == null || !player.IsValid || player.IsBot)
//                 return;
//
//             Helper.KickPlayer(player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED);
//         }));
//         
//         // Server.NextFrame(() =>
//         // {
//         //     var player = Utilities.GetPlayerFromSlot(playerslot);
//         //
//         //     if (player == null || !player.IsValid || player.IsBot)
//         //         return;
//         //
//         //     new PlayerManager().LoadPlayerData(player);
//         // });
//     }

    [GameEventHandler]
    public HookResult OnPlayerFullConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
#if DEBUG
        Logger.LogCritical("[OnPlayerFullConnect]");
#endif

        var player = @event.Userid;

        if (player == null || !player.IsValid)
            return HookResult.Continue;

        if (player is { IsBot: true, IsHLTV: false })
        {
            BotPlayers.Add(player);
            return HookResult.Continue;
        }

        var start = LatencyHistogram.Now();
        // Same connection as OnClientConnected → deduplicated by the session inside LoadPlayerData
        PlayerManager.LoadPlayerData(player, true);
        PluginMetrics.ConnectHandler.RecordSince(start);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
#if DEBUG
        Logger.LogCritical("[OnRoundStart]");
#endif

        foreach (var player in PlayersInfo.Values)
        {
            player.DiePosition = null;
        }

        // Walk the online players and look each up, instead of every stored rename × a fresh player list
        if (RenamedPlayers.Count > 0)
            AddTimer(0.5f, Managers.PlayerManager.EnforceRenamesOnline);

        return HookResult.Continue;
    }
    
    private HookResult HookUmChat(UserMessage um)
    {
        var author = Utilities.GetPlayerFromIndex(um.ReadInt("entityindex"));
        if (author == null || !author.IsValid || author.IsBot)
            return HookResult.Continue;

        // Fast path: no gag/silence entry for this slot → no clock read, no allocation
        if (!PlayerPenaltyManager.HasAnyPenalty(author.Slot, PenaltyType.Gag, PenaltyType.Silence))
            return HookResult.Continue;

        if (!PlayerPenaltyManager.IsPenalized(author.Slot, PenaltyType.Gag, out DateTime? endDateTime) &&
            !PlayerPenaltyManager.IsPenalized(author.Slot, PenaltyType.Silence, out endDateTime))
            return HookResult.Continue;
    
        if (_localizer == null || endDateTime == null)
            return HookResult.Continue;

        var message = um.ReadString("param2");
        if (!ChatTriggers.StartsWithTrigger(message)) return HookResult.Stop;
        
        for (var i = um.Recipients.Count - 1; i >= 0; i--)
        {
            if (um.Recipients[i] != author)
            {
                um.Recipients.RemoveAt(i);
            }
        }
        
        return HookResult.Continue;

        // author.SendLocalizedMessage(_localizer, "sa_player_penalty_chat_active", endDateTime.Value.ToString("g", author.GetLanguage()));
    }

    private HookResult ComamndListenerHandler(CCSPlayerController? player, CommandInfo info)
    {
        var start = LatencyHistogram.Now();
        try
        {
            return CommandListenerCore(player, info);
        }
        finally
        {
            PluginMetrics.CommandListener.RecordSince(start);
        }
    }

    private HookResult CommandListenerCore(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        // Ordinal-ignore-case comparisons instead of a lower-cased copy of every command
        var command = info.GetArg(0);

        if (CommandLogFilter.ShouldLog(Config.OtherSettings.AdditionalCommandsToLog, command))
            Helper.LogCommand(player, info);

        if (command.Equals("css_admins_reload", StringComparison.OrdinalIgnoreCase))
        {
            // CSS core's own css_admins_reload (@css/generic) wipes the admins loaded from SQL; re-apply them a moment
            // later. Checked and rate-limited here so an unauthorised or spammed command cannot pile up reloads.
            if (AdminManager.PlayerHasPermissions(new SteamID(player.SteamID), "@css/generic"))
                ScheduleAdminsReloadAfterCoreReload();
            return HookResult.Continue;
        }

        if (command.Equals("callvote", StringComparison.OrdinalIgnoreCase))
        {
            var voteType = info.GetArg(1);

            if (!voteType.Equals("kick", StringComparison.OrdinalIgnoreCase))
                return HookResult.Continue;

            var target = int.TryParse(info.GetArg(2), out var userId)
                ? Utilities.GetPlayerFromUserid(userId)
                : null;

            if (target == null || !target.IsValid || target.Connected != PlayerConnectedState.Connected)
                return HookResult.Continue;

            return !player.CanTarget(target) ? HookResult.Stop : HookResult.Continue;
        }

        if (command.IndexOf("say", StringComparison.OrdinalIgnoreCase) < 0)
            return HookResult.Continue;

        var text = info.GetArg(1);
        if (text.Length == 0)
            return HookResult.Stop;

        var startsWithTrigger = ChatTriggers.StartsWithTrigger(text);

        // Not gagged: a trigger message is a normal command / chat line, let it through untouched
        if (startsWithTrigger && !PlayerPenaltyManager.HasAnyPenalty(player.Slot, PenaltyType.Gag, PenaltyType.Silence))
            return HookResult.Continue;

        var checkStart = LatencyHistogram.Now();
        DateTime? endDateTime = null;
        var gagged = PlayerPenaltyManager.HasAnyPenalty(player.Slot, PenaltyType.Gag, PenaltyType.Silence) &&
                     (PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Gag, out endDateTime) ||
                      PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence, out endDateTime));
        PluginMetrics.ChatPenaltyCheck.RecordSince(checkStart);
        if (!gagged && startsWithTrigger)
            return HookResult.Continue;

        if (gagged)
        {
            // "!text" / "/text" used to skip this check and rely on HookUmChat to hide the line, which does not hold when
            // another plugin formats and broadcasts chat. A gagged player's trigger message is never passed on: a
            // command-shaped one is executed here silently, anything else is dropped.
            if (startsWithTrigger)
            {
                var silentCommand = ChatTriggers.ToSilentCommand(text);
                if (silentCommand != null)
                    player.ExecuteClientCommandFromServer(silentCommand);
                return HookResult.Stop;
            }

            if (_localizer != null && endDateTime is not null)
                player.SendLocalizedMessage(_localizer, "sa_player_penalty_chat_active", endDateTime.Value.ToString("g", player.GetLanguage()));
            return HookResult.Stop;
        }

        if (text[0] != '@') return HookResult.Continue;

        if (command.Equals("say", StringComparison.OrdinalIgnoreCase) &&
            AdminManager.PlayerHasPermissions(new SteamID(player.SteamID), "@css/chat"))
        {
            player.ExecuteClientCommandFromServer($"css_say {text.Remove(0, 1)}");
            return HookResult.Stop;
        }

        if (!command.Equals("say_team", StringComparison.OrdinalIgnoreCase)) return HookResult.Continue;

        StringBuilder sb = new();
        if (AdminManager.PlayerHasPermissions(new SteamID(player.SteamID), "@css/chat"))
        {
            sb.Append(_localizer!["sa_adminchat_template_admin", player.PlayerName, info.GetArg(1).Remove(0, 1)]);
            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && p is { IsBot: false, IsHLTV: false } && AdminManager.PlayerHasPermissions(new SteamID(p.SteamID), "@css/chat")))
            {
                p.PrintToChat(sb.ToString());
            }
        }
        else
        {
            sb.Append(_localizer!["sa_adminchat_template_player", player.PlayerName, info.GetArg(1).Remove(0, 1)]);
            player.PrintToChat(sb.ToString());
            foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false } && AdminManager.PlayerHasPermissions(new SteamID(p.SteamID), "@css/chat")))
            {
                p.PrintToChat(sb.ToString());
            }
        }

        return HookResult.Stop;
    }

    /*public HookResult OnCommandSay(CCSPlayerController? player, CommandInfo info)
	{
		if (player == null ||  !player.IsValid || player.IsBot)
			return HookResult.Continue;

		if (info.GetArg(1).StartsWith($"/")
			|| info.GetArg(1).StartsWith($"!"))
			return HookResult.Continue;

		if (info.GetArg(1).Length == 0)
			return HookResult.Handled;

		if (PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Gag) || PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence))
			return HookResult.Handled;

		return HookResult.Continue;
	}*/

    public HookResult OnCommandTeamSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        if (info.GetArg(1).StartsWith($"/")
            || info.GetArg(1).StartsWith($"!"))
            return HookResult.Continue;

        if (info.GetArg(1).Length == 0)
            return HookResult.Handled;

        if (PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Gag, out _) || PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence, out _))
            return HookResult.Stop;

        if (!info.GetArg(1).StartsWith($"@")) return HookResult.Continue;

        StringBuilder sb = new();

        if (AdminManager.PlayerHasPermissions(new SteamID(player.SteamID), "@css/chat"))
        {
            sb.Append(_localizer!["sa_adminchat_template_admin", player.PlayerName, info.GetArg(1).Remove(0, 1)]);
            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && p is { IsBot: false, IsHLTV: false } && AdminManager.PlayerHasPermissions(new SteamID(p.SteamID), "@css/chat")))
            {
                p.PrintToChat(sb.ToString());
            }
        }
        else
        {
            sb.Append(_localizer!["sa_adminchat_template_player", player.PlayerName, info.GetArg(1).Remove(0, 1)]);
            player.PrintToChat(sb.ToString());
            foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false } && AdminManager.PlayerHasPermissions(new SteamID(p.SteamID), "@css/chat")))
            {
                p.PrintToChat(sb.ToString());
            }
        }

        return HookResult.Handled;
    }

    private void OnMapStart(string mapName)
    {
        // Results computed for connections of the previous map must not be applied to this one
        Runtime.Sessions.Clear();

        if (Config.OtherSettings.ReloadAdminsEveryMapChange && ServerLoaded && ServerId != null)
            ReloadAdmins(null);

        AddTimer(1.0f, ServerManager.CheckHibernationStatus);
        
        if (!ServerLoaded || ServerId == null)
            AddTimer(1.5f, OnGameServerSteamAPIActivated);

        // AddTimer(34, () =>
        // {
        //     if (!ServerLoaded)
        //         OnGameServerSteamAPIActivated();
        // });

        SilentPlayers.Clear();
        PublishSilentSnapshot();

        PlayerPenaltyManager.RemoveAllPenalties();
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        
        if (player?.UserId == null || !player.IsValid || player.IsHLTV ||
            player.Connected != PlayerConnectedState.Connected || !PlayersInfo.ContainsKey(player.SteamID) ||
            @event.Attacker == null)
            return HookResult.Continue;

        var playerPosition = player.PlayerPawn.Value?.AbsOrigin; 
        var playerRotation = player.PlayerPawn.Value?.AbsRotation;
        PlayersInfo[player.SteamID].DiePosition = new DiePosition(
            new Vector3(
                playerPosition?.X ?? 0,
                playerPosition?.Y ?? 0,
                playerPosition?.Z ?? 0
            ),
            new Vector3(
                playerRotation?.X ?? 0,
                playerRotation?.Y ?? 0,
                playerRotation?.Z ?? 0
            )
        );

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot || !SilentPlayers.Contains(player.Slot))
            return HookResult.Continue;

        if (@event is not { Oldteam: <= 1, Team: >= 1 }) return HookResult.Continue;
        
        SilentPlayers.Remove(player.Slot);
        PublishSilentSnapshot();
        SimpleAdminApi?.OnAdminToggleSilentEvent(player.Slot, false);

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerInfo(EventPlayerInfo @event, GameEventInfo _)
    {
        EnforceRename(@event.Userid);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerChangename(EventPlayerChangename @event, GameEventInfo _)
    {
        EnforceRename(@event.Userid);
        return HookResult.Continue;
    }

    /// <summary>
    /// Re-applies a permanent rename. Checked after a short delay because the engine may not
    /// have written the client's new name yet when the event fires, and Rename itself
    /// takes 0.4s to settle (it briefly writes the name with a trailing space).
    /// </summary>
    private void EnforceRename(CCSPlayerController? player)
    {
        if (player is null || !player.IsValid || player.IsBot) return;
        if (!RenamedPlayers.TryGetValue(player.SteamID, out var name)) return;

        AddTimer(0.5f, () =>
        {
            if (player.IsValid && !player.PlayerName.Equals(name))
                player.Rename(name);
        });
    }
}