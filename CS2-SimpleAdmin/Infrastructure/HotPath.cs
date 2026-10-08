using CounterStrikeSharp.API.Core;

namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Chat command triggers ("!", "/" …) from core.json, copied into an array once instead of
/// Concat/Any/lambda allocations on every chat message.
/// </summary>
internal static class ChatTriggers
{
    private sealed record Cache(object PublicSource, object SilentSource, string[] Triggers);

    private static Cache? _cache;

    /// <summary>The configured public and silent triggers (cached array).</summary>
    public static string[] Current => GetTriggers();

    public static bool StartsWithTrigger(string message)
    {
        var triggers = GetTriggers();
        foreach (var trigger in triggers)
            if (trigger.Length > 0 && message.StartsWith(trigger, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string[] GetTriggers()
    {
        IEnumerable<string> publicTriggers = CoreConfig.PublicChatTrigger;
        IEnumerable<string> silentTriggers = CoreConfig.SilentChatTrigger;
        var cache = _cache;
        if (cache != null && ReferenceEquals(cache.PublicSource, publicTriggers) && ReferenceEquals(cache.SilentSource, silentTriggers))
            return cache.Triggers;

        var triggers = publicTriggers.Concat(silentTriggers).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToArray();
        _cache = new Cache(publicTriggers, silentTriggers, triggers);
        return triggers;
    }

    /// <summary>
    /// For a gagged player: the chat-trigger message as one console command ("css_name args"), or null when the line must
    /// be dropped. A line qualifies only if all of these hold: exactly one trigger prefix; a command name of ASCII letters,
    /// digits and '_' (optional "css_" prefix); the name is on the allow-list (<paramref name="allowed"/>, bare name); the
    /// whole remainder is printable ASCII without ';', quotes and backslashes (no command chaining, no quoting tricks) and at
    /// most 128 characters. "!привет", "!ez gg", "!x;quit", "!say hi", "!!x" and "! " all give null.
    /// </summary>
    public static string? ToSilentCommand(string message, Func<string, bool> allowed) => ToSilentCommand(message, GetTriggers(), allowed);

    internal static string? ToSilentCommand(string message, string[] triggers, Func<string, bool> allowed)
    {
        foreach (var trigger in triggers)
        {
            if (trigger.Length == 0 || !message.StartsWith(trigger, StringComparison.Ordinal)) continue;

            var rest = message.AsSpan(trigger.Length).Trim();
            if (rest.IsEmpty || rest.Length > 128) return null;

            foreach (var c in rest)
                if (c is < ' ' or > '~' or ';' or '"' or '\\') return null;

            var nameEnd = rest.IndexOf(' ');
            var name = nameEnd < 0 ? rest : rest[..nameEnd];
            if (name.Length > 32) return null;
            foreach (var c in name)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
                    return null;

            var bare = StripPrefix(name.ToString()).ToLowerInvariant();
            if (bare.Length == 0 || !allowed(bare)) return null;

            var args = nameEnd < 0 ? "" : rest[nameEnd..].Trim().ToString();
            return args.Length == 0 ? "css_" + bare : "css_" + bare + " " + args;
        }

        return null;
    }

    /// <summary>"css_admin" → "admin"; "admin" → "admin".</summary>
    internal static string StripPrefix(string name) =>
        name.StartsWith("css_", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

    /// <summary>For tests: the matching rule without CoreConfig.</summary>
    internal static bool StartsWithAny(string message, string[] triggers)
    {
        foreach (var trigger in triggers)
            if (trigger.Length > 0 && message.StartsWith(trigger, StringComparison.Ordinal))
                return true;
        return false;
    }
}

/// <summary>
/// OtherSettings.AdditionalCommandsToLog as a case-insensitive set, rebuilt only when the configured list changes.
/// Every command still goes through the listener (arbitrary commands keep being loggable); only the lookup is O(1).
/// </summary>
internal static class CommandLogFilter
{
    private sealed record Cache(List<string> Source, int Count, HashSet<string> Set);

    private static Cache? _cache;

    public static bool ShouldLog(List<string> configured, string command)
    {
        if (configured.Count == 0) return false;
        var cache = _cache;
        if (cache == null || !ReferenceEquals(cache.Source, configured) || cache.Count != configured.Count)
        {
            cache = new Cache(configured, configured.Count, new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase));
            _cache = cache;
        }

        return cache.Set.Contains(command);
    }
}
