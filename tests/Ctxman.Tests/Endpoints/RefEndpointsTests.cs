using System.Net;
using System.Text;
using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Core.Storage;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// AC10/AC11 (Spec §3.4 / §4.3 / §7.1): Page-Fault über
/// <c>GET /v1/sessions/{sid}/refs/{segment_id}</c>. Externalisiert + Blob vorhanden ⇒ 200
/// { content, content_type }, last_referenced_turn := current_turn, ref_expanded-Event.
/// Evicted/compacted ODER externalisiert mit gesweeptem Blob ⇒ 410 { summary, origin? }.
/// </summary>
public sealed class RefEndpointsTests
{
    private const string Tenant = "tenant-refs";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    private static async Task<BlobRef> PutBlob(CtxmanWebAppFactory factory, string content)
    {
        var store = factory.Services.GetRequiredService<IBlobStore>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await store.Put(Tenant, stream, "text/plain; charset=utf-8", CancellationToken.None);
    }

    // AC10: externalisiertes Segment mit vorhandenem Blob ⇒ 200 { content, content_type },
    // last_referenced_turn := current_turn, ref_expanded { segment_id } emittiert.
    [Fact]
    public async Task PageFault_Externalized_Returns200_UpdatesLiveness_EmitsRefExpanded()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        const string content = "the externalized tool output";
        var blobRef = await PutBlob(factory, content);

        var session = Wp4Fixtures.NewSession(
            Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000), currentTurn: 7);
        var segment = Segment.CreateExternalized(
            id: Ulid.NewUlid(),
            sessionId: session.Id,
            tenantId: Tenant,
            region: Region.Working,
            kind: "tool_result",
            role: Role.Tool,
            blobRef: blobRef,
            summary: "tool output summary",
            seq: Wp4Fixtures.NextSeq(),
            createdTurn: 0,
            tokens: 10);
        var segmentId = segment.Id;

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/v1/sessions/{session.Id}/refs/{segmentId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(content, doc.RootElement.GetProperty("content").GetString());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("content_type").GetString()));
        }

        using var verify = factory.CreateDbContext(Tenant);

        // Spec §3.4: last_referenced_turn := current_turn (7).
        var persisted = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
        Assert.Equal(7, persisted.LastReferencedTurn);

        var refExpanded = await verify.Events.AsNoTracking()
            .SingleAsync(ev => ev.SessionId == session.Id && ev.Type == "ref_expanded");
        using (var payload = JsonDocument.Parse(refExpanded.Payload))
        {
            Assert.Equal(segmentId.ToString(), payload.RootElement.GetProperty("segment_id").GetString());
        }
    }

    // AC11(a): evicted Segment ⇒ 410 { summary, origin? }. origin gesetzt ⇒ vorhanden.
    [Fact]
    public async Task PageFault_Evicted_Returns410_WithSummaryAndOrigin()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        var segment = new Segment(
            id: Ulid.NewUlid(),
            sessionId: session.Id,
            tenantId: Tenant,
            region: Region.Working,
            kind: "skill_content",
            source: null,
            role: Role.User,
            content: "stale",
            blobRef: null,
            refetchable: true,
            origin: "skill://gone",
            summary: "evicted skill summary",
            toolCallId: null,
            frameId: null,
            pinned: false,
            createdTurn: 0,
            lastReferencedTurn: 0,
            tokens: 10,
            seq: Wp4Fixtures.NextSeq(),
            state: SegmentState.Evicted);
        var segmentId = segment.Id;

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/v1/sessions/{session.Id}/refs/{segmentId}");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("evicted skill summary", doc.RootElement.GetProperty("summary").GetString());
        Assert.Equal("skill://gone", doc.RootElement.GetProperty("origin").GetString());
    }

    // AC11(b): externalisiertes Segment, dessen Blob gesweept/gelöscht wurde ⇒ 410 { summary }.
    [Fact]
    public async Task PageFault_Externalized_BlobSwept_Returns410_WithSummary()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        // Blob anlegen, Segment darauf zeigen lassen, dann Blob löschen (Sweep-Äquivalent).
        var blobRef = await PutBlob(factory, "soon to be swept");
        var store = factory.Services.GetRequiredService<IBlobStore>();

        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        var segment = Segment.CreateExternalized(
            id: Ulid.NewUlid(),
            sessionId: session.Id,
            tenantId: Tenant,
            region: Region.Working,
            kind: "tool_result",
            role: Role.Tool,
            blobRef: blobRef,
            summary: "summary survives sweep",
            seq: Wp4Fixtures.NextSeq(),
            createdTurn: 0,
            tokens: 10);
        var segmentId = segment.Id;

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        await store.Delete(Tenant, blobRef.Key, CancellationToken.None);

        var response = await client.GetAsync($"/v1/sessions/{session.Id}/refs/{segmentId}");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("summary survives sweep", doc.RootElement.GetProperty("summary").GetString());
    }
}
