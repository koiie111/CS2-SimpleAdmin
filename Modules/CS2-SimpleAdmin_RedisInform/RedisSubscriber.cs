using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;

namespace CS2_SimpleAdmin_RedisInform;

/// <summary>
/// Redis pub/sub bridge for admin-activity messages.
/// <list type="bullet">
/// <item>Wire format (compatible with v1): "&lt;senderId&gt;:{json}" with MessageKey/CallerName/MessageArgs, plus an
/// Id used for de-duplication (older senders without Id are still accepted).</item>
/// <item>Own messages (sender id) and already-seen message ids are ignored; re-shown messages are not re-published
/// (dontPublish), so there is no loop between servers.</item>
/// <item>Incoming payloads are validated (size, key, argument count/length) and parsed on the Redis thread; the game
/// thread receives at most <see cref="MaxIncomingQueued"/> pending messages and applies at most
/// <see cref="MaxAppliedPerFrame"/> per frame. A burst beyond that is dropped and counted.</item>
/// <item>Outgoing publishes are fire-and-forget on Redis' own pipeline, capped by <see cref="MaxOutgoingInFlight"/>.</item>
/// <item>Stop() cancels everything and disposes the connection in the background.</item>
/// </list>
/// </summary>
public class RedisSubscriber(string serverIdentifier, PluginConfig config, ILogger logger)
{
    internal const int MaxPayloadChars = 8192;
    internal const int MaxArgs = 16;
    internal const int MaxArgChars = 512;
    internal const int MaxIncomingQueued = 64;
    internal const int MaxAppliedPerFrame = 4;
    internal const int MaxOutgoingInFlight = 32;
    private const int SeenIdsCapacity = 512;

    internal sealed record ActivityMessage(string Id, string MessageKey, string? CallerName, string[] MessageArgs);

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<ActivityMessage> _incoming = new();
    private readonly SeenIds _seen = new(SeenIdsCapacity);
    private ConnectionMultiplexer? _connection;
    private ISubscriber? _subscriber;
    private int _incomingCount;
    private int _pumpScheduled;
    private int _outgoingInFlight;
    public long Dropped;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        IsRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var options = new ConfigurationOptions
                {
                    EndPoints = { config.RedisConnectionString },
                    Password = config.RedisPassword,
                    AbortOnConnectFail = false,
                    ConnectRetry = 5,
                    ConnectTimeout = 5000,
                    ReconnectRetryPolicy = new LinearRetry(1000)
                };

