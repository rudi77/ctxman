using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Api.Gc;
using Ctxman.Core.Domain;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ctxman.Tests.Watermarks;

/// <summary>
/// AC6–AC9 (Spec §3.1): Watermark-Aktionen im Render-Pfad. Budgets/Token-Mengen werden direkt
/// geseedet, damit die Schwellen 0.60/0.80/0.95·B exakt gekreuzt werden. soft/hard reihen einen
/// async GC-Job ein (Minor- bzw. Major-Marker); emergency läuft synchron I/O-frei (nur Clean-Page +
/// TTL-Eviction, KEIN Blob-Write); bleibt der Verbrauch danach ≥ Budget ⇒ 413 ohne Turn-Advance.
/// </summary>
public sealed class WatermarkTests
{
    private const string Tenant = "tenant-watermark";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    private static HttpRequestMessage Render(Ulid sid, string? idempotencyKey = null) =>
        BuildRender(sid, idempotencyKey, turnAdvance: false);

    private static HttpRequestMessage BuildRender(Ulid sid, string? idempotencyKey, bool turnAdvance)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/render")
        {
            Content = JsonContent.Create(new { provider = "anthropic", turn_advance = turnAdvance }),
        };
        if (idempotencyKey is not null)
        {
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return req;
    }

    // AC6 (Spec §3.1): Verbrauch über soft (0.60·B), unter hard ⇒ watermark_state "soft",
    // watermark_crossed { level: "soft" } im Outbox, und ein eingereihter Minor-Job, dessen Effekt
    // (Clean-Page-Eviction des TTL-abgelaufenen refetchable Segments) nach WaitForIdle sichtbar ist.
    [Fact]
    public async Task Soft_EnqueuesMinor_AndEmitsSoftEvent()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        // Budget 1000; 700 Tokens ⇒ ratio 0.70 ⇒ soft. currentTurn 20 > ttl(skill_content)=8 ⇒
        // der refetchable skill ist clean-page-evictable, sobald der Minor-Job läuft.
        var session = Wp4Fixtures.NewSession(
            Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1000), currentTurn: 20);
        var skill = Wp4Fixtures.LiveWorking(
            session.Id, Tenant, kind: "skill_content", tokens: 700, refetchable: true,
            origin: "skill://foo", role: Role.User, lastReferencedTurn: 0);
        var skillId = skill.Id;

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(skill);
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Render(session.Id));
        response.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("soft", doc.RootElement.GetProperty("watermark_state").GetString());
        }

        await factory.Services.GetRequiredService<IGcJobQueue>().WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        var soft = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id && ev.Type == "watermark_crossed")
            .ToListAsync();
        var softEvent = Assert.Single(soft);
        using (var payload = JsonDocument.Parse(softEvent.Payload))
        {
            Assert.Equal("soft", payload.RootElement.GetProperty("level").GetString());
        }

        // Der eingereihte Minor-Job lief: das TTL-abgelaufene refetchable Segment ist evicted.
        var evicted = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == skillId);
        Assert.Equal(SegmentState.Evicted, evicted.State);
        var segmentEvicted = await verify.Events.AsNoTracking()
            .CountAsync(ev => ev.SessionId == session.Id && ev.Type == "segment_evicted");
        Assert.Equal(1, segmentEvicted);
    }

    // AC7 (Spec §3.1 / §3.3): Verbrauch über hard (0.80·B), unter emergency ⇒ watermark_state "hard",
    // watermark_crossed { level: "hard" } im Outbox; der hard-Marker führt KEINE Externalisierung/
    // Eviction aus (Major-Execution ist WP5).
    [Fact]
    public async Task Hard_EnqueuesMajorMarker_AndExecutesNothing()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        // Budget 1000; 850 Tokens ⇒ ratio 0.85 ⇒ hard. Das tool_result wäre externalisierbar,
        // darf vom hard-Marker aber NICHT angefasst werden (Major = WP5).
        var session = Wp4Fixtures.NewSession(
            Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1000, externalizeThresholdTokens: 100));
        var big = Wp4Fixtures.LiveWorking(
            session.Id, Tenant, kind: "tool_result", tokens: 850,
            content: new string('x', 12_000), role: Role.Tool);
        var bigId = big.Id;

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(big);
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Render(session.Id));
        response.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("hard", doc.RootElement.GetProperty("watermark_state").GetString());
        }

        await factory.Services.GetRequiredService<IGcJobQueue>().WaitForIdleAsync();

        using var verify = factory.CreateDbContext(Tenant);

        var crossed = await verify.Events.AsNoTracking()
            .Where(ev => ev.SessionId == session.Id && ev.Type == "watermark_crossed")
            .ToListAsync();
        var hardEvent = Assert.Single(crossed);
        using (var payload = JsonDocument.Parse(hardEvent.Payload))
        {
            Assert.Equal("hard", payload.RootElement.GetProperty("level").GetString());
        }

        // Kein Externalisieren/Eviktieren durch den Major-Marker (WP5).
        var persisted = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == bigId);
        Assert.Equal(SegmentState.Live, persisted.State);
        var mutations = await verify.Events.AsNoTracking()
            .CountAsync(ev => ev.SessionId == session.Id
                && (ev.Type == "segment_externalized" || ev.Type == "segment_evicted" || ev.Type == "unit_evicted"));
        Assert.Equal(0, mutations);
    }

    // AC8 (Spec §3.1): Verbrauch über emergency (0.95·B) mit evictierbaren (TTL-abgelaufenen
    // refetchable) Segmenten ⇒ render evictiert SYNCHRON über Clean-Page/TTL, OHNE Blob-Write
    // (kein externalisiertes Segment), und emittiert watermark_crossed { level: "emergency" } +
    // segment_evicted/unit_evicted.
    [Fact]
    public async Task Emergency_SynchronousEvict_NoBlobWrite_EmitsEvents()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        // Budget 1000; currentTurn 20. 980 evictable Tokens (refetchable skill, TTL abgelaufen) +
        // 80 nicht-evictable (user_msg, TTL 40, nicht refetchable) ⇒ Summe 1060 ⇒ ratio 1.06 ⇒
        // emergency; nach Eviction der 980 bleiben 80 (< Budget) ⇒ kein 413, Turn advanced nicht
        // (turn_advance=false), aber Render liefert 200.
        var session = Wp4Fixtures.NewSession(
            Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1000), currentTurn: 20);
        var evictable = Wp4Fixtures.LiveWorking(
            session.Id, Tenant, kind: "skill_content", tokens: 980, refetchable: true,
            origin: "skill://big", role: Role.User, lastReferencedTurn: 0);
        var keep = Wp4Fixtures.LiveWorking(
            session.Id, Tenant, kind: "user_msg", tokens: 80, role: Role.User, lastReferencedTurn: 20);
        var evictableId = evictable.Id;

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.AddRange(evictable, keep);
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Render(session.Id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("emergency", doc.RootElement.GetProperty("watermark_state").GetString());
        }

        using var verify = factory.CreateDbContext(Tenant);

        // Synchrone Clean-Page-Eviction des refetchable Segments.
        var persisted = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == evictableId);
        Assert.Equal(SegmentState.Evicted, persisted.State);

        // KEIN Blob-Write / keine Externalisierung im Notfallpfad.
        var anyExternalized = await verify.Segments.AsNoTracking()
            .AnyAsync(s => s.SessionId == session.Id && s.State == SegmentState.Externalized);
        Assert.False(anyExternalized);
        var externalizedEvents = await verify.Events.AsNoTracking()
            .CountAsync(ev => ev.SessionId == session.Id && ev.Type == "segment_externalized");
        Assert.Equal(0, externalizedEvents);

        // watermark_crossed { emergency } + mindestens ein segment_evicted/unit_evicted.
        var emergency = await verify.Events.AsNoTracking()
            .SingleAsync(ev => ev.SessionId == session.Id && ev.Type == "watermark_crossed");
        using (var payload = JsonDocument.Parse(emergency.Payload))
        {
            Assert.Equal("emergency", payload.RootElement.GetProperty("level").GetString());
        }
        var evictionEvents = await verify.Events.AsNoTracking()
            .CountAsync(ev => ev.SessionId == session.Id
                && (ev.Type == "segment_evicted" || ev.Type == "unit_evicted"));
        Assert.True(evictionEvents >= 1);
    }

    // AC9 (Spec §3.1): bleiben die lebenden Segmente auch nach der Notfall-Eviction ≥ Budget
    // (hier: ausschließlich nicht-evictierbare pinned/∞-TTL-Segmente) ⇒ 413 mit retrybarem Body,
    // KEIN Turn-Advance / kein Versions-Bump.
    [Fact]
    public async Task Emergency_StillOverBudget_Returns413_NoTurnAdvance()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        // Budget 1000; ein gepinntes (nicht evictierbares) Segment mit 1200 Tokens ⇒ ratio 1.2 ⇒
        // emergency; nichts ist evictierbar ⇒ Verbrauch bleibt 1200 ≥ Budget ⇒ 413.
        var session = Wp4Fixtures.NewSession(
            Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1000), currentTurn: 0);
        var pinned = Wp4Fixtures.LiveWorking(
            session.Id, Tenant, kind: "user_msg", tokens: 1200, pinned: true, role: Role.User);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(pinned);
            await db.SaveChangesAsync();
        }

        // turn_advance=true mit Key: nur so beweist das 413, dass der Turn-Advance, der sonst
        // passieren WÜRDE, unterbleibt (Spec §3.1).
        var response = await client.SendAsync(
            BuildRender(session.Id, idempotencyKey: "emergency-413", turnAdvance: true));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("retryable").GetBoolean());
        }

        // Kein Turn-Advance, kein Versions-Bump (Body bestätigt: nichts committet).
        using var verify = factory.CreateDbContext(Tenant);
        var persisted = await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.Equal(0, persisted.CurrentTurn);
        Assert.Equal(1, persisted.ContextVersion);
    }
}
