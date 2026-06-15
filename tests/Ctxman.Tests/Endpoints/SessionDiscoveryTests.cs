using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Tests.Support;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// API-Tests für die Session-Discovery: <c>GET /v1/sessions</c> (tenant-gescopter Snapshot)
/// und der SSE-Stream <c>GET /v1/sessions/events</c> (snapshot-Frame + Live-<c>session_created</c>).
/// Tenant über <c>X-Tenant-Id</c> (Spec §4.1, Modus none); Tenant-Isolation über §10.
/// </summary>
public sealed class SessionDiscoveryTests
{
    private static HttpClient ClientFor(CtxmanWebAppFactory factory, string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        return client;
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/v1/sessions", new { });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;
    }

    [Fact] // GET /v1/sessions ⇒ 200 { sessions: [...] } mit allen Sessions des Tenants, neueste zuerst.
    public async Task ListSessions_ReturnsAllTenantSessions_NewestFirst()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory, "tenant-list");

        var first = await CreateSessionAsync(client);
        var second = await CreateSessionAsync(client);

        var response = await client.GetAsync("/v1/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sessions = doc.RootElement.GetProperty("sessions");
        Assert.Equal(2, sessions.GetArrayLength());

        // Neueste zuerst (OrderByDescending CreatedAt) — zweite Session vor der ersten.
        Assert.Equal(second, sessions[0].GetProperty("session_id").GetString());
        Assert.Equal(first, sessions[1].GetProperty("session_id").GetString());

        // Summary trägt Budget-Status wie das Detail (snake_case).
        Assert.True(sessions[0].TryGetProperty("tokens_used", out _));
        Assert.True(sessions[0].TryGetProperty("watermark_state", out _));
        Assert.True(sessions[0].TryGetProperty("current_turn", out _));
    }

    [Fact] // §10: GET /v1/sessions ist tenant-isoliert — fremde Sessions erscheinen nie.
    public async Task ListSessions_DoesNotLeakOtherTenant()
    {
        using var factory = new CtxmanWebAppFactory();

        var owner = ClientFor(factory, "tenant-a");
        await CreateSessionAsync(owner);
        await CreateSessionAsync(owner);

        var other = ClientFor(factory, "tenant-b");
        var response = await other.GetAsync("/v1/sessions");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("sessions").GetArrayLength());
    }

    [Fact] // GET /v1/sessions/events ⇒ text/event-stream mit snapshot-Frame der bestehenden IDs.
    public async Task SessionEvents_SendsSnapshotFrame_WithExistingIds()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory, "tenant-sse");
        var sid = await CreateSessionAsync(client);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/sessions/events");
        var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var reader = await SseReader.OpenAsync(response);
        var snapshot = await reader.ReadFrameAsync("snapshot", TimeSpan.FromSeconds(5));
        Assert.NotNull(snapshot);
        using var doc = JsonDocument.Parse(snapshot!);
        var ids = doc.RootElement.GetProperty("session_ids").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(sid, ids);
    }

    [Fact] // Live-Push: eine NACH dem Connect angelegte Session löst einen session_created-Frame aus.
    public async Task SessionEvents_PushesSessionCreated_AfterConnect()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory, "tenant-live");

        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/sessions/events");
        var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var reader = await SseReader.OpenAsync(response);
        // Snapshot zuerst konsumieren (leer), dann eine neue Session anlegen.
        await reader.ReadFrameAsync("snapshot", TimeSpan.FromSeconds(5));

        var creator = ClientFor(factory, "tenant-live");
        var sid = await CreateSessionAsync(creator);

        var data = await reader.ReadFrameAsync("session_created", TimeSpan.FromSeconds(5));
        Assert.NotNull(data);
        using var doc = JsonDocument.Parse(data!);
        Assert.Equal(sid, doc.RootElement.GetProperty("session_id").GetString());
    }

    [Fact] // §10: der SSE-Stream pusht nur Ereignisse des eigenen Tenants — kein Cross-Tenant-Leak.
    public async Task SessionEvents_DoesNotPushOtherTenantCreates()
    {
        using var factory = new CtxmanWebAppFactory();
        var listener = ClientFor(factory, "tenant-x");

        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/sessions/events");
        var response = await listener.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var reader = await SseReader.OpenAsync(response);
        await reader.ReadFrameAsync("snapshot", TimeSpan.FromSeconds(5));

        // Ein anderer Tenant legt eine Session an — darf beim tenant-x-Listener NICHT ankommen.
        var foreign = ClientFor(factory, "tenant-y");
        await CreateSessionAsync(foreign);

        var leaked = await reader.ReadFrameAsync("session_created", TimeSpan.FromSeconds(2));
        Assert.Null(leaked); // kein session_created-Frame innerhalb des Fensters.
    }

    /// <summary>
    /// Liest einen SSE-Stream über GENAU EINEN <see cref="StreamReader"/> (zwei Reader über denselben
    /// Netzwerk-Stream würden gepufferte Bytes verlieren). Überspringt Heartbeat-Kommentare und
    /// liefert die <c>data:</c>-Payload des nächsten <c>event: {eventType}</c>-Frames; null bei Timeout.
    /// </summary>
    private sealed class SseReader : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly StreamReader _reader;

        private SseReader(Stream stream, StreamReader reader)
        {
            _stream = stream;
            _reader = reader;
        }

        public static async Task<SseReader> OpenAsync(HttpResponseMessage response)
        {
            var stream = await response.Content.ReadAsStreamAsync();
            return new SseReader(stream, new StreamReader(stream));
        }

        public async Task<string?> ReadFrameAsync(string eventType, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                string? currentEvent = null;
                while (!cts.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync(cts.Token);
                    if (line is null) return null;
                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                    {
                        currentEvent = line["event: ".Length..];
                    }
                    else if (line.StartsWith("data: ", StringComparison.Ordinal) && currentEvent == eventType)
                    {
                        return line["data: ".Length..];
                    }
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _stream.DisposeAsync();
        }
    }
}
