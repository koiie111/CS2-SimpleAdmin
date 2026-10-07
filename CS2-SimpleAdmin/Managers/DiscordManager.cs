using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin.Managers;

public class DiscordManager(string webhookUrl)
{
    
    /// <summary>
    /// Queues a plain text message for the webhook (bounded HTTP queue, see <see cref="DiscordSender"/>).
    /// Returns immediately; nothing is serialised or sent on the caller's (game) thread.
    /// </summary>
    public Task SendMessageAsync(string message)
    {
        DiscordSender.EnqueueText(webhookUrl, message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Queues an embed message for the webhook (bounded HTTP queue, see <see cref="DiscordSender"/>).
    /// </summary>
    public Task SendEmbedAsync(Embed embed)
    {
        DiscordSender.EnqueueEmbed(webhookUrl, embed);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Converts a hexadecimal color string (e.g. "#FF0000") to its integer representation.
    /// </summary>
    /// <param name="hex">The hexadecimal color string, optionally starting with '#'.</param>
    /// <returns>An integer representing the color.</returns>
    public static int ColorFromHex(string hex)
    {
        if (hex.StartsWith($"#"))
        {
            hex = hex[1..];
        }

        return int.Parse(hex, System.Globalization.NumberStyles.HexNumber);
    }
}

/// <summary>
/// Represents a Discord embed message containing rich content such as title, description, fields, and images.
/// </summary>
public class Embed
{
    public int Color { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public Footer? Footer { get; init; }
    public string? Timestamp { get; init; }

    public List<EmbedField> Fields { get; } = [];

    /// <summary>
    /// Adds a field to the embed message.
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <param name="value">The value or content of the field.</param>
    /// <param name="inline">Whether the field should be displayed inline with other fields.</param>
    public void AddField(string name, string value, bool inline)
    {
        var field = new EmbedField
        {
            Name = name,
            Value = value,
            Inline = inline
        };
        
        Fields.Add(field);
    }
}

/// <summary>
/// Represents the footer section of a Discord embed message, including optional text and icon URL.
/// </summary>
public class Footer
{
    public string? Text { get; init; }
    public string? IconUrl { get; set; }
}

/// <summary>
/// Represents a field inside a Discord embed message.
/// </summary>
public class EmbedField
{
    public string? Name { get; init; }
    public string? Value { get; init; }
    public bool Inline { get; init; }
}

