using System.Collections.Frozen;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdminApi;

namespace CS2_SimpleAdmin.Infrastructure;

internal enum ChatVerdict
{
    /// <summary>Nothing to say about this line: let it go on (the caller handles "@" admin chat as before).</summary>
    Pass,

    /// <summary>Drop the line silently.</summary>
    Block,

    /// <summary>Drop the line and tell the player that the chat penalty is active.</summary>
    BlockAndNotify,

    /// <summary>Drop the line and tell the player that the penalties of this connection are still being verified.</summary>
    BlockUnverified,

    /// <summary>Drop the line and run <see cref="ChatDecision.Command"/> on the player's behalf (one console command).</summary>
    RunCommand
}

internal readonly record struct ChatDecision(ChatVerdict Verdict, string? Command = null);

/// <summary>Why a player may not publish free text right now.</summary>
internal enum ChatRestriction
{
    None,

    /// <summary>GAG or SILENCE in force (a MUTE alone is not a chat restriction).</summary>
    Gagged,

    /// <summary>The connection's penalties have not been read yet (connect load pending, SteamID 0, hot reload...).</summary>
    Unverified
}

/// <summary>What a command of this plugin does with text, decided by its <b>key</b> (never by the alias it is registered under).</summary>
internal enum OwnCommandClass : byte
{
    /// <summary>Not a command of this plugin (other plugins, the engine).</summary>
    None = 0,

    /// <summary>Verified: answers or opens a menu for the caller only and publishes no free text of the caller to others.</summary>
    Safe,

    /// <summary>Everything else (administration with arguments, reasons, names...): not allowed through a chat trigger of a gagged player.</summary>
    Other,

    /// <summary>Delivers text typed by the caller to other players (say family, vote question).</summary>
    PublishesText
}

