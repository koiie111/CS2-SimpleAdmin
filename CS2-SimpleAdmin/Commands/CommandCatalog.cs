using System.Reflection;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin;

/// <summary>
/// The single list of this plugin's command keys, their callbacks and their default aliases. It needs no plugin instance and
/// no engine, so the registration rules (aliases, classification, guards) can be tested against the real keys.
/// </summary>
internal static class CommandCatalog
{
    /// <summary>Command key → name of the callback method of <see cref="CS2_SimpleAdmin"/>.</summary>
    internal static readonly (string Key, string MethodName)[] KeysAndMethods =
    [
        ("css_ban", nameof(CS2_SimpleAdmin.OnBanCommand)),
        ("css_addban", nameof(CS2_SimpleAdmin.OnAddBanCommand)),
        ("css_banip", nameof(CS2_SimpleAdmin.OnBanIpCommand)),
        ("css_unban", nameof(CS2_SimpleAdmin.OnUnbanCommand)),
        ("css_warn", nameof(CS2_SimpleAdmin.OnWarnCommand)),
        ("css_unwarn", nameof(CS2_SimpleAdmin.OnUnwarnCommand)),
        ("css_asay", nameof(CS2_SimpleAdmin.OnAdminToAdminSayCommand)),
        ("css_cssay", nameof(CS2_SimpleAdmin.OnAdminCustomSayCommand)),
        ("css_say", nameof(CS2_SimpleAdmin.OnAdminSayCommand)),
        ("css_psay", nameof(CS2_SimpleAdmin.OnAdminPrivateSayCommand)),
        ("css_csay", nameof(CS2_SimpleAdmin.OnAdminCenterSayCommand)),
        ("css_hsay", nameof(CS2_SimpleAdmin.OnAdminHudSayCommand)),
        ("css_penalties", nameof(CS2_SimpleAdmin.OnPenaltiesCommand)),
        ("css_admin", nameof(CS2_SimpleAdmin.OnAdminCommand)),
        ("css_adminhelp", nameof(CS2_SimpleAdmin.OnAdminHelpCommand)),
        ("css_addadmin", nameof(CS2_SimpleAdmin.OnAddAdminCommand)),
        ("css_deladmin", nameof(CS2_SimpleAdmin.OnDelAdminCommand)),
        ("css_addgroup", nameof(CS2_SimpleAdmin.OnAddGroup)),
        ("css_delgroup", nameof(CS2_SimpleAdmin.OnDelGroupCommand)),
        ("css_reloadadmins", nameof(CS2_SimpleAdmin.OnRelAdminCommand)),
        ("css_reloadbans", nameof(CS2_SimpleAdmin.OnRelBans)),
        ("css_hide", nameof(CS2_SimpleAdmin.OnHideCommand)),
        ("css_hidecomms", nameof(CS2_SimpleAdmin.OnHideCommsCommand)),
        ("css_who", nameof(CS2_SimpleAdmin.OnWhoCommand)),
        ("css_disconnected", nameof(CS2_SimpleAdmin.OnDisconnectedCommand)),
        ("css_warns", nameof(CS2_SimpleAdmin.OnWarnsCommand)),
        ("css_history", nameof(CS2_SimpleAdmin.OnHistoryCommand)),
        ("css_players", nameof(CS2_SimpleAdmin.OnPlayersCommand)),
        ("css_kick", nameof(CS2_SimpleAdmin.OnKickCommand)),
        ("css_map", nameof(CS2_SimpleAdmin.OnMapCommand)),
        ("css_wsmap", nameof(CS2_SimpleAdmin.OnWorkshopMapCommand)),
        ("css_cvar", nameof(CS2_SimpleAdmin.OnCvarCommand)),
        ("css_rcon", nameof(CS2_SimpleAdmin.OnRconCommand)),
        ("css_rr", nameof(CS2_SimpleAdmin.OnRestartCommand)),
        ("css_gag", nameof(CS2_SimpleAdmin.OnGagCommand)),
        ("css_addgag", nameof(CS2_SimpleAdmin.OnAddGagCommand)),
        ("css_ungag", nameof(CS2_SimpleAdmin.OnUngagCommand)),
        ("css_mute", nameof(CS2_SimpleAdmin.OnMuteCommand)),
        ("css_addmute", nameof(CS2_SimpleAdmin.OnAddMuteCommand)),
        ("css_unmute", nameof(CS2_SimpleAdmin.OnUnmuteCommand)),
        ("css_silence", nameof(CS2_SimpleAdmin.OnSilenceCommand)),
        ("css_addsilence", nameof(CS2_SimpleAdmin.OnAddSilenceCommand)),
        ("css_unsilence", nameof(CS2_SimpleAdmin.OnUnsilenceCommand)),
        ("css_vote", nameof(CS2_SimpleAdmin.OnVoteCommand)),
        ("css_slay", nameof(CS2_SimpleAdmin.OnSlayCommand)),
        ("css_slap", nameof(CS2_SimpleAdmin.OnSlapCommand)),
        ("css_team", nameof(CS2_SimpleAdmin.OnTeamCommand)),
        ("css_rename", nameof(CS2_SimpleAdmin.OnRenameCommand)),
        ("css_prename", nameof(CS2_SimpleAdmin.OnPrenameCommand)),
        ("css_tp", nameof(CS2_SimpleAdmin.OnGotoCommand)),
        ("css_bring", nameof(CS2_SimpleAdmin.OnBringCommand)),
        ("css_pluginsmanager", nameof(CS2_SimpleAdmin.OnPluginManagerCommand)),
        ("css_adminvoice", nameof(CS2_SimpleAdmin.OnAdminVoiceCommand)),
    ];

