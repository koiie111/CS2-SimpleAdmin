using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2_SimpleAdmin.Infrastructure;

namespace CS2_SimpleAdmin.Managers;

/// <summary>
/// Discord webhook transport.
/// <list type="bullet">
/// <item>All sends go through the bounded HTTP queue (<see cref="Runtime.Http"/>: 64 pending, 2 in flight). When it is
/// full the notification is dropped and counted – Discord logs are best-effort and must never back up into the
/// game or the database queue.</item>
/// <item>The caller builds the message from game data on the game thread; JSON serialisation (typed DTOs,
/// source-generated, trim-safe) and the request happen on the worker.</item>
/// <item>Each request has a 15 s deadline; request, content and response are disposed; HTTP 429 honours Retry-After
/// (capped at 30 s) and pauses that webhook; 429/5xx/timeouts are retried at most twice.</item>
/// </list>
/// </summary>
internal static class DiscordSender
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> PausedUntil = new();
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);
    internal const int MaxAttempts = 3;

    public static bool EnqueueText(string url, string message) =>
        EnqueueRaw(url, () => JsonSerializer.Serialize(new TextPayload { Content = message }, PayloadContext.Default.TextPayload));

    public static bool EnqueueEmbed(string url, Embed embed) =>
        EnqueueRaw(url, () => JsonSerializer.Serialize(EmbedPayload.From(embed), PayloadContext.Default.EmbedPayload));

    private static bool EnqueueRaw(string url, Func<string> serialize)
    {
        var queue = Runtime.Http;
        if (queue != null && queue.TryEnqueue("discord", async ct => { await SendAsync(url, serialize(), ct); }))
            return true;

        Interlocked.Increment(ref PluginMetrics.HttpRejected);
        RateLimitedLog.Warning("discord.dropped", "Discord queue full or not started; notification dropped");
        return false;
    }

    /// <returns>True when delivered.</returns>
    internal static async Task<bool> SendAsync(string url, string json, CancellationToken ct, HttpMessageInvoker? invoker = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        invoker ??= Client;
        delay ??= Task.Delay;
        for (var attempt = 1; ; attempt++)
        {
            if (PausedUntil.TryGetValue(url, out var until) && until > DateTime.UtcNow)
            {
                var wait = until - DateTime.UtcNow;
                await delay(wait < MaxRetryAfter ? wait : MaxRetryAfter, ct).ConfigureAwait(false);
            }

            TimeSpan retryDelay;
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await invoker.SendAsync(request, cts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref PluginMetrics.HttpSent);
                    return true;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Interlocked.Increment(ref PluginMetrics.HttpRateLimited);
                    var after = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    if (after > MaxRetryAfter) after = MaxRetryAfter;
                    PausedUntil[url] = DateTime.UtcNow + after;
                    retryDelay = TimeSpan.Zero; // the pause above is applied at the start of the next attempt
                }
                else if ((int)response.StatusCode >= 500)
                {
                    retryDelay = TimeSpan.FromSeconds(2);
                }
                else
                {
                    Interlocked.Increment(ref PluginMetrics.HttpFailed);
                    RateLimitedLog.Warning("discord.status", $"Discord webhook rejected the message: {(int)response.StatusCode} {response.ReasonPhrase}");
                    return false;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                retryDelay = TimeSpan.FromSeconds(2); // per-request deadline hit
            }
            catch (HttpRequestException ex)
            {
                RateLimitedLog.Warning("discord.http", $"Error sending discord message: {ex.Message}");
                retryDelay = TimeSpan.FromSeconds(2);
            }

            if (attempt >= MaxAttempts)
            {
                Interlocked.Increment(ref PluginMetrics.HttpFailed);
                RateLimitedLog.Warning("discord.giveup", $"Discord message dropped after {attempt} attempts");
                return false;
            }

            if (retryDelay > TimeSpan.Zero)
                await delay(retryDelay, ct).ConfigureAwait(false);
        }
    }

    internal static void ResetForTests() => PausedUntil.Clear();

    internal sealed class TextPayload
    {
        [JsonPropertyName("content")] public string? Content { get; init; }
    }

    internal sealed class EmbedPayload
    {
        [JsonPropertyName("embeds")] public EmbedDto[] Embeds { get; init; } = [];

        public static EmbedPayload From(Embed embed) => new()
        {
            Embeds =
            [
                new EmbedDto
                {
                    Color = embed.Color,
                    Title = string.IsNullOrEmpty(embed.Title) ? null : embed.Title,
                    Description = string.IsNullOrEmpty(embed.Description) ? null : embed.Description,
                    Thumbnail = string.IsNullOrEmpty(embed.ThumbnailUrl) ? null : new UrlDto { Url = embed.ThumbnailUrl },
                    Image = string.IsNullOrEmpty(embed.ImageUrl) ? null : new UrlDto { Url = embed.ImageUrl },
                    Footer = string.IsNullOrEmpty(embed.Footer?.Text) ? null : new FooterDto { Text = embed.Footer.Text, IconUrl = embed.Footer.IconUrl },
                    Timestamp = embed.Timestamp,
                    Fields = embed.Fields.Count > 0
                        ? embed.Fields.Select(f => new FieldDto { Name = f.Name, Value = f.Value, Inline = f.Inline }).ToArray()
                        : null
                }
            ]
        };
    }

    internal sealed class EmbedDto
    {
        [JsonPropertyName("color")] public int Color { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("thumbnail")] public UrlDto? Thumbnail { get; init; }
        [JsonPropertyName("image")] public UrlDto? Image { get; init; }
        [JsonPropertyName("footer")] public FooterDto? Footer { get; init; }
        [JsonPropertyName("timestamp")] public string? Timestamp { get; init; }
        [JsonPropertyName("fields")] public FieldDto[]? Fields { get; init; }
    }

    internal sealed class UrlDto
    {
        [JsonPropertyName("url")] public string? Url { get; init; }
    }

    internal sealed class FooterDto
    {
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("icon_url")] public string? IconUrl { get; init; }
    }

    internal sealed class FieldDto
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("value")] public string? Value { get; init; }
        [JsonPropertyName("inline")] public bool Inline { get; init; }
    }
}

/// <summary>Source-generated JSON metadata for webhook payloads (same JSON as the old anonymous objects).</summary>
[JsonSerializable(typeof(DiscordSender.TextPayload))]
[JsonSerializable(typeof(DiscordSender.EmbedPayload))]
internal partial class PayloadContext : JsonSerializerContext;