/// <summary>
/// The classification of this plugin's own commands by their original key (<c>css_say</c>, <c>css_vote</c>, ...) and the
/// alias table built from it when the commands are registered. A renamed alias (<c>css_psay</c> → <c>css_tell</c>) keeps the
/// class of its key. The table is an immutable snapshot replaced at registration; a chat line only does one dictionary lookup.
/// </summary>
internal static class OwnCommands
{
    /// <summary>
    /// Positive list of keys verified to reply or open a menu for the caller only, without publishing free text of the
    /// caller to other players. A key is added here only after reading its callback. Everything not listed is not allowed
    /// through the chat trigger of a gagged player, whatever its name or configuration says.
    /// </summary>
    internal static readonly FrozenSet<string> SafeKeys = new[]
    {
        "css_penalties",    // own penalties, message to the caller
        "css_admin",        // opens the admin menu for the caller
        "css_adminhelp",    // prints admin_help.txt to the caller
        "css_who",          // caller's console
        "css_disconnected", // menu for the caller
        "css_warns",        // menu for the caller
        "css_history",      // reply to the caller
        "css_players",      // caller's console
        "css_hide",         // toggles the caller's own state, message to the caller
        "css_hidecomms"     // toggles the caller's own state
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keys whose callback publishes text typed by the caller to other players. Each of them runs <see cref="ChatGuard"/>.</summary>
    internal static readonly FrozenSet<string> PublishingKeys = new[]
    {
        "css_say", "css_psay", "css_csay", "css_hsay", "css_cssay", "css_asay", "css_vote"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static OwnCommandClass ClassOfKey(string key) =>
        PublishingKeys.Contains(key) ? OwnCommandClass.PublishesText
        : SafeKeys.Contains(key) ? OwnCommandClass.Safe
        : OwnCommandClass.Other;

    private static FrozenDictionary<string, OwnCommandClass> _aliases =
        FrozenDictionary<string, OwnCommandClass>.Empty;

    /// <summary>Builds the alias table from (key, alias) pairs and publishes it. Called at registration, never per message.</summary>
    internal static void Replace(IEnumerable<(string Key, string Alias)> registered)
    {
        var map = new Dictionary<string, OwnCommandClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, alias) in registered)
        {
            if (string.IsNullOrWhiteSpace(alias)) continue;
            var cls = ClassOfKey(key);
            // An alias shared by two keys (a configuration mistake) gets the more restrictive class
            if (map.TryGetValue(alias, out var existing) && existing >= cls) continue;
            map[alias] = cls;
        }

        Volatile.Write(ref _aliases, map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    internal static void Reset() => Volatile.Write(ref _aliases, FrozenDictionary<string, OwnCommandClass>.Empty);

    /// <summary>Class of a command name as typed (arg 0), case-insensitive; <see cref="OwnCommandClass.None"/> if this plugin did not register it.</summary>
    internal static OwnCommandClass Classify(string commandName) =>
        Volatile.Read(ref _aliases).TryGetValue(commandName, out var cls) ? cls : OwnCommandClass.None;
}

/// <summary>
/// What a gagged / silenced / unverified player can still do with a chat or console line. Pure decision logic, used by the
/// global command listener and covered by tests.
/// <para>
/// The rule: <b>a restricted player never publishes free text.</b> There is no way to send it through "say", "say_team",
/// a trigger ("!text", "/text", "!hello world", "!!x", "! x"), an unknown command, a command of another plugin whose name
/// contains "say", or this plugin's own commands that deliver the caller's text (<see cref="OwnCommandClass.PublishesText"/>,
/// under any alias). The only thing a trigger line can still do is run a command that is verified safe (positive list
/// <see cref="OwnCommands.SafeKeys"/>, never extended by configuration) or a command of another plugin that the operator
/// listed in <c>GagAllowedChatCommands</c>; it runs exactly once, here, because the line itself is dropped before the engine's
/// chat handler (which would both publish it and dispatch the command) ever sees it.
/// </para>
/// </summary>
internal static class ChatGate
{
    internal static bool IsSayCommand(string command) =>
        command.Equals("say", StringComparison.OrdinalIgnoreCase) || command.Equals("say_team", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for "say", "say_team" and anything with "say" in its name (css_say, css_psay, ...). Heuristic for commands of other plugins.</summary>
    internal static bool IsSayLike(string command) => command.IndexOf("say", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <param name="command">The console command name as typed (arg 0).</param>
    /// <param name="text">Arg 1: the chat text.</param>
    /// <param name="restriction">Why the player may not publish text (None: nothing to enforce).</param>
    /// <param name="triggers">Configured public and silent chat triggers.</param>
    /// <param name="classify">Class of a command name as registered by this plugin (production: <see cref="OwnCommands.Classify"/>).</param>
    /// <param name="configuredForeign">Names of other plugins' commands the operator allowed for restricted players.</param>
    internal static ChatDecision Decide(string command, string text, ChatRestriction restriction, string[] triggers,
        Func<string, OwnCommandClass> classify, IEnumerable<string>? configuredForeign)
    {
        var own = classify(command);
        var say = IsSayCommand(command);

        if (own == OwnCommandClass.None && !say && !IsSayLike(command)) return new ChatDecision(ChatVerdict.Pass);
        if (own is OwnCommandClass.Safe or OwnCommandClass.Other) return new ChatDecision(ChatVerdict.Pass); // not text publishers, whatever they are called
        if (text.Length == 0 && own == OwnCommandClass.None) return new ChatDecision(ChatVerdict.Block);
        if (restriction == ChatRestriction.None) return new ChatDecision(ChatVerdict.Pass);

        var blocked = restriction == ChatRestriction.Unverified ? ChatVerdict.BlockUnverified : ChatVerdict.BlockAndNotify;
        if (!say) return new ChatDecision(blocked); // css_say & co. (any alias) or another plugin's *say* command typed in the console

        return ChatTriggers.StartsWithAny(text, triggers)
            ? DecideTrigger(text, triggers, classify, configuredForeign)
            : new ChatDecision(blocked);
    }

    /// <summary>Production overload: the registered alias table.</summary>
    internal static ChatDecision Decide(string command, string text, ChatRestriction restriction, string[] triggers,
        IEnumerable<string>? configuredForeign) =>
        Decide(command, text, restriction, triggers, OwnCommands.Classify, configuredForeign);

    // Separate method: the closure below is only allocated for lines that start with a trigger
    private static ChatDecision DecideTrigger(string text, string[] triggers, Func<string, OwnCommandClass> classify,
        IEnumerable<string>? configuredForeign)
    {
        var silent = ChatTriggers.ToSilentCommand(text, triggers, bare => IsTriggerAllowed(bare, classify, configuredForeign));
        return silent != null ? new ChatDecision(ChatVerdict.RunCommand, silent) : new ChatDecision(ChatVerdict.Block);
    }

    /// <summary>
    /// A trigger may run a command of this plugin only if it is verified safe. A command of another plugin runs only if the
    /// operator listed it, and a name that this plugin registered itself is never taken from that list (so a dangerous own
    /// callback cannot be allowed by adding its alias to the configuration).
    /// </summary>
    internal static bool IsTriggerAllowed(string bare, Func<string, OwnCommandClass> classify, IEnumerable<string>? configuredForeign)
    {
        if (bare.Length == 0) return false;
        var asPrefixed = classify("css_" + bare); // what the trigger really runs
        var asBare = classify(bare);              // same spelling registered without the prefix: must not be dangerous either
        if (asPrefixed != OwnCommandClass.None || asBare != OwnCommandClass.None)
            return asPrefixed == OwnCommandClass.Safe && asBare is OwnCommandClass.None or OwnCommandClass.Safe;
        return !IsSayLike(bare) && ConfiguredForeign(bare, configuredForeign);
    }

    private static bool ConfiguredForeign(string bare, IEnumerable<string>? configured)
    {
        if (configured == null) return false;
        foreach (var name in configured)
            if (ChatTriggers.StripPrefix(name).Equals(bare, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

/// <summary>
/// The restriction of one connection and the guard that the callbacks publishing caller text run. All checks are local
/// reads of the immutable penalty state and the session table: no SQL, no allocation, no player scan.
/// </summary>
internal static class ChatGuard
{
    /// <summary>
    /// Pure rule: gag/silence wins; otherwise the connection is unverified unless the current session of the slot is this
    /// very connection (SteamID + userid) and its penalties were applied. A session of a previous occupant of the slot
    /// never vouches for a new connection.
    /// </summary>
    internal static ChatRestriction Evaluate(int slot, ulong steamId, int userId, out DateTime? endsAt)
    {
        endsAt = null;
        if (PlayerPenaltyManager.HasAnyPenalty(slot, PenaltyType.Gag, PenaltyType.Silence) &&
            (PlayerPenaltyManager.IsPenalized(slot, PenaltyType.Gag, out endsAt) ||
             PlayerPenaltyManager.IsPenalized(slot, PenaltyType.Silence, out endsAt)))
            return ChatRestriction.Gagged;

        var session = Runtime.Sessions.Get(slot);
        return session != null && session.SteamId == steamId && session.UserId == userId &&
               session.LoadState == ConnectLoadState.Loaded
            ? ChatRestriction.None
            : ChatRestriction.Unverified;
    }

    /// <summary>Seam (tests): the restriction of a live caller. Production reads the controller.</summary>
    internal static Func<CCSPlayerController, ChatRestriction> CallerRestriction { get; set; } = static player =>
        Evaluate(player.Slot, player.SteamID, player.UserId ?? -1, out _);

    /// <summary>Seam (tests): tells the caller why the text was refused.</summary>
    internal static Action<CCSPlayerController, ChatRestriction> NotifyCaller { get; set; } = static (player, restriction) =>
    {
        var localizer = CS2_SimpleAdmin._localizer;
        if (restriction == ChatRestriction.Gagged && localizer != null &&
            Evaluate(player.Slot, player.SteamID, player.UserId ?? -1, out var ends) == ChatRestriction.Gagged && ends != null)
            player.SendLocalizedMessage(localizer, "sa_player_penalty_chat_active", ends.Value.ToString("g", player.GetLanguage()));
        else
            player.PrintToChat(restriction == ChatRestriction.Gagged
                ? "[CS2-SimpleAdmin] You are gagged: your text is not delivered."
                : "[CS2-SimpleAdmin] Chat is unavailable until your penalties are verified.");
    };

    /// <summary>
    /// First statement of every callback that delivers the caller's text to other players. The server console
    /// (<paramref name="caller"/> == null) is never restricted. Returns false (after telling the caller) when the text must
    /// not be published.
    /// </summary>
    internal static bool MayPublish(CCSPlayerController? caller)
    {
        if (caller == null) return true;
        var restriction = CallerRestriction(caller);
        if (restriction == ChatRestriction.None) return true;
        NotifyCaller(caller, restriction);
        return false;
    }
}
