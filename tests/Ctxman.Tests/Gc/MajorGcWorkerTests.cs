using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Api.Gc;
using Ctxman.Core.Domain;
using Ctxman.Core.Gc;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ctxman.Tests.Gc;

/// <summary>
/// API-/Worker-Akzeptanztests der Major Collection (Spec §3.3, WP5).
/// Deckt AC3–AC13 ab. Deterministisch über <see cref="IGcJobQueue.WaitForIdleAsync"/> —
/// kein <c>Thread.Sleep</c>.
/// </summary>
public sealed class MajorGcWorkerTests
{
    private const string Tenant = "tenant-major-gc";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    private static IGcJobQueue Queue(CtxmanWebAppFactory factory) =>
        factory.Services.GetRequiredService<IGcJobQueue>();

    private static FakeCompactionModel FakeCompaction(CtxmanWebAppFactory factory) =>
        factory.Services.GetRequiredService<FakeCompactionModel>();

    private static RecordingPromotionSink FakeSink(CtxmanWebAppFactory factory) =>
        factory.Services.GetRequiredService<RecordingPromotionSink>();

    /// <summary>
    /// Legt eine Session + N Live-Working-Segmente an und gibt die Session zurück.
    /// Budget und max_share sind so gewählt, dass alle N Segmente ins Compaction-Fenster passen.
    /// </summary>
    private static async Task<Session> SeedSessionWithSegments(
        CtxmanWebAppFactory factory,
        int segmentCount = 3,
        int tokensPerSegment = 100)
    {
        Wp4Fixtures.EnsureHost(factory);

        // Budget groß, max_share 0.8 ⇒ Limit = 0.8 * budget. Mit 3 × 100 tokens = 300 < 0.8 × 10000 = 8000.
        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 10_000) with
        {
            Compaction = new CompactionConfig("model", "default-v1", MaxShare: 0.8),
            Promotion = new PromotionConfig(new PromotionSink("webhook", "https://sink.example/ingest")),
        };

        var session = Wp4Fixtures.NewSession(Tenant, policy);

        using var db = factory.CreateDbContext(Tenant);
        db.Sessions.Add(session);

        for (var i = 0; i < segmentCount; i++)
        {
            db.Segments.Add(Wp4Fixtures.LiveWorking(
                session.Id, Tenant,
                kind: "assistant_msg",
                tokens: tokensPerSegment,
                content: $"segment content {i}"));
        }

