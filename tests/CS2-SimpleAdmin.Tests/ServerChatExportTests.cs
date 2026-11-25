using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CS2_SimpleAdmin.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CS2_SimpleAdmin.Tests;

public class ServerChatExportTests
{
    [Fact]
    public async Task RetriesPersistedBatchWithStableEventIds()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var directory = Directory.CreateTempSubdirectory("simpleadmin-chat-");
        var key = new string('a', 64);
        var worker = ServerChatExport.Start(new ServerChatExportConfig
        {
            Endpoint = $"http://127.0.0.1:{endpoint.Port}/ingest",
            ServerId = 42,
            ApiKey = key
        }, directory.FullName, NullLogger.Instance)!;

        var spool = Path.Combine(directory.FullName, "server-chat", "42");
        try
        {
            worker.Capture(76561198000000001UL, "Вираж", "CT", "Подожди у выхода");
            worker.Capture(76561198000000002UL, "mira", "", "Иду");
            var rejected = await ReadPayloadAsync(listener, 503, key, timeout.Token);
            Assert.Single(Directory.GetFiles(spool, "*.json"));

            var wrongAck = await ReadPayloadAsync(listener, 200, key, timeout.Token, "{}");
            Assert.Equal(rejected, wrongAck);
            Assert.Single(Directory.GetFiles(spool, "*.json"));

            var accepted = await ReadPayloadAsync(listener, 200, key, timeout.Token, "{\"ok\":true}");
            Assert.Equal(rejected, accepted);
            using var payload = JsonDocument.Parse(accepted);
            Assert.Equal(2, payload.RootElement.GetArrayLength());
            var first = payload.RootElement[0];
            Assert.Equal("76561198000000001", first.GetProperty("steam_id").GetString());
            Assert.Equal("CT", first.GetProperty("team").GetString());
            Assert.Equal("Подожди у выхода", first.GetProperty("message").GetString());
            Assert.Equal("", payload.RootElement[1].GetProperty("team").GetString());
            Assert.NotEqual(first.GetProperty("event_id").GetString(), payload.RootElement[1].GetProperty("event_id").GetString());

            while (Directory.GetFiles(spool, "*.json").Length != 0)
                await Task.Delay(25, timeout.Token);
        }
        finally
        {
            worker.Stop();
            // Wait for cleanup in the test only. The plugin never waits in Unload.
            var stopDeadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < stopDeadline)
            {
                try
                {
                    directory.Delete(recursive: true);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(25);
                }
            }
        }
    }

    private static async Task<string> ReadPayloadAsync(TcpListener listener, int status, string key, CancellationToken token, string response = "")
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Assert.Equal("POST /ingest HTTP/1.1", await reader.ReadLineAsync(token));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
        {
            var colon = line.IndexOf(':');
            headers.Add(line[..colon], line[(colon + 1)..].Trim());
        }

        Assert.Equal("42", headers["X-Server-Chat-Id"]);
        Assert.Equal(key, headers["X-Server-Chat-Key"]);
        var body = new char[int.Parse(headers["Content-Length"])];
        Assert.Equal(body.Length, await reader.ReadBlockAsync(body.AsMemory(), token));
        var reason = status == 200 ? "OK" : "Service Unavailable";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n{response}"), token);
        return new string(body);
    }
}
