using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Modules.Commands;
using CS2_SimpleAdmin.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin;

public static class RegisterCommands
{
    private delegate void CommandCallback(CCSPlayerController? caller, CommandInfo.CommandCallback callback);
    
    private static readonly string CommandsPath = Path.Combine(CS2_SimpleAdmin.ConfigDirectory, "Commands.json");
    private static readonly List<CommandMapping> CommandMappings = CommandCatalog.Entries
        .Select(e => new CommandMapping(e.Key, (CommandInfo.CommandCallback)Delegate.CreateDelegate(
            typeof(CommandInfo.CommandCallback), CS2_SimpleAdmin.Instance, e.Method)))
        .ToList();

    /// <summary>
    /// Initializes command registration.
    /// If the commands config file does not exist, creates it and then recurses to register commands.
    /// Otherwise, directly registers commands from the configuration.
    /// </summary>
    public static void InitializeCommands()
    {
        if (!File.Exists(CommandsPath))
        {
            CreateConfig();
            InitializeCommands();
        }
        else
        {
            Register();
        }
    }

    /// <summary>
    /// Creates the default commands configuration JSON file with built-in commands and aliases.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    private static void CreateConfig()
    {
        var commands = new CommandsConfig
        {
            Commands = CommandCatalog.DefaultAliases.ToDictionary(kv => kv.Key, kv => new Command { Aliases = kv.Value })
        };
        
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(commands, options);
        File.WriteAllText(CommandsPath, json);
    }

    /// <summary>
    /// Reads the command configuration JSON file and registers all commands and their aliases with their callbacks.
    /// Also registers any custom commands previously stored.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    private static void Register()
    {
        var json = File.ReadAllText(CommandsPath);
        var commandsConfig = JsonSerializer.Deserialize<CommandsConfig>(json, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var registered = new List<(string Key, string Alias)>();
        if (commandsConfig?.Commands != null)
        {
            foreach (var (key, aliases) in CommandCatalog.ResolveAliases(commandsConfig.Commands.ToDictionary(c => c.Key, c => c.Value?.Aliases)))
            {
                CS2_SimpleAdmin._logger?.LogInformation($"Registering command: `{key}` with aliases: `{string.Join(", ", aliases)}`");
                var mapping = CommandMappings.First(m => m.CommandKey == key);
                foreach (var alias in aliases)
                {
                    CS2_SimpleAdmin.Instance.AddCommand(alias, "", mapping.Callback);
                    registered.Add((key, alias));
                }
            }

            // Commands added after Commands.json was generated are registered under their default name
            foreach (var mapping in CommandMappings.Where(m => !commandsConfig.Commands.ContainsKey(m.CommandKey)))
            {
                CS2_SimpleAdmin._logger?.LogInformation($"Registering command: `{mapping.CommandKey}` (not in Commands.json, using default alias)");
                CS2_SimpleAdmin.Instance.AddCommand(mapping.CommandKey, "", mapping.Callback);
                registered.Add((mapping.CommandKey, mapping.CommandKey));
            }
        }

        // Classification by key, published once per registration: the command listener only looks names up
        OwnCommands.Replace(registered);
        
        foreach (var (name, definitions) in CustomCommandRegistry.Definitions)
        {
            foreach (var definition in definitions)
            {
                CS2_SimpleAdmin._logger?.LogInformation($"Registering custom command: `{name}`");
                CS2_SimpleAdmin.Instance.AddCommand(name, definition.Description, definition.Callback);
            }
        }
    }
    
    /// <summary>
    /// Represents the JSON configuration structure for commands.
    /// </summary>
    private class CommandsConfig
    {
        public Dictionary<string, Command>? Commands { get; init; }
    }
    
    /// <summary>
    /// Represents a command definition containing a list of aliases.
    /// </summary>
    private class Command
    {
        public string[]? Aliases { get; init; }
    }
    
    /// <summary>
    /// Maps a command key to its respective command callback handler.
    /// </summary>
    private class CommandMapping(string commandKey, CommandInfo.CommandCallback callback)
    {
        public string CommandKey { get; } = commandKey;
        public CommandInfo.CommandCallback Callback { get; } = callback;
    }
}