                _connection = await ConnectionMultiplexer.ConnectAsync(options);
                if (_cts.IsCancellationRequested) { await DisposeConnectionAsync(); return; }
                _subscriber = _connection.GetSubscriber();
                await _subscriber.SubscribeAsync(RedisChannel.Literal(config.SubscribeChannel), (_, message) => OnMessage(message));
            }
            catch (Exception ex)
            {
                logger.LogError("Redis subscriber failed to start: {Error}", ex.Message);
            }
        });
    }

    /// <summary>Unsubscribes and closes in the background. Pending game-thread work is discarded.</summary>
    public void Stop()
    {
        IsRunning = false;
        _cts.Cancel();
        while (_incoming.TryDequeue(out _)) { }
        _ = DisposeConnectionAsync();
    }

    private async Task DisposeConnectionAsync()
    {
        try
        {
            if (_subscriber != null) await _subscriber.UnsubscribeAllAsync();
            if (_connection != null)
            {
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Redis shutdown: {Error}", ex.Message);
        }
    }

    // ------------------------------------------------------------------ incoming (Redis thread)

    private void OnMessage(RedisValue value)
    {
        if (_cts.IsCancellationRequested) return;
        var parsed = Parse(value.ToString(), serverIdentifier);
        if (parsed == null || !_seen.Add(parsed.Id)) return;

        if (Interlocked.Increment(ref _incomingCount) > MaxIncomingQueued)
        {
            Interlocked.Decrement(ref _incomingCount);
            Interlocked.Increment(ref Dropped);
            return;
        }

        _incoming.Enqueue(parsed);
        SchedulePump();
    }

    /// <summary>Validates and parses one wire message. Null when it is ours, malformed or too large.</summary>
    internal static ActivityMessage? Parse(string raw, string ownSenderId)
    {
        if (raw.Length == 0 || raw.Length > MaxPayloadChars) return null;
        var separator = raw.IndexOf(':');
        if (separator <= 0) return null;
        var sender = raw[..separator];
        if (sender == ownSenderId) return null;

        JObject json;
        try
        {
            json = JObject.Parse(raw[(separator + 1)..]);
        }
        catch (JsonException)
        {
            return null;
        }

        var key = json.Value<string>("MessageKey");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) return null;
        var caller = json.Value<string>("CallerName");
        if (caller is { Length: > MaxArgChars }) caller = caller[..MaxArgChars];

        string[] args;
        var argsToken = json["MessageArgs"];
        if (argsToken is JArray array)
        {
            if (array.Count > MaxArgs) return null;
            args = array.Select(t => Truncate(t.Type == JTokenType.Null ? string.Empty : t.ToString())).ToArray();
        }
        else if (argsToken == null || argsToken.Type == JTokenType.Null)
        {
            args = [];
        }
        else
        {
            args = [Truncate(argsToken.ToString())];
        }

        // v1 senders send no Id: such messages are not de-duplicated (only the sender check applies), so two
        // identical legitimate messages are both shown
        var id = json.Value<string>("Id") ?? Guid.NewGuid().ToString("N");
        return new ActivityMessage(id, key, caller, args);

        static string Truncate(string s) => s.Length > MaxArgChars ? s[..MaxArgChars] : s;
    }

    private void SchedulePump()
    {
        if (Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) == 0)
            Server.NextWorldUpdate(Pump);
    }

    /// <summary>Game thread: shows at most a few messages per frame, the rest on following frames.</summary>
    private void Pump()
    {
        var api = CS2_SimpleAdmin_RedisInform.SharedApi;
        for (var i = 0; i < MaxAppliedPerFrame && !_cts.IsCancellationRequested && _incoming.TryDequeue(out var message); i++)
        {
            Interlocked.Decrement(ref _incomingCount);
            try
            {
                api?.ShowAdminActivity(message.MessageKey, message.CallerName ?? string.Empty, true, message.MessageArgs);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Unable to show Redis activity message: {Error}", ex.Message);
            }
        }

        Volatile.Write(ref _pumpScheduled, 0);
        if (!_incoming.IsEmpty && !_cts.IsCancellationRequested) SchedulePump();
    }

    // ------------------------------------------------------------------ outgoing (game thread → Redis)

    /// <summary>Serialises and publishes without blocking the caller; dropped when too many publishes are pending.</summary>
    public void Publish(string messageKey, string? callerName, object messageArgs)
    {
        var subscriber = _subscriber;
        if (subscriber == null || _cts.IsCancellationRequested) return;
        if (Interlocked.Increment(ref _outgoingInFlight) > MaxOutgoingInFlight)
        {
            Interlocked.Decrement(ref _outgoingInFlight);
            Interlocked.Increment(ref Dropped);
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        _seen.Add(id);
        var json = JsonConvert.SerializeObject(new { Id = id, MessageKey = messageKey, CallerName = callerName, MessageArgs = messageArgs });
        subscriber.PublishAsync(RedisChannel.Literal(config.PublishChannel), $"{serverIdentifier}:{json}", CommandFlags.FireAndForget)
            .ContinueWith(t =>
            {
                Interlocked.Decrement(ref _outgoingInFlight);
                if (t.IsFaulted) logger.LogWarning("Redis publish failed: {Error}", t.Exception?.GetBaseException().Message);
            }, TaskScheduler.Default);
    }

    /// <summary>Bounded set of recently seen message ids (oldest evicted).</summary>
    internal sealed class SeenIds(int capacity)
    {
        private readonly HashSet<string> _set = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        public bool Add(string id)
        {
            lock (_set)
            {
                if (!_set.Add(id)) return false;
                _order.Enqueue(id);
                if (_order.Count > capacity) _set.Remove(_order.Dequeue());
                return true;
            }
        }
    }
}
