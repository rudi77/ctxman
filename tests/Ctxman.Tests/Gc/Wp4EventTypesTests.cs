using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ctxman.Api.Gc;
using Ctxman.Core.Domain;
using Ctxman.Core.Gc;
using Ctxman.Core.Storage;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ctxman.Tests.Gc;

/// <summary>
/// AC17 (Spec §6): über die WP4-Operationen hinweg treten alle dokumentierten Lifecycle-Event-Typen
/// mindestens einmal auf — <c>segment_externalized</c>, <c>segment_evicted</c>, <c>unit_evicted</c>,
/// <c>ref_expanded</c>, <c>blob_swept</c>, <c>watermark_crossed</c> — mit snake_case-Payloads und den
/// dokumentierten Feldern. Deterministisch (WaitForIdleAsync + fixierter TimeProvider, kein Sleep).
/// </summary>
public sealed class Wp4EventTypesTests
{
    private const string Tenant = "tenant-events17";
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    [Fact]
    public async Task AllWp4EventTypes_AppearWithSnakeCasePayloads()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var queue = factory.Services.GetRequiredService<IGcJobQueue>();

        // --- Session 1: Minor GC erzeugt segment_externalized + segment_evicted + unit_evicted,
        //     anschließend ein Page-Fault ein ref_expanded. Budget groß ⇒ kein Watermark hier.
        var gcSession = Wp4Fixtures.NewSession(
            Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000), currentTurn: 20);

        // externalisierbar: großes, nicht-refetchables tool_result (> threshold).
        var bigToolResult = Wp4Fixtures.LiveWorking(
            gcSession.Id, Tenant, kind: "tool_result", tokens: 5000,
            content: new string('x', 12_000), role: Role.Tool, lastReferencedTurn: 20);
        var bigToolResultId = bigToolResult.Id;

        // clean-page: refetchable skill, TTL(8) abgelaufen ⇒ segment_evicted.
        var skill = Wp4Fixtures.LiveWorking(
            gcSession.Id, Tenant, kind: "skill_content", tokens: 10, refetchable: true,
            origin: "skill://x", role: Role.User, lastReferencedTurn: 0);

        // gekoppelte Unit (tool_call + kleines tool_result via tool_call_id), TTL(2) abgelaufen,
        // tool_result zu klein zum Externalisieren ⇒ unit_evicted (genau ein Event über beide).
        var coupledCall = Wp4Fixtures.LiveWorking(
            gcSession.Id, Tenant, kind: "tool_call", tokens: 5, content: "call",
            toolCallId: "u1", role: Role.Assistant, lastReferencedTurn: 0);
        var coupledResult = Wp4Fixtures.LiveWorking(
            gcSession.Id, Tenant, kind: "tool_result", tokens: 5, content: "result",
            toolCallId: "u1", role: Role.Tool, lastReferencedTurn: 0);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(gcSession);
            db.Segments.AddRange(bigToolResult, skill, coupledCall, coupledResult);
            await db.SaveChangesAsync();
        }

        var gc = await client.PostAsJsonAsync($"/v1/sessions/{gcSession.Id}/gc", new { level = "minor" });
        Assert.Equal(HttpStatusCode.Accepted, gc.StatusCode);
        await queue.WaitForIdleAsync();

        // Page-Fault auf das externalisierte Segment ⇒ ref_expanded.
        var refResponse = await client.GetAsync($"/v1/sessions/{gcSession.Id}/refs/{bigToolResultId}");
        Assert.Equal(HttpStatusCode.OK, refResponse.StatusCode);

        // --- Session 2: kleines Budget, ein Render kreuzt soft ⇒ watermark_crossed.
        var wmSession = Wp4Fixtures.NewSession(
            Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1000), currentTurn: 0);
        var filler = Wp4Fixtures.LiveWorking(
            wmSession.Id, Tenant, kind: "user_msg", tokens: 700, role: Role.User, lastReferencedTurn: 0);
        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(wmSession);
            db.Segments.Add(filler);
            await db.SaveChangesAsync();
        }
        var render = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{wmSession.Id}/render")
        {
            Content = JsonContent.Create(new { provider = "anthropic", turn_advance = false }),
        };
        var renderResponse = await client.SendAsync(render);
        renderResponse.EnsureSuccessStatusCode();
        await queue.WaitForIdleAsync();

        // --- Blob-Sweep: ein unreferenzierter, alter Blob ⇒ blob_swept.
        var sweepKey = new string('a', 64);
        const long sweepSize = 8192;
        var sweepSeg = new Segment(
            id: Ulid.NewUlid(),
            sessionId: gcSession.Id,
            tenantId: Tenant,
            region: Region.Working,
            kind: "tool_result",
            source: null,
            role: Role.Tool,
            content: null,
            blobRef: new BlobRef("fs", sweepKey, sweepSize, "text/plain; charset=utf-8"),
            refetchable: false,
            origin: null,
            summary: "swept",
            toolCallId: null,
            frameId: null,
            pinned: false,
            createdTurn: 0,
            lastReferencedTurn: 0,
            tokens: 10,
            seq: Wp4Fixtures.NextSeq(),
            state: SegmentState.Evicted); // keine Live-Referenz ⇒ unreferenced
        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Segments.Add(sweepSeg);
            await db.SaveChangesAsync();
        }

        var sweepStore = new FakeBlobStore();
        sweepStore.Add(Tenant, new BlobInfo(sweepKey, sweepSize, Base));
        var sweeper = new BlobSweeper(new FakeTimeProvider(Base.AddHours(100)));
        using (var db = factory.CreateDbContext(Tenant))
        {
            await sweeper.SweepTenantAsync(Tenant, db, sweepStore, CancellationToken.None);
        }

        // --- Assert: jeder Typ kommt mindestens einmal vor, Payload ist snake_case mit den Feldern.
        using var verify = factory.CreateDbContext(Tenant);
        var byType = await verify.Events.AsNoTracking()
            .GroupBy(ev => ev.Type)
            .Select(g => new { g.Key, Payload = g.First().Payload })
            .ToDictionaryAsync(x => x.Key, x => x.Payload);

        foreach (var type in new[]
                 {
                     "segment_externalized", "segment_evicted", "unit_evicted",
                     "ref_expanded", "blob_swept", "watermark_crossed",
                 })
        {
            Assert.True(byType.ContainsKey(type), $"Missing event type: {type}");
        }

        AssertField(byType["segment_externalized"], "segment_id");
        AssertField(byType["segment_externalized"], "blob_key");
        AssertField(byType["segment_evicted"], "segment_id");
        AssertField(byType["unit_evicted"], "unit_id");
        AssertField(byType["unit_evicted"], "segment_ids");
        AssertField(byType["ref_expanded"], "segment_id");
        AssertField(byType["blob_swept"], "key");
        AssertField(byType["blob_swept"], "size_bytes");
        AssertField(byType["blob_swept"], "reason");
        AssertField(byType["watermark_crossed"], "level");
    }

    private static void AssertField(string payload, string snakeCaseField)
    {
        using var doc = JsonDocument.Parse(payload);
        Assert.True(
            doc.RootElement.TryGetProperty(snakeCaseField, out _),
            $"Payload {payload} is missing snake_case field '{snakeCaseField}'.");
    }

    private sealed class FakeBlobStore : IBlobStore
    {
        private readonly Dictionary<string, Dictionary<string, BlobInfo>> _byTenant = new();

        public void Add(string tenantId, BlobInfo info)
        {
            if (!_byTenant.TryGetValue(tenantId, out var blobs))
            {
                blobs = new Dictionary<string, BlobInfo>(StringComparer.Ordinal);
                _byTenant[tenantId] = blobs;
            }
            blobs[info.Key] = info;
        }

#pragma warning disable CS1998
        public async IAsyncEnumerable<BlobInfo> List(
            string tenantId, [EnumeratorCancellation] CancellationToken ct)
        {
            if (_byTenant.TryGetValue(tenantId, out var blobs))
            {
                foreach (var info in blobs.Values.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    yield return info;
                }
            }
        }
#pragma warning restore CS1998

        public ValueTask Delete(string tenantId, string key, CancellationToken ct)
        {
            if (_byTenant.TryGetValue(tenantId, out var blobs))
            {
                blobs.Remove(key);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<BlobRef> Put(string tenantId, Stream content, string contentType, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<Stream> Get(string tenantId, string key, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> Exists(string tenantId, string key, CancellationToken ct)
            => ValueTask.FromResult(_byTenant.TryGetValue(tenantId, out var b) && b.ContainsKey(key));
    }
}
