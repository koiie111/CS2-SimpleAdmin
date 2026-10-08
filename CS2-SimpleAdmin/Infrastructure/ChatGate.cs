namespace CS2_SimpleAdmin.Infrastructure;

internal enum ChatVerdict
{
    /// <summary>Nothing to say about this line: let it go on (the caller handles "@" admin chat as before).</summary>
    Pass,

    /// <summary>Drop the line silently.</summary>
    Block,

    /// <summary>Drop the line and tell the player that the chat penalty is active.</summary>
    BlockAndNotify,

    /// <summary>Drop the line and run <see cref="ChatDecision.Command"/> on the player's behalf (one console command).</summary>
    RunCommand
}

internal readonly record struct ChatDecision(ChatVerdict Verdict, string? Command = null);

/// <summary>
/// What happens to a line a player sent through the <c>say</c>/<c>say_team</c> family when the player is gagged or
/// silenced. Pure decision logic, used by the global command listener and covered by tests.
/// <para>
/// The rule: <b>a gagged player never publishes free text.</b> There is no way to send it through "say", "say_team",
/// a trigger ("!text", "/text", "!hello world", "!!x", "! x"), an unknown command or a plugin's own broadcast commands
/// (css_say, css_psay, css_csay, css_hsay, css_cssay, css_asay, anything else whose name contains "say"). The only thing a
/// trigger line can still do is run a command from the allow-list (<see cref="GagChatCommands"/>), which replies to the
/// caller only, and it runs exactly once, here, because the line itself is dropped before the engine's chat handler (which
/// would both publish it and dispatch the command) ever sees it.
/// </para>
/// </summary>
internal static class ChatGate
{
    /// <summary>The two real chat commands. Every other command that merely contains "say" is treated as a broadcast command.</summary>
    internal static bool IsSayCommand(string command) =>
        command.Equals("say", StringComparison.OrdinalIgnoreCase) || command.Equals("say_team", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for "say", "say_team" and anything with "say" in its name (css_say, css_psay, ...).</summary>
    internal static bool IsSayLike(string command) => command.IndexOf("say", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <param name="command">The console command name as typed (arg 0).</param>
    /// <param name="text">Arg 1: the chat text.</param>
    /// <param name="gagged">Gag or silence in force for the player (a MUTE alone is not).</param>
    /// <param name="triggers">Configured public and silent chat triggers.</param>
    /// <param name="allowed">Command names (no css_ prefix) a gagged player may still run through a trigger.</param>
    internal static ChatDecision Decide(string command, string text, bool gagged, string[] triggers, Func<string, bool> allowed)
    {
        if (!IsSayLike(command)) return new ChatDecision(ChatVerdict.Pass);
        if (text.Length == 0) return new ChatDecision(ChatVerdict.Block);
        if (!gagged) return new ChatDecision(ChatVerdict.Pass);

        if (!IsSayCommand(command))
            return new ChatDecision(ChatVerdict.BlockAndNotify); // css_say & co. typed in the console by a gagged player

        if (ChatTriggers.StartsWithAny(text, triggers))
        {
            var silent = ChatTriggers.ToSilentCommand(text, triggers, allowed);
            return silent != null ? new ChatDecision(ChatVerdict.RunCommand, silent) : new ChatDecision(ChatVerdict.Block);
        }

        return new ChatDecision(ChatVerdict.BlockAndNotify);
    }
}

/// <summary>
/// The commands a gagged player may still run with a chat trigger: this plugin's own commands that answer the caller
/// instead of broadcasting, plus <c>OtherSettings.GagAllowedChatCommands</c>. Commands registered by other plugins through
/// the API and unknown commands are not on the list: the plugin cannot know what they print.
/// </summary>
internal static class GagChatCommands
{
    /// <summary>Keys (default names) of this plugin's commands that relay text to other players. Never allowed.</summary>
    internal static readonly HashSet<string> BroadcastCommandKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "css_say", "css_psay", "css_csay", "css_hsay", "css_cssay", "css_asay"
    };

    private static readonly object Gate = new();
    private static HashSet<string> _own = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forget the registered aliases (commands are being registered again).</summary>
    internal static void ResetOwn()
    {
        lock (Gate) _own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Called for every registered alias of this plugin's commands.</summary>
    internal static void NoteRegistered(string commandKey, string alias)
    {
        if (BroadcastCommandKeys.Contains(commandKey)) return;
        var bare = ChatTriggers.StripPrefix(alias);
        lock (Gate)
        {
            var copy = new HashSet<string>(_own, StringComparer.OrdinalIgnoreCase) { bare };
            _own = copy;
        }
    }

    internal static bool IsAllowed(string bareName, IEnumerable<string>? configured)
    {
        if (bareName.Length == 0 || ChatGate.IsSayLike(bareName)) return false;
        if (Volatile.Read(ref _own).Contains(bareName)) return true;
        if (configured == null) return false;
        foreach (var name in configured)
            if (ChatTriggers.StripPrefix(name).Equals(bareName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
