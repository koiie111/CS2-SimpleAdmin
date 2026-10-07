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
