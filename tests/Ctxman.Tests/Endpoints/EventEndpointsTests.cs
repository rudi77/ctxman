using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Tests.Support;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// AC16 (Spec §4.3 / §6 / §10): <c>GET /v1/sessions/{sid}/events?after_seq=N</c> liefert nur Events
/// mit seq &gt; N, aufsteigend, tenant-isoliert (fremde Events nie sichtbar). Die SSE-Variante
/// (Accept: text/event-stream bzw. ?stream=true) liefert Content-Type text/event-stream, Frames und
/// TERMINIERT (hängt nicht).
/// </summary>
public sealed class EventEndpointsTests
{
    private const string Tenant = "tenant-events";
    private const string OtherTenant = "tenant-events-other";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory, string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        return client;
    }

    private static EventRecord Event(string tenant, Ulid sessionId, long seq, string type) => new()
    {
        Id = Ulid.NewUlid(),
        TenantId = tenant,
        SessionId = sessionId,
        Type = type,
        Payload = "{\"k\":\"v\"}",
        Seq = seq,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seq),
    };

    private static async Task<Ulid> SeedSessionWithEvents(CtxmanWebAppFactory factory, string tenant)
    {
        Wp4Fixtures.EnsureHost(factory);
        var session = Wp4Fixtures.NewSession(tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        using var db = factory.CreateDbContext(tenant);
        db.Sessions.Add(session);
        db.Events.AddRange(
            Event(tenant, session.Id, 0, "segment_appended"),
            Event(tenant, session.Id, 1, "render_served"),
            Event(tenant, session.Id, 2, "watermark_crossed"),
            Event(tenant, session.Id, 3, "segment_evicted"));
        await db.SaveChangesAsync();
        return session.Id;
    }

    [Fact]
    public async Task Events_AfterSeq_ReturnsOnlyGreater_Ascending()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory, Tenant);
        var sessionId = await SeedSessionWithEvents(factory, Tenant);

        var response = await client.GetAsync($"/v1/sessions/{sessionId}/events?after_seq=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var seqs = doc.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("seq").GetInt64()).ToList();

        Assert.Equal(new long[] { 2, 3 }, seqs);
    }

    [Fact]
    public async Task Events_AreTenantIsolated()
    {
        await using var factory = new CtxmanWebAppFactory();

        // Eigene + fremde Session mit Events; der eigene Stream darf die fremde Session nie sehen.
        var sessionId = await SeedSessionWithEvents(factory, Tenant);
        var foreignSessionId = await SeedSessionWithEvents(factory, OtherTenant);

        var client = ClientFor(factory, Tenant);

        // Eigene Session: alle vier Events sichtbar.
        var own = await client.GetAsync($"/v1/sessions/{sessionId}/events?after_seq=-1");
        using (var doc = JsonDocument.Parse(await own.Content.ReadAsStringAsync()))
        {
            Assert.Equal(4, doc.RootElement.GetProperty("events").GetArrayLength());
        }

        // Fremde Session über den eigenen Tenant ⇒ 404 (Global Query Filter, kein Leak).
        var foreign = await client.GetAsync($"/v1/sessions/{foreignSessionId}/events?after_seq=-1");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Events_Sse_ReturnsEventStreamContentType_AndTerminates()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory, Tenant);
        var sessionId = await SeedSessionWithEvents(factory, Tenant);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/sessions/{sessionId}/events?after_seq=-1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // ResponseHeadersRead + die Lese-Operation müssen terminieren (kein Hängen). Ein großzügiges
        // Timeout schützt den Test-Runner, ohne auf Timing zu setzen (der Endpunkt schließt den Stream).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(cts.Token);

        // Mindestens ein SSE-Frame im Backlog (id:/data:), und der Stream ist beendet (ReadToEnd kehrte zurück).
        Assert.Contains("data:", body);
        Assert.Contains("id:", body);
    }

    [Fact]
    public async Task Events_Stream_QueryFlag_ReturnsEventStream()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory, Tenant);
        var sessionId = await SeedSessionWithEvents(factory, Tenant);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await client.GetAsync(
            $"/v1/sessions/{sessionId}/events?after_seq=-1&stream=true",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("data:", body);
    }
}
