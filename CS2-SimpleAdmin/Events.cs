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
        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
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
        RemoveListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
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
    
    /// <summary>
    /// The earliest point at which the connection's SteamID64 is confirmed. The listener cannot veto the handshake (it
    /// returns void in CounterStrikeSharp 1.0.369): the ban state is read from the shared database right away and a
    /// banned connection is disconnected as soon as that answer arrives, i.e. a few milliseconds to a few seconds after
    /// authorization, not before the network handshake completes.
    /// </summary>
    private void OnClientAuthorized(int playerslot, SteamID steamId)
    {
        var player = Utilities.GetPlayerFromSlot(playerslot);
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;
        if (steamId.SteamId64 == 0 || player.SteamID != steamId.SteamId64) return; // player_connect_full starts the load then

        if (!CachedPlayers.Contains(player))
            CachedPlayers.Add(player);

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
    
    /// <summary>
    /// Second line of defence (UserMessageGagChatType): a chat user message (SayText2, 118) whose author is gagged or
    /// silenced is dropped, whatever produced it. The primary barrier is <see cref="CommandListenerCore"/>, which stops the
    /// line before the engine publishes it; this hook only matters for text that reaches the chat by another route, for
    /// example a chat-processor plugin re-sending the author's text. It needs nothing but the penalty state: a missing
    /// localizer or end date never lets text through. Limits: a plugin that prints the text without a SayText2 carrying the
    /// author (plain PrintToChat of a formatted string) is invisible to it, and a listener of another plugin that runs
    /// before ours on the same line has already acted when we see it.
    /// </summary>
    private HookResult HookUmChat(UserMessage um)
    {
        var author = Utilities.GetPlayerFromIndex(um.ReadInt("entityindex"));
        if (author == null || !author.IsValid || author.IsBot)
            return HookResult.Continue;

        // Gag/silence in force, or a connection whose penalties have not been read yet: its text is not published
        return ChatGuard.Evaluate(author.Slot, author.SteamID, author.UserId ?? -1, out _) == ChatRestriction.None
            ? HookResult.Continue
            : HookResult.Stop;
    }

    /// <summary>
    /// Game thread. Why this connection may not publish text right now. A connected human without a session of its own
    /// (plugin loaded mid-map, map change, missed connect event) is adopted here: the session starts its verification and its
    /// deadline, and until the load completes the player counts as unverified.
    /// </summary>
    private static ChatRestriction EvaluateRestriction(CCSPlayerController player, out DateTime? endDateTime)
    {
        var userId = player.UserId ?? -1;
        var restriction = ChatGuard.Evaluate(player.Slot, player.SteamID, userId, out endDateTime);
        if (restriction == ChatRestriction.Unverified && userId >= 0)
        {
            var session = Runtime.Sessions.Get(player.Slot);
            if (session == null || session.UserId != userId || session.SteamId != player.SteamID)
                PlayerManager.LoadPlayerData(player);
        }

        return restriction;
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

        // One lookup in the alias table built at registration (own commands by key) and one substring test for the rest;
        // everything else (jointeam, buy, ...) leaves here without touching the controller
        var own = OwnCommands.Classify(command);
        if (own is OwnCommandClass.Safe or OwnCommandClass.Other || own == OwnCommandClass.None && !ChatGate.IsSayLike(command))
            return HookResult.Continue;

        var text = info.GetArg(1);

        var checkStart = LatencyHistogram.Now();
        var restriction = EvaluateRestriction(player, out var endDateTime);
        PluginMetrics.ChatPenaltyCheck.RecordSince(checkStart);

        var decision = ChatGate.Decide(command, text, restriction, ChatTriggers.Current, Config.OtherSettings.GagAllowedChatCommands);
        switch (decision.Verdict)
        {
            case ChatVerdict.Block:
                return HookResult.Stop;
            case ChatVerdict.BlockAndNotify:
                // Notice only when there is something to tell; the line is dropped either way
                if (_localizer != null && endDateTime is not null)
                    player.SendLocalizedMessage(_localizer, "sa_player_penalty_chat_active", endDateTime.Value.ToString("g", player.GetLanguage()));
                return HookResult.Stop;
            case ChatVerdict.BlockUnverified:
                player.PrintToChat("[CS2-SimpleAdmin] Chat is unavailable until your penalties are verified.");
                return HookResult.Stop;
            case ChatVerdict.RunCommand:
                // The line is dropped before the engine's chat handler (which would publish it and dispatch the command); the
                // allow-listed command runs here once, as the player, with the usual permission and argument checks
                player.ExecuteClientCommandFromServer(decision.Command!);
                return HookResult.Stop;
        }

        if (text.Length == 0 || text[0] != '@') return HookResult.Continue;

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