    internal static IEnumerable<(string Key, MethodInfo Method)> Entries =>
        KeysAndMethods.Select(e => (e.Key, Method: typeof(CS2_SimpleAdmin).GetMethod(e.MethodName,
            BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMethodException(nameof(CS2_SimpleAdmin), e.MethodName)));

    /// <summary>Default aliases per command key (what a freshly generated Commands.json contains).</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> DefaultAliases = new Dictionary<string, string[]>
    {
        ["css_ban"] = ["css_ban"],
        ["css_addban"] = ["css_addban"],
        ["css_banip"] = ["css_banip"],
        ["css_unban"] = ["css_unban"],
        ["css_warn"] = ["css_warn"],
        ["css_unwarn"] = ["css_unwarn"],
        ["css_asay"] = ["css_asay"],
        ["css_cssay"] = ["css_cssay"],
        ["css_say"] = ["css_say"],
        ["css_psay"] = ["css_psay"],
        ["css_csay"] = ["css_csay"],
        ["css_hsay"] = ["css_hsay"],
        ["css_penalties"] = ["css_penalties", "css_mypenalties", "css_comms"],
        ["css_admin"] = ["css_admin"],
        ["css_adminhelp"] = ["css_adminhelp"],
        ["css_addadmin"] = ["css_addadmin"],
        ["css_deladmin"] = ["css_deladmin"],
        ["css_addgroup"] = ["css_addgroup"],
        ["css_delgroup"] = ["css_delgroup"],
        ["css_reloadadmins"] = ["css_reloadadmins"],
        ["css_reloadbans"] = ["css_reloadbans"],
        ["css_hide"] = ["css_hide", "css_stealth"],
        ["css_hidecomms"] = ["css_hidecomms"],
        ["css_who"] = ["css_who"],
        ["css_disconnected"] = ["css_disconnected", "css_last"],
        ["css_warns"] = ["css_warns"],
        ["css_history"] = ["css_history", "css_penaltyhistory"],
        ["css_players"] = ["css_players"],
        ["css_kick"] = ["css_kick"],
        ["css_map"] = ["css_map", "css_changemap"],
        ["css_wsmap"] = ["css_wsmap", "css_changewsmap", "css_workshop"],
        ["css_cvar"] = ["css_cvar"],
        ["css_rcon"] = ["css_rcon"],
        ["css_rr"] = ["css_rr", "css_rg", "css_restart", "css_restartgame"],
        ["css_gag"] = ["css_gag"],
        ["css_addgag"] = ["css_addgag"],
        ["css_ungag"] = ["css_ungag"],
        ["css_mute"] = ["css_mute"],
        ["css_addmute"] = ["css_addmute"],
        ["css_unmute"] = ["css_unmute"],
        ["css_silence"] = ["css_silence"],
        ["css_addsilence"] = ["css_addsilence"],
        ["css_unsilence"] = ["css_unsilence"],
        ["css_vote"] = ["css_vote"],
        ["css_slay"] = ["css_slay"],
        ["css_slap"] = ["css_slap"],
        ["css_team"] = ["css_team"],
        ["css_rename"] = ["css_rename"],
        ["css_prename"] = ["css_prename"],
        ["css_resize"] = ["css_resize", "css_size"],
        ["css_tp"] = ["css_tp", "css_tpto", "css_goto"],
        ["css_bring"] = ["css_bring", "css_tphere"],
        ["css_pluginsmanager"] = ["css_pluginsmanager", "css_pluginmanager"],
        ["css_adminvoice"] = ["css_adminvoice", "css_listenall"],
    };


    /// <summary>
    /// Pure part of registration: for every configured key that has a callback, its non-empty aliases (keys without aliases or
    /// without a callback are dropped, a null entry in a hand-edited Commands.json does not abort the others).
    /// </summary>
    internal static IEnumerable<(string Key, string[] Aliases)> ResolveAliases(IReadOnlyDictionary<string, string[]?> configured)
    {
        foreach (var (key, raw) in configured)
        {
            var aliases = raw?.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
            if (aliases == null)
            {
                CS2_SimpleAdmin._logger?.LogWarning($"Commands.json: `{key}` has no aliases, skipped");
                continue;
            }

            if (aliases.Length == 0 || KeysAndMethods.All(m => m.Key != key)) continue;
            yield return (key, aliases);
        }
    }
}
