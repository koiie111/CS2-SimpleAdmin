using CounterStrikeSharp.API.Core.Commands;

namespace CS2_SimpleAdmin;

/// <summary>
/// Commands registered by other plugins through the API, replayed by <see cref="RegisterCommands.Register"/>.
/// Kept apart from RegisterCommands, whose static initializer needs a running server.
/// </summary>
internal static class CustomCommandRegistry
{
    internal static readonly Dictionary<string, IList<CommandDefinition>> Definitions =
        new(StringComparer.InvariantCultureIgnoreCase);
}