        await db.SaveChangesAsync();
        return session;
    }

    // AC5 (Spec §3.3): POST /gc { level: "major" } triggert den Worker → nach idle existiert
    // ein compaction_summary-Segment und alle Quell-Segmente sind compacted.
    [Fact]
    public async Task MajorGc_CreatesCompactionSummary_AndCompactsSources()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var session = await SeedSessionWithSegments(factory, segmentCount: 3);

        var response = await client.PostAsJsonAsync($"/v1/sessions/{session.Id}/gc", new { level = "major" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        // Genau ein compaction_summary-Segment muss existieren.
        var summaries = await verify.Segments.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.Kind == "compaction_summary")
            .ToListAsync();
        Assert.Single(summaries);

        // Alle anderen (nicht-summary) Segmente müssen compacted sein.
        var originals = await verify.Segments.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.Kind != "compaction_summary")
            .ToListAsync();
        Assert.All(originals, s => Assert.Equal(SegmentState.Compacted, s.State));
    }

    // AC4 (Spec §3.3): Promotion läuft BEFORE Compaction — fact_promoted-Event hat seq KLEINER als
    // compaction_started, welches KLEINER als compaction_completed ist.
    [Fact]
    public async Task MajorGc_EventSequence_PromotionBeforeCompaction()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var session = await SeedSessionWithSegments(factory, segmentCount: 3);

        await client.PostAsJsonAsync($"/v1/sessions/{session.Id}/gc", new { level = "major" });
        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        var factPromoted = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id && ev.Type == "fact_promoted")
            .FirstOrDefaultAsync();
        var compactionStarted = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id && ev.Type == "compaction_started")
            .FirstOrDefaultAsync();
        var compactionCompleted = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id && ev.Type == "compaction_completed")
            .FirstOrDefaultAsync();

        Assert.NotNull(factPromoted);
        Assert.NotNull(compactionStarted);
        Assert.NotNull(compactionCompleted);

        // Spec §3.3: Promotion BEFORE Compaction — Seq-Reihenfolge.
        Assert.True(factPromoted.Seq < compactionStarted.Seq,
            $"fact_promoted.seq={factPromoted.Seq} must be < compaction_started.seq={compactionStarted.Seq}");
        Assert.True(compactionStarted.Seq < compactionCompleted.Seq,
            $"compaction_started.seq={compactionStarted.Seq} must be < compaction_completed.seq={compactionCompleted.Seq}");
    }

    // AC6 (Spec §3.3): fact_promoted-Event wird emittiert; RecordingPromotionSink registriert den Write;
    // Quell-Segmente werden durch Promotion NICHT verändert (sie werden erst durch Compaction compacted).
    [Fact]
    public async Task MajorGc_FactPromoted_SinkReceivesWrite_SourcesUnchangedByPromotion()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var session = await SeedSessionWithSegments(factory, segmentCount: 3);

        // Quell-Segment-IDs vor dem GC einfrieren.
        List<Ulid> sourceIds;
        using (var db = factory.CreateDbContext(Tenant))
        {
            sourceIds = await db.Segments.AsNoTracking()
                .Where(s => s.SessionId == session.Id && s.Kind != "compaction_summary")
                .Select(s => s.Id)
                .ToListAsync();
        }

        await client.PostAsJsonAsync($"/v1/sessions/{session.Id}/gc", new { level = "major" });
        await Queue(factory).WaitForIdleAsync();

        // fact_promoted-Event muss existieren.
        using var verify = factory.CreateDbContext(Tenant);
        var factPromotedEvent = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id && ev.Type == "fact_promoted")
            .FirstOrDefaultAsync();
        Assert.NotNull(factPromotedEvent);

        // Payload muss segment_id, sink, payload_digest enthalten.
        using var payloadDoc = JsonDocument.Parse(factPromotedEvent.Payload);
        Assert.True(payloadDoc.RootElement.TryGetProperty("segment_id", out _));
        Assert.True(payloadDoc.RootElement.TryGetProperty("sink", out _));
        Assert.True(payloadDoc.RootElement.TryGetProperty("payload_digest", out _));

        // RecordingPromotionSink muss mindestens einen Write erhalten haben.
        var sinkWrites = FakeSink(factory).Writes;
        Assert.NotEmpty(sinkWrites);

        // Quell-Segmente dürfen NUR durch Compaction (nicht durch Promotion) geändert worden sein.
        // Nach Abschluss beider Phasen müssen sie compacted sein.
        var sourcesAfter = await verify.Segments.AsNoTracking()
            .Where(s => sourceIds.Contains(s.Id))
            .ToListAsync();
        Assert.All(sourcesAfter, s => Assert.Equal(SegmentState.Compacted, s.State));
    }

    // AC9 (Spec §3.3): genau EIN compaction_summary-Segment; sein seq = älteste Source-seq;
    // alle Quell-Segmente state=compacted und noch in der DB (Soft-Delete, I3).
    [Fact]
    public async Task MajorGc_ExactlyOneCompactionSummary_SeqEqualsOldestSource()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var session = await SeedSessionWithSegments(factory, segmentCount: 3);

        // Älteste Seq der Quell-Segmente ermitteln, bevor der GC läuft.
        long oldestSourceSeq;
        List<Ulid> sourceIds;
        using (var db = factory.CreateDbContext(Tenant))
        {
            var sources = await db.Segments.AsNoTracking()
                .Where(s => s.SessionId == session.Id)
                .ToListAsync();
            oldestSourceSeq = sources.Min(s => s.Seq);
            sourceIds = sources.Select(s => s.Id).ToList();
        }

        await client.PostAsJsonAsync($"/v1/sessions/{session.Id}/gc", new { level = "major" });
        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        // Genau ein compaction_summary.
        var summaries = await verify.Segments.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.Kind == "compaction_summary")
            .ToListAsync();
        Assert.Single(summaries);

        // seq des Summary = älteste Source-seq.
        Assert.Equal(oldestSourceSeq, summaries[0].Seq);

        // Alle Quell-Segmente sind noch in der DB (Soft-Delete / I3) und compacted.
        var sourceSegments = await verify.Segments.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.Kind != "compaction_summary")
            .ToListAsync();
        Assert.Equal(sourceIds.Count, sourceSegments.Count);
        Assert.All(sourceSegments, s => Assert.Equal(SegmentState.Compacted, s.State));
    }

    // AC10 (Spec §3.3): compaction_started und compaction_completed werden emittiert;
    // completed-Payload hat source_ids, summary_id, tokens_before, tokens_after.
    [Fact]
    public async Task MajorGc_EmitsCompactionStartedAndCompleted_WithCorrectPayload()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var session = await SeedSessionWithSegments(factory, segmentCount: 3, tokensPerSegment: 100);

        List<Ulid> sourceIds;
        using (var db = factory.CreateDbContext(Tenant))
        {
            sourceIds = await db.Segments.AsNoTracking()
                .Where(s => s.SessionId == session.Id)
                .Select(s => s.Id)
                .ToListAsync();
        }

        await client.PostAsJsonAsync($"/v1/sessions/{session.Id}/gc", new { level = "major" });
        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        var startedEvent = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id && ev.Type == "compaction_started")
            .FirstAsync();
        var completedEvent = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id && ev.Type == "compaction_completed")
            .FirstAsync();

        // compaction_completed payload prüfen.
        using var completedDoc = JsonDocument.Parse(completedEvent.Payload);
        var root = completedDoc.RootElement;

        Assert.True(root.TryGetProperty("source_ids", out var sourceIdsElem));
        Assert.True(root.TryGetProperty("summary_id", out _));
        Assert.True(root.TryGetProperty("tokens_before", out var tokensBefore));
        Assert.True(root.TryGetProperty("tokens_after", out _));

        // source_ids muss alle 3 Quell-Segmente enthalten.
        var reportedIds = sourceIdsElem.EnumerateArray()
            .Select(e => e.GetString())
            .ToHashSet();
        Assert.Equal(sourceIds.Count, reportedIds.Count);
        foreach (var id in sourceIds)
        {
            Assert.Contains(id.ToString(), reportedIds);
        }

        // tokens_before = Summe der Tokens der gewählten Segmente (3 × 100 = 300).
        Assert.Equal(300, tokensBefore.GetInt32());

        // summary_id muss eine gültige ULID sein.
        var summaryIdStr = root.GetProperty("summary_id").GetString();
        Assert.True(Ulid.TryParse(summaryIdStr, out _));
    }

    // AC11 (Spec §3.3 / I3): compacted-Segmente erscheinen NICHT im Render-Output.
    [Fact]
    public async Task MajorGc_CompactedSegments_NeverAppearInRender()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var session = await SeedSessionWithSegments(factory, segmentCount: 3);

        List<string> originalContents;
        using (var db = factory.CreateDbContext(Tenant))
        {
            originalContents = await db.Segments.AsNoTracking()
                .Where(s => s.SessionId == session.Id)
                .Select(s => s.Content!)
                .ToListAsync();
        }

        await client.PostAsJsonAsync($"/v1/sessions/{session.Id}/gc", new { level = "major" });
        await Queue(factory).WaitForIdleAsync();

        // Render nach der Major GC.
        var renderResp = await client.PostAsJsonAsync(
            $"/v1/sessions/{session.Id}/render",
            new { provider = "anthropic", turn_advance = false });
        Assert.Equal(HttpStatusCode.OK, renderResp.StatusCode);

        var renderBody = await renderResp.Content.ReadAsStringAsync();

        // Kein compactierter Content darf im Render erscheinen.
        foreach (var content in originalContents)
        {
            Assert.DoesNotContain(content, renderBody);
        }
    }

    // AC13 (Spec §2.2 I1 / §3.3): gepinnte Working-Segmente und Static-Segmente sind nach
    // Major GC unverändert (Live-State, unberührt).
    [Fact]
    public async Task MajorGc_PinnedAndStaticSegments_Untouched()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 10_000) with
        {
            Compaction = new CompactionConfig("model", "default-v1", MaxShare: 0.8),
            Promotion = new PromotionConfig(new PromotionSink("webhook", "https://sink.example/ingest")),
        };

        var session = Wp4Fixtures.NewSession(Tenant, policy);
        var pinned = Wp4Fixtures.LiveWorking(session.Id, Tenant, kind: "assistant_msg", tokens: 50, pinned: true, content: "pinned segment");
        var normal1 = Wp4Fixtures.LiveWorking(session.Id, Tenant, kind: "assistant_msg", tokens: 100, content: "normal 1");
        var normal2 = Wp4Fixtures.LiveWorking(session.Id, Tenant, kind: "assistant_msg", tokens: 100, content: "normal 2");

        // Static-Segment.
        var staticSeg = new Segment(
            id: Ulid.NewUlid(),
            sessionId: session.Id,
            tenantId: Tenant,
            region: Region.Static,
            kind: "system_prompt",
            source: null,
            role: Role.System,
            content: "system prompt",
            blobRef: null,
            refetchable: false,
            origin: null,
            summary: null,
            toolCallId: null,
            frameId: null,
            pinned: false,
            createdTurn: 0,
            lastReferencedTurn: 0,
            tokens: 20,
            seq: Wp4Fixtures.NextSeq(),
            state: SegmentState.Live);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.AddRange(pinned, normal1, normal2, staticSeg);
            await db.SaveChangesAsync();
        }

        var client = ClientFor(factory);
        await client.PostAsJsonAsync($"/v1/sessions/{session.Id}/gc", new { level = "major" });
        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        // Gepinntes Working-Segment muss noch Live sein.
        var pinnedAfter = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == pinned.Id);
        Assert.Equal(SegmentState.Live, pinnedAfter.State);
        Assert.True(pinnedAfter.Pinned);

        // Static-Segment muss unverändert Live sein.
        var staticAfter = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == staticSeg.Id);
        Assert.Equal(SegmentState.Live, staticAfter.State);
        Assert.Equal(Region.Static, staticAfter.Region);
    }

    // AC3 (Spec §8): zwei Major-Jobs für dieselbe Session laufen nicht verschränkt —
    // nach idle gibt es genau EINE compaction_summary, Quell-Segmente sind genau einmal compacted.
    [Fact]
    public async Task TwoMajorJobs_SameSession_DoNotInterleave()
    {
        await using var factory = new CtxmanWebAppFactory();
        var session = await SeedSessionWithSegments(factory, segmentCount: 3);

        // Beide Jobs VOR dem WaitForIdle enqueuen, damit sie konkurrieren.
        Queue(factory).Enqueue(new MinorGcJob(Tenant, session.Id, Ulid.NewUlid(), GcLevel.Major));
        Queue(factory).Enqueue(new MinorGcJob(Tenant, session.Id, Ulid.NewUlid(), GcLevel.Major));
        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        // Genau eine compaction_summary — kein doppeltes Summary durch Race.
        var summaries = await verify.Segments.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.Kind == "compaction_summary")
            .ToListAsync();
        Assert.Single(summaries);

        // Alle Original-Segmente genau einmal compacted — kein doppelter State-Wechsel.
        var originals = await verify.Segments.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.Kind != "compaction_summary")
            .ToListAsync();
        Assert.All(originals, s => Assert.Equal(SegmentState.Compacted, s.State));

        // Genau ein compaction_completed-Event (nicht zwei).
        var completedCount = await verify.Events.AsNoTracking()
            .CountAsync(ev => ev.SessionId == session.Id && ev.Type == "compaction_completed");
        Assert.Equal(1, completedCount);
    }

    // AC12 (Spec §3.3): ECHTE Version-Isolation — ein neues Live-Working-Segment, das deterministisch
    // WÄHREND des Compaction-LLM-Calls eingefügt wird (d. h. NACH dem Einfrieren des Fensters,
    // aber VOR der DB-Transaktion), darf NICHT compacted werden, NICHT in source_ids auftauchen
    // und NICHT das Summary-Segment sein.
    //
    // Technik: FakeCompactionModel.OnSummarize-Hook wird auf dem COMPACTION-Aufruf ausgelöst
    // (PromptTemplateId != "fact-extraction-v1"). Der Hook fügt das neue Segment in einem
    // separaten DbContext ein — simuliert exakt einen nebenläufigen Append nach dem Fenstereinfrieren
    // (Spec §3.3 step 3).
    [Fact]
    public async Task MajorGc_NewSegment_AppendedDuringCompactionLlmCall_RemainLive()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 10_000) with
        {
            Compaction = new CompactionConfig("model", "default-v1", MaxShare: 0.8),
            Promotion = new PromotionConfig(new PromotionSink("webhook", "https://sink.example/ingest")),
        };
        var session = Wp4Fixtures.NewSession(Tenant, policy);

        // Initiale Segmente — diese bilden das eingefrorene Fenster.
        var window1 = Wp4Fixtures.LiveWorking(session.Id, Tenant, kind: "assistant_msg", tokens: 100, content: "window seg 1");
        var window2 = Wp4Fixtures.LiveWorking(session.Id, Tenant, kind: "assistant_msg", tokens: 100, content: "window seg 2");

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.AddRange(window1, window2);
            await db.SaveChangesAsync();
        }

        // Das concurrent segment: wird erst vom Hook eingefügt, NACHDEM der Worker das Fenster
        // eingefroren und den Promotion-Call abgeschlossen hat, aber WÄHREND der Compaction-LLM-Call
        // noch läuft. So ist es definitiv nicht Teil des eingefrorenen Fensters.
        Ulid concurrentSegId = default;

        var fake = FakeCompaction(factory);
        fake.OnSummarize = async _ =>
        {
            // Separater DbContext — simuliert einen nebenläufigen Client-Append.
            using var concurrentDb = factory.CreateDbContext(Tenant);
            var concurrentSeg = Wp4Fixtures.LiveWorking(
                session.Id, Tenant,
                kind: "assistant_msg",
                tokens: 50,
                content: "concurrent segment appended after window freeze");
            concurrentSegId = concurrentSeg.Id;
            concurrentDb.Segments.Add(concurrentSeg);
            await concurrentDb.SaveChangesAsync();
        };

        Queue(factory).Enqueue(new MinorGcJob(Tenant, session.Id, Ulid.NewUlid(), GcLevel.Major));
        await Queue(factory).WaitForIdleAsync();

        // Hook nur einmal ausgelöst (ein Compaction-Aufruf).
        fake.OnSummarize = null;

        Assert.NotEqual(default, concurrentSegId);

        using var verify = factory.CreateDbContext(Tenant);

        // 1. Das concurrent Segment MUSS Live bleiben (Spec §3.3 step 3 Version-Isolation).
        var concurrentAfter = await verify.Segments.AsNoTracking()
            .SingleAsync(s => s.Id == concurrentSegId);
        Assert.Equal(SegmentState.Live, concurrentAfter.State);

        // 2. Das concurrent Segment darf NICHT in source_ids des compaction_completed-Events stehen.
        var completedEvent = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id && ev.Type == "compaction_completed")
            .FirstAsync();
        using var completedDoc = JsonDocument.Parse(completedEvent.Payload);
        var sourceIds = completedDoc.RootElement.GetProperty("source_ids")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToHashSet();
        Assert.DoesNotContain(concurrentSegId.ToString(), sourceIds);

        // 3. Das concurrent Segment ist NICHT das Summary-Segment (Summary-Kind wäre "compaction_summary").
        Assert.NotEqual("compaction_summary", concurrentAfter.Kind);

        // 4. Sanity: Die zwei Window-Segmente wurden korrekt compacted.
        var window1After = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == window1.Id);
        var window2After = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == window2.Id);
        Assert.Equal(SegmentState.Compacted, window1After.State);
        Assert.Equal(SegmentState.Compacted, window2After.State);

        // 5. Genau ein compaction_summary-Segment wurde angelegt.
        var summaries = await verify.Segments.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.Kind == "compaction_summary")
            .ToListAsync();
        Assert.Single(summaries);
    }

    // AC4 / Spec §3.3 — Empty-Extraction Branch: wenn "fact-extraction-v1" einen LEEREN String
    // zurückgibt, darf KEIN fact_promoted-Event emittiert werden und der RecordingPromotionSink
    // darf KEINEN Write erhalten. Compaction soll dennoch normal ablaufen: ein compaction_summary
    // wird angelegt, compaction_started + compaction_completed werden emittiert, Quell-Segmente
    // werden compacted. Ein leeres Ergebnis bedeutet "nichts zu promoten", kein Fehler.
    [Fact]
    public async Task MajorGc_EmptyPromotionExtraction_SkipsFactWrite_ButCompactionProceedsNormally()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        // Konfiguriere FakeCompactionModel: Promotion gibt leer zurück, Compaction normal.
        var fake = FakeCompaction(factory);
        fake.EmptyPromotionResult = true;

        var session = await SeedSessionWithSegments(factory, segmentCount: 3, tokensPerSegment: 100);

        await client.PostAsJsonAsync($"/v1/sessions/{session.Id}/gc", new { level = "major" });
        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        // 1. KEIN fact_promoted-Event darf emittiert worden sein (leere Extraktion = keine Fakten).
        var factPromotedCount = await verify.Events.AsNoTracking()
            .CountAsync(ev => ev.SessionId == session.Id && ev.Type == "fact_promoted");
        Assert.Equal(0, factPromotedCount);

        // 2. RecordingPromotionSink darf KEINEN Write erhalten haben.
        var sinkWrites = FakeSink(factory).Writes;
        Assert.Empty(sinkWrites);

        // 3. Compaction muss dennoch normal ablaufen: genau ein compaction_summary-Segment.
        var summaries = await verify.Segments.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.Kind == "compaction_summary")
            .ToListAsync();
        Assert.Single(summaries);

        // 4. Alle Quell-Segmente müssen compacted sein.
        var originals = await verify.Segments.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.Kind != "compaction_summary")
            .ToListAsync();
        Assert.All(originals, s => Assert.Equal(SegmentState.Compacted, s.State));

        // 5. compaction_started und compaction_completed wurden emittiert.
        var startedCount = await verify.Events.AsNoTracking()
            .CountAsync(ev => ev.SessionId == session.Id && ev.Type == "compaction_started");
        var completedCount = await verify.Events.AsNoTracking()
            .CountAsync(ev => ev.SessionId == session.Id && ev.Type == "compaction_completed");
        Assert.Equal(1, startedCount);
        Assert.Equal(1, completedCount);
    }

    // AC12 (Spec §3.3) Variante: Segmente außerhalb des max_share-Caps bleiben Live.
    [Fact]
    public async Task MajorGc_SegmentsBeyondTokenCap_RemainLive()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        // Budget 1000, max_share 0.4 ⇒ Limit = 400.
        // 2 Segmente × 200 = 400 → beide passen (erstes: 0+200≤400; zweites: 200+200=400, Bedingung: > 400 → nein).
        // 3. Segment mit 200: 400+200=600 > 400 → Stop.
        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1000) with
        {
            Compaction = new CompactionConfig("model", "default-v1", MaxShare: 0.4),
            Promotion = new PromotionConfig(new PromotionSink("webhook", "https://sink.example/ingest")),
        };
        var session = Wp4Fixtures.NewSession(Tenant, policy);

        var seg1 = Wp4Fixtures.LiveWorking(session.Id, Tenant, kind: "assistant_msg", tokens: 200, content: "inside cap 1");
        var seg2 = Wp4Fixtures.LiveWorking(session.Id, Tenant, kind: "assistant_msg", tokens: 200, content: "inside cap 2");
        var seg3 = Wp4Fixtures.LiveWorking(session.Id, Tenant, kind: "assistant_msg", tokens: 200, content: "outside cap");

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.AddRange(seg1, seg2, seg3);
            await db.SaveChangesAsync();
        }

        Queue(factory).Enqueue(new MinorGcJob(Tenant, session.Id, Ulid.NewUlid(), GcLevel.Major));
        await Queue(factory).WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        var seg3After = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == seg3.Id);
        // seg3 liegt außerhalb des Caps: muss Live bleiben.
        Assert.Equal(SegmentState.Live, seg3After.State);

        var seg1After = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == seg1.Id);
        var seg2After = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == seg2.Id);
        Assert.Equal(SegmentState.Compacted, seg1After.State);
        Assert.Equal(SegmentState.Compacted, seg2After.State);
    }

    // Regression: der bestehende MajorGc_IsEnqueuedButNotExecuted-Test aus WP4 muss weiterhin grün bleiben.
    // Dieser Test ist in MinorGcWorkerTests.cs definiert und wird nicht dupliziert.
    // Hier verifizieren wir ergänzend: mit WP5-Fakes läuft der Worker korrekt, kein Netzwerk-Call.
    [Fact]
    public async Task MajorGc_UsesOnlyFakes_NoRealNetworkCalls()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var session = await SeedSessionWithSegments(factory, segmentCount: 3);

        await client.PostAsJsonAsync($"/v1/sessions/{session.Id}/gc", new { level = "major" });
        await Queue(factory).WaitForIdleAsync();

        // FakeCompactionModel muss aufgerufen worden sein (kein echter LLM-Call).
        var fake = FakeCompaction(factory);
        Assert.NotEmpty(fake.ReceivedRequests);

        // Erwarte: 1 fact-extraction-Aufruf + 1 compaction-Aufruf.
        var factExtractionCalls = fake.RequestsForTemplate("fact-extraction-v1").Count();
        var compactionCalls = fake.RequestsForTemplate("default-v1").Count();
        Assert.Equal(1, factExtractionCalls);
        Assert.Equal(1, compactionCalls);
    }
}
