using System.Net;
using System.Net.Http.Json;
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
/// API-/Worker-Akzeptanz der asynchronen Minor Collection (Spec §3.2 / §8). Deckt AC1
/// (Per-Session-Serialisierung), AC3 (Externalisierung mit Blob-Write) und das Pin-Verhalten
/// von AC13 (gepinntes Working-Segment wird nie angefasst) ab. Deterministisch über
/// <see cref="IGcJobQueue.WaitForIdleAsync"/> — kein <c>Thread.Sleep</c>.
/// </summary>
public sealed class MinorGcWorkerTests
{
    private const string Tenant = "tenant-gcworker";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    private static IGcJobQueue Queue(CtxmanWebAppFactory factory) =>
        factory.Services.GetRequiredService<IGcJobQueue>();

    private static IBlobStore Blobs(CtxmanWebAppFactory factory) =>
        factory.Services.GetRequiredService<IBlobStore>();

    // AC3 (Spec §3.2.2): großes, nicht-refetchables tool_result wird durch Minor GC externalisiert —
    // state=externalized, content=null, summary gesetzt, UND der Blob liegt im Blob Store.
    [Fact]
    public async Task MinorGc_ExternalizesLargeSegment_WritesBlob_AndClearsContent()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        // tool_result ist Externalize:true; > externalize_threshold_tokens (2000) ⇒ Kandidat (§3.2.2).
        var bigContent = new string('x', 12_000);
        var segment = Wp4Fixtures.LiveWorking(
            session.Id, Tenant, kind: "tool_result", tokens: 5000, content: bigContent, role: Role.Tool);
        var segmentId = segment.Id;

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        Queue(factory).Enqueue(new MinorGcJob(Tenant, session.Id, Ulid.NewUlid(), GcLevel.Minor));
        await Queue(factory).WaitForIdleAsync();

        string? blobKey;
        using (var db = factory.CreateDbContext(Tenant))
        {
            var persisted = await db.Segments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
            Assert.Equal(SegmentState.Externalized, persisted.State);
            Assert.Null(persisted.Content);
            Assert.False(string.IsNullOrEmpty(persisted.Summary));
            Assert.NotNull(persisted.BlobRef);
            blobKey = persisted.BlobRef!.Key;
        }

        Assert.True(await Blobs(factory).Exists(Tenant, blobKey!, CancellationToken.None));
    }

    // AC1 (Spec §8): zwei Minor-Jobs für DIESELBE Session laufen nicht verschränkt. Beobachtbar
    // über das Ergebnis: jedes externalisierbare Segment wird genau EINMAL externalisiert und die
    // segment_externalized-Events tragen eindeutige, lückenlose seq — keine doppelte Externalisierung,
    // keine korrumpierte Verschränkung.
    [Fact]
    public async Task TwoMinorJobs_SameSession_DoNotInterleave()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        var segments = Enumerable.Range(0, 6)
            .Select(_ => Wp4Fixtures.LiveWorking(
                session.Id, Tenant, kind: "tool_result", tokens: 5000,
                content: new string('y', 12_000), role: Role.Tool))
            .ToList();

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.AddRange(segments);
            await db.SaveChangesAsync();
        }

        // Beide Jobs vor dem ersten WaitForIdle enqueuen ⇒ sie konkurrieren um dieselbe Session.
        Queue(factory).Enqueue(new MinorGcJob(Tenant, session.Id, Ulid.NewUlid(), GcLevel.Minor));
        Queue(factory).Enqueue(new MinorGcJob(Tenant, session.Id, Ulid.NewUlid(), GcLevel.Minor));
        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        // Jedes Segment ist externalisiert (kein verschlucktes/doppelt verarbeitetes Segment).
        var persisted = await verify.Segments.AsNoTracking()
            .Where(s => s.SessionId == session.Id).ToListAsync();
        Assert.All(persisted, s => Assert.Equal(SegmentState.Externalized, s.State));

        // Genau ein segment_externalized je Segment — der zweite Job findet nichts mehr zu tun.
        var externalizedEvents = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id && ev.Type == "segment_externalized")
            .ToListAsync();
        Assert.Equal(segments.Count, externalizedEvents.Count);

        // Event-seq pro Session ist eindeutig und lückenlos ⇒ keine verschränkte Doppelvergabe.
        var seqs = externalizedEvents.Select(e => e.Seq).OrderBy(s => s).ToList();
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
    }

    // AC13 (Spec §2.2 I1 / §3.2): ein gepinntes Working-Segment wird vom Minor GC NIE angefasst —
    // es bliebe sonst externalisierbar (tool_result, > Threshold), bleibt aber live mit Inhalt.
    [Fact]
    public async Task MinorGc_NeverTouchesPinnedWorkingSegment()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        var pinned = Wp4Fixtures.LiveWorking(
            session.Id, Tenant, kind: "tool_result", tokens: 5000,
            content: new string('z', 12_000), pinned: true, role: Role.Tool);
        var pinnedId = pinned.Id;

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(pinned);
            await db.SaveChangesAsync();
        }

        Queue(factory).Enqueue(new MinorGcJob(Tenant, session.Id, Ulid.NewUlid(), GcLevel.Minor));
        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);
        var persisted = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == pinnedId);
        Assert.Equal(SegmentState.Live, persisted.State);
        Assert.False(string.IsNullOrEmpty(persisted.Content));
        Assert.Null(persisted.BlobRef);
    }

    // AC12 (Spec §3.3 / §8): ein Major-Job wird eingereiht (202 { job_id }), aber NICHT ausgeführt
    // (Compaction/Promotion ist WP5) — keine Segment-Mutation, keine GC-Events.
    [Fact]
    public async Task MajorGc_IsEnqueuedButNotExecuted()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        var segment = Wp4Fixtures.LiveWorking(
            session.Id, Tenant, kind: "tool_result", tokens: 5000,
            content: new string('m', 12_000), role: Role.Tool);
        var segmentId = segment.Id;

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync($"/v1/sessions/{session.Id}/gc", new { level = "major" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.TryGetProperty("job_id", out var jobId));
            Assert.True(Ulid.TryParse(jobId.GetString(), out _));
        }

        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);
        // Major hat das (externalisierbare) Segment NICHT angefasst.
        var persisted = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
        Assert.Equal(SegmentState.Live, persisted.State);
        var gcEvents = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id
                && (ev.Type == "segment_externalized" || ev.Type == "segment_evicted" || ev.Type == "unit_evicted"))
            .CountAsync();
        Assert.Equal(0, gcEvents);
    }
}
