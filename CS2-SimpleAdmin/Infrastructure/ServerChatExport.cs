using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace CS2_SimpleAdmin.Infrastructure;

public sealed class ServerChatExportConfig
{
    public string Endpoint { get; set; } = "";
    public int ServerId { get; set; }
    public string ApiKey { get; set; } = "";
}

internal sealed class ServerChatExport
{
    private const int BatchSize = 64;
    private const long DiskLimit = 64L * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly record struct ChatLine(long At, ulong SteamId, string Nickname, string Team, string Text);

    private sealed record Row(
        [property: JsonPropertyName("event_id")] string EventId,
        [property: JsonPropertyName("sent_at")] long At,
        [property: JsonPropertyName("steam_id")] string SteamId,
        [property: JsonPropertyName("nickname")] string Nickname,
        [property: JsonPropertyName("team")] string Team,
        [property: JsonPropertyName("message")] string Text);

    private readonly Channel<ChatLine> _queue = Channel.CreateBounded<ChatLine>(new BoundedChannelOptions(8192)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly int _serverId;
    private readonly string _spool;
    private readonly ILogger _log;
    private long _dropped;

    internal static ServerChatExport? Start(ServerChatExportConfig? config, string dataPath, ILogger log)
    {
        if (config is null || string.IsNullOrWhiteSpace(config.Endpoint))
            return null;

        if (config.ServerId <= 0 || config.ApiKey is not { Length: 64 }
            || config.ApiKey.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || !Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)))
        {
            log.LogWarning("Invalid ServerChatExport settings. Chat export is disabled.");
            return null;
        }

        return new ServerChatExport(config, uri, dataPath, log);
    }

    private ServerChatExport(ServerChatExportConfig config, Uri endpoint, string dataPath, ILogger log)
    {
        _endpoint = endpoint;
        _apiKey = config.ApiKey;
        _serverId = config.ServerId;
        _spool = Path.Combine(dataPath, "server-chat", _serverId.ToString(CultureInfo.InvariantCulture));
        _log = log;
        _ = Task.Run(RunAsync);
    }

    internal void Capture(ulong steamId, string nickname, string team, string text)
    {
        if (!_queue.Writer.TryWrite(new ChatLine(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), steamId, nickname, team, text)))
            Interlocked.Increment(ref _dropped);
    }

    internal void Stop()
    {
        _queue.Writer.TryComplete();
        _ = _stop.CancelAsync();
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        FileStream? spoolLock = null;
        var files = new Queue<(string Path, long Size)>();
        var pending = new List<ChatLine>(BatchSize);
        long diskBytes = 0;
        try
        {
            Directory.CreateDirectory(_spool);
            while (spoolLock is null)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    spoolLock = new FileStream(Path.Combine(_spool, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    await Task.Delay(Interval, token);
                }
            }

            foreach (var temporary in Directory.EnumerateFiles(_spool, "*.tmp")) File.Delete(temporary);
            foreach (var path in Directory.EnumerateFiles(_spool, "*.json").Order(StringComparer.Ordinal))
            {
                var file = new FileInfo(path);
                files.Enqueue((path, file.Length));
                diskBytes += file.Length;
            }

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(8),
                MaxResponseContentBufferSize = 4096
            };
            client.DefaultRequestHeaders.Add("X-Server-Chat-Id", _serverId.ToString(CultureInfo.InvariantCulture));
            client.DefaultRequestHeaders.Add("X-Server-Chat-Key", _apiKey);

            TimeSpan retry = Interval;
            var nextAttempt = DateTimeOffset.MinValue;
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(token))
            {
                var expired = DateTime.UtcNow - Retention;
                while (files.TryPeek(out var oldest) && File.GetLastWriteTimeUtc(oldest.Path) < expired)
                    RemoveFirst();

                Persist(8);
                ReportDrops();
                if (DateTimeOffset.UtcNow < nextAttempt)
                    continue;

                // Bound work per wakeup so uploads cannot starve persistence during a backlog.
                for (var index = 0; index < 8 && files.TryPeek(out var batch); index++)
                {
                    if (!await DeliverAsync(client, batch.Path, token))
                    {
                        nextAttempt = DateTimeOffset.UtcNow + retry;
                        retry = TimeSpan.FromSeconds(Math.Min(60, retry.TotalSeconds * 2));
                        break;
                    }
                    RemoveFirst();
                    retry = Interval;
                    nextAttempt = DateTimeOffset.MinValue;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _log.LogError("Server chat export stopped due to {ErrorType}.", error.GetType().Name);
        }
        finally
        {
            if (spoolLock != null)
            {
                try { Persist(int.MaxValue); }
                catch (Exception error) { _log.LogError("Cannot save server chat outbox: {ErrorType}.", error.GetType().Name); }
                spoolLock.Dispose();
            }
            ReportDrops();
        }

        void RemoveFirst()
        {
            var first = files.Peek();
            File.Delete(first.Path);
            files.Dequeue();
            diskBytes -= first.Size;
        }

        void Persist(int batches)
        {
            for (var index = 0; index < batches; index++)
            {
                while (pending.Count < BatchSize && _queue.Reader.TryRead(out var item))
                    pending.Add(item);

                if (pending.Count == 0)
                    return;

                var data = JsonSerializer.SerializeToUtf8Bytes(pending.Select(line => new Row(
                    Guid.NewGuid().ToString("N"), line.At, line.SteamId.ToString(CultureInfo.InvariantCulture),
                    Limit(line.Nickname, 128), line.Team, Limit(line.Text, 512))).ToArray());
                if (diskBytes + data.Length > DiskLimit)
                {
                    Interlocked.Add(ref _dropped, pending.Count);
                    pending.Clear();
                    continue;
                }

                var name = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D15", CultureInfo.InvariantCulture);
                var path = Path.Combine(_spool, name + "-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllBytes(path + ".tmp", data);
                File.Move(path + ".tmp", path);
                files.Enqueue((path, data.Length));
                diskBytes += data.Length;
                pending.Clear();
            }
        }
    }

    private async Task<bool> DeliverAsync(HttpClient client, string path, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
            using var response = await client.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var acknowledgement = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
                if (acknowledgement.RootElement.ValueKind == JsonValueKind.Object
                    && acknowledgement.RootElement.TryGetProperty("ok", out var ok)
                    && ok.ValueKind == JsonValueKind.True)
                    return true;
            }

            _log.LogWarning("Server chat endpoint returned {StatusCode}. Batch retained for retry.", (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            _log.LogWarning("Server chat endpoint returned an invalid acknowledgement. Batch retained for retry.");
            return false;
        }
    }

    private void ReportDrops()
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            _log.LogWarning("Server chat outbox reached its limit. {Dropped} messages dropped.", dropped);
    }

    private static string Limit(string value, int size)
    {
        if (value.Length <= size)
            return value;
        return value[..(char.IsHighSurrogate(value[size - 1]) ? size - 1 : size)];
    }
}
