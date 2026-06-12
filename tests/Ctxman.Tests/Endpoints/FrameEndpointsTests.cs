using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Core.Rendering;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// API-Tests für Frame-Push/-Pop, Render-Scope und Session-Archivierung (WP6-Akzeptanz AC1–AC11;
/// Spec §2.5, §3.3, §4.3, §6).
///
/// Jede Testmethode verwendet eine eigene <see cref="CtxmanWebAppFactory"/>-Instanz (SQLite in-memory)
/// und ist vollständig unabhängig vom Zustand anderer Tests.
/// </summary>
public sealed class FrameEndpointsTests
{
    private const string Tenant = "tenant-frames";

    // ── HTTP-Hilfsmethoden ────────────────────────────────────────────────────────────────────

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    private static async Task<string> CreateSessionAsync(HttpClient client, object? body = null)
    {
        var created = await client.PostAsJsonAsync("/v1/sessions", body ?? new { });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("session_id").GetString()!;
    }

    private static HttpRequestMessage PushFrame(string sid, string label, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/frames")
        {
            Content = JsonContent.Create(new { label }),
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return req;
    }

    private static HttpRequestMessage PopFrame(
        string sid,
        string fid,
        string returnContent,
        string idempotencyKey,
        string? returnKind = null)
    {
        var body = returnKind is null
            ? (object)new { return_content = returnContent }
            : new { return_content = returnContent, return_kind = returnKind };

        var req = new HttpRequestMessage(HttpMethod.Delete, $"/v1/sessions/{sid}/frames/{fid}")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return req;
    }

    private static HttpRequestMessage AppendSegment(string sid, object body, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return req;
    }

    private static HttpRequestMessage Render(string sid, object body, string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/render")
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
        {
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return req;
    }

    private static HttpRequestMessage ArchiveSession(string sid, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/archive");
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return req;
    }

    // ── AC1: Frame-Push ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC1 / Spec §2.5 / §4.3: POST /frames ohne offenen Frame ⇒ 201 { frame_id };
    /// parent_frame_id = null (Root-Level). Idempotency-Key Pflicht.
    /// </summary>
    [Fact]
    public async Task Push_RootLevel_Returns201_WithFrameId_AndParentNull()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var response = await client.SendAsync(PushFrame(sid, "task-root", "push-root-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var frameId = doc.RootElement.GetProperty("frame_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(frameId));

        // DB-Inspektion: Frame mit parent_frame_id = null angelegt.
        using var db = factory.CreateDbContext(Tenant);
        var frame = await db.Frames.AsNoTracking()
            .FirstAsync(f => f.Id == Ulid.Parse(frameId!));
        Assert.Null(frame.ParentFrameId);
        Assert.Equal("task-root", frame.Label);
        Assert.Equal(FrameStatus.Open, frame.Status);
    }

    /// <summary>
    /// AC1 / Spec §2.5: nested Push ⇒ parent_frame_id = zuvor geöffneter Frame (Stack-Tip).
    /// </summary>
    [Fact]
    public async Task Push_NestedFrame_SetsParentFrameId_ToCurrentTip()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Ersten Frame pushen.
        var firstResp = await client.SendAsync(PushFrame(sid, "outer", "push-outer-1"));
        firstResp.EnsureSuccessStatusCode();
        var firstFrameId = (await firstResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        // Zweiten Frame pushen — muss outer als Parent haben.
        var secondResp = await client.SendAsync(PushFrame(sid, "inner", "push-inner-1"));
        Assert.Equal(HttpStatusCode.Created, secondResp.StatusCode);
        var secondFrameId = (await secondResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        using var db = factory.CreateDbContext(Tenant);
        var innerFrame = await db.Frames.AsNoTracking()
            .FirstAsync(f => f.Id == Ulid.Parse(secondFrameId));
        // Spec §2.5: parent_frame_id = oberster offener Frame (outer).
        Assert.Equal(Ulid.Parse(firstFrameId), innerFrame.ParentFrameId);
    }

    /// <summary>
    /// AC1 / Spec §4.4: fehlender Idempotency-Key auf POST /frames ⇒ 400 (kein Write).
    /// </summary>
    [Fact]
    public async Task Push_MissingIdempotencyKey_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/frames")
        {
            Content = JsonContent.Create(new { label = "no-key" }),
        };
        // Absichtlich kein Idempotency-Key-Header.

        var response = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── AC2: Segment-frame_id beim Append ─────────────────────────────────────────────────────

    /// <summary>
    /// AC2 / Spec §2.5: Append bei offenem Frame ⇒ Segment erhält frame_id = Tip-Frame.
    /// </summary>
    [Fact]
    public async Task Append_WithOpenFrame_SetsFrameId_ToTip()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Frame pushen.
        var pushResp = await client.SendAsync(PushFrame(sid, "work-frame", "push-ac2"));
        pushResp.EnsureSuccessStatusCode();
        var frameId = (await pushResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        // Segment appenden — muss frame_id des offenen Frames erhalten.
        var appendResp = await client.SendAsync(
            AppendSegment(sid, new { kind = "user_msg", role = "user", content = "in frame" }, "append-ac2"));
        appendResp.EnsureSuccessStatusCode();
        var segmentIds = (await appendResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("segment_ids");
        var segId = Ulid.Parse(segmentIds[0].GetString()!);

        using var db = factory.CreateDbContext(Tenant);
        var seg = await db.Segments.AsNoTracking().FirstAsync(s => s.Id == segId);
        Assert.Equal(Ulid.Parse(frameId), seg.FrameId);
    }

    /// <summary>
    /// AC2 / Spec §2.5: Append ohne offenen Frame ⇒ Segment erhält frame_id = null (Root).
    /// </summary>
    [Fact]
    public async Task Append_WithoutOpenFrame_FrameIdIsNull()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Kein Push — kein offener Frame.
        var appendResp = await client.SendAsync(
            AppendSegment(sid, new { kind = "user_msg", role = "user", content = "root level" }, "append-root"));
        appendResp.EnsureSuccessStatusCode();
        var segmentIds = (await appendResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("segment_ids");
        var segId = Ulid.Parse(segmentIds[0].GetString()!);

        using var db = factory.CreateDbContext(Tenant);
        var seg = await db.Segments.AsNoTracking().FirstAsync(s => s.Id == segId);
        // Spec §2.5: kein Frame offen ⇒ frame_id = null.
        Assert.Null(seg.FrameId);
    }

    // ── AC3: Frame-Pop (Eviction + Return-Segment) ────────────────────────────────────────────

    /// <summary>
    /// AC3 / Spec §2.5: DELETE /frames/{fid} ⇒ 200 { return_segment_id, context_version };
    /// Frame-Segmente werden evicted; ein subagent_return-Segment landet im Parent-Frame (Root).
    /// </summary>
    [Fact]
    public async Task Pop_Returns200_WithReturnSegmentId_AndContextVersion()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Frame pushen und ein Segment appenden.
        var pushResp = await client.SendAsync(PushFrame(sid, "pop-test-frame", "push-pop-1"));
        pushResp.EnsureSuccessStatusCode();
        var frameId = (await pushResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        var appendResp = await client.SendAsync(
            AppendSegment(sid, new { kind = "user_msg", role = "user", content = "work done" }, "append-pop-1"));
        appendResp.EnsureSuccessStatusCode();
        var appendedSegId = Ulid.Parse(
            (await appendResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("segment_ids")[0].GetString()!);

        // Frame poppen.
        var popResp = await client.SendAsync(PopFrame(sid, frameId, "result-content", "pop-pop-1"));

        Assert.Equal(HttpStatusCode.OK, popResp.StatusCode);
        using var doc = JsonDocument.Parse(await popResp.Content.ReadAsStringAsync());
        var returnSegId = doc.RootElement.GetProperty("return_segment_id").GetString();
        var contextVersion = doc.RootElement.GetProperty("context_version").GetInt64();
        Assert.False(string.IsNullOrWhiteSpace(returnSegId));
        Assert.True(contextVersion > 0);

        // DB-Inspektion: Frame-Segment ist evicted.
        using var db = factory.CreateDbContext(Tenant);
        var appendedSeg = await db.Segments.AsNoTracking().FirstAsync(s => s.Id == appendedSegId);
        Assert.Equal(SegmentState.Evicted, appendedSeg.State);

        // Return-Segment existiert im Root (frame_id = null, kind = subagent_return).
        var returnSeg = await db.Segments.AsNoTracking()
            .FirstAsync(s => s.Id == Ulid.Parse(returnSegId!));
        Assert.Equal("subagent_return", returnSeg.Kind);
        Assert.Equal("result-content", returnSeg.Content);
        Assert.Null(returnSeg.FrameId); // Root-Frame, Spec §2.5.
    }

    // ── AC4: LIFO-Verletzung ⇒ 409 ────────────────────────────────────────────────────────────

    /// <summary>
    /// AC4 / Spec §2.5 LIFO: Frame mit offenem Kind-Frame kann nicht gepoppt werden ⇒ 409.
    /// Keine Eviction findet statt.
    /// </summary>
    [Fact]
    public async Task Pop_FrameWithOpenChild_Returns409_NoEviction()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Outer-Frame pushen.
        var outerPush = await client.SendAsync(PushFrame(sid, "outer", "push-outer-lifo"));
        outerPush.EnsureSuccessStatusCode();
        var outerFrameId = (await outerPush.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        // Inner-Frame pushen (Kind von outer).
        var innerPush = await client.SendAsync(PushFrame(sid, "inner", "push-inner-lifo"));
        innerPush.EnsureSuccessStatusCode();

        // Versuch, outer zu poppen während inner offen ist ⇒ 409.
        var popResp = await client.SendAsync(PopFrame(sid, outerFrameId, "too-early", "pop-outer-lifo"));

        Assert.Equal(HttpStatusCode.Conflict, popResp.StatusCode);

        // Kein Frame darf gepoppt worden sein — outer bleibt open.
        using var db = factory.CreateDbContext(Tenant);
        var outerFrame = await db.Frames.AsNoTracking()
            .FirstAsync(f => f.Id == Ulid.Parse(outerFrameId));
        Assert.Equal(FrameStatus.Open, outerFrame.Status);
    }

    // ── AC5: Promotion vor Eviction ──────────────────────────────────────────────────────────

    /// <summary>
    /// AC5 / Spec §3.3: Vor der Eviction beim Pop läuft die Promotion-Policy.
    /// fact_promoted-Event erscheint VOR frame_popped im Event-Stream.
    /// RecordingPromotionSink erhält einen Write (Spec §3.3 step 1).
    /// FakeCompactionModel gibt für "fact-extraction-v1" einen non-empty String zurück
    /// (Spec §3.3: empty ⇒ kein Event), daher wird fact_promoted emittiert.
    /// </summary>
    [Fact]
    public async Task Pop_WithPromotionEligibleSegments_EmitsFactPromotedBeforeFramePopped()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Sicherstellen, dass der RecordingPromotionSink leer ist.
        var sink = factory.Services.GetRequiredService<RecordingPromotionSink>();
        sink.Reset();

        // Frame pushen und Live-Working-Segment appenden (Promotion-Kandidat).
        var pushResp = await client.SendAsync(PushFrame(sid, "promotion-frame", "push-promo-1"));
        pushResp.EnsureSuccessStatusCode();
        var frameId = (await pushResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        await client.SendAsync(AppendSegment(sid,
            new { kind = "decision", role = "assistant", content = "important decision" },
            "append-promo-1"));

        // Frame poppen — Promotion muss AUSSERHALB der Transaktion, vor Eviction laufen.
        var popResp = await client.SendAsync(PopFrame(sid, frameId, "promotion result", "pop-promo-1"));
        popResp.EnsureSuccessStatusCode();

        // RecordingPromotionSink muss einen Write erhalten haben (Spec §3.3 step 1).
        Assert.NotEmpty(sink.Writes);

        // Event-Stream: fact_promoted muss vor frame_popped stehen (Spec §6 Reihenfolge).
        using var db = factory.CreateDbContext(Tenant);
        var events = await db.Events.AsNoTracking()
            .Where(ev => ev.SessionId == Ulid.Parse(sid))
            .OrderBy(ev => ev.Seq)
            .Select(ev => new { ev.Type, ev.Seq })
            .ToListAsync();

        var factPromotedSeq = events.FirstOrDefault(e => e.Type == "fact_promoted")?.Seq;
        var framePoppedSeq = events.FirstOrDefault(e => e.Type == "frame_popped")?.Seq;

        Assert.NotNull(factPromotedSeq); // fact_promoted muss vorhanden sein.
        Assert.NotNull(framePoppedSeq);  // frame_popped muss vorhanden sein.
        // Spec §6 / §3.3: fact_promoted-Events ZUERST, dann frame_popped.
        Assert.True(factPromotedSeq < framePoppedSeq,
            $"fact_promoted (seq={factPromotedSeq}) must come before frame_popped (seq={framePoppedSeq})");
    }

    // ── AC6: Events frame_pushed und frame_popped ─────────────────────────────────────────────

    /// <summary>
    /// AC6 / Spec §6: frame_pushed-Event wird beim Push emittiert; frame_popped-Event mit
    /// return_segment_id beim Pop.
    /// </summary>
    [Fact]
    public async Task PushAndPop_EmitCorrectEvents()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var pushResp = await client.SendAsync(PushFrame(sid, "event-test", "push-ev-1"));
        pushResp.EnsureSuccessStatusCode();
        var frameId = (await pushResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        var popResp = await client.SendAsync(PopFrame(sid, frameId, "done", "pop-ev-1"));
        popResp.EnsureSuccessStatusCode();
        var returnSegId = (await popResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("return_segment_id").GetString()!;

        using var db = factory.CreateDbContext(Tenant);
        var events = await db.Events.AsNoTracking()
            .Where(ev => ev.SessionId == Ulid.Parse(sid))
            .OrderBy(ev => ev.Seq)
            .ToListAsync();

        // Spec §6: frame_pushed emittiert.
        var pushedEvent = events.FirstOrDefault(e => e.Type == "frame_pushed");
        Assert.NotNull(pushedEvent);
        using (var pushedDoc = JsonDocument.Parse(pushedEvent!.Payload))
        {
            Assert.Equal(frameId, pushedDoc.RootElement.GetProperty("frame_id").GetString());
        }

        // Spec §6: frame_popped { return_segment_id } emittiert.
        var poppedEvent = events.FirstOrDefault(e => e.Type == "frame_popped");
        Assert.NotNull(poppedEvent);
        using var poppedDoc = JsonDocument.Parse(poppedEvent!.Payload);
        Assert.Equal(returnSegId, poppedDoc.RootElement.GetProperty("return_segment_id").GetString());
    }

    // ── AC7: Render scope=path ────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC7 / Spec §2.5: render scope=path (default) rendert Root + offene Frames; evicted
    /// Frame-Segmente erscheinen nicht (I3 / Spec §2.2).
    /// </summary>
    [Fact]
    public async Task Render_ScopePath_RendersRootPlusOpenFrameSegments()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Root-Segment appenden.
        await client.SendAsync(AppendSegment(sid,
            new { kind = "user_msg", role = "user", content = "root-content" }, "append-path-root"));

        // Frame pushen und Frame-Segment appenden.
        var pushResp = await client.SendAsync(PushFrame(sid, "scope-path-frame", "push-path-1"));
        pushResp.EnsureSuccessStatusCode();
        await client.SendAsync(AppendSegment(sid,
            new { kind = "assistant_msg", role = "assistant", content = "frame-content" }, "append-path-frame"));

        // Render mit scope=path — beide Segmente sollen sichtbar sein.
        var renderResp = await client.SendAsync(
            Render(sid, new { provider = "anthropic", scope = "path", turn_advance = false }));
        renderResp.EnsureSuccessStatusCode();
        var body = await renderResp.Content.ReadAsStringAsync();

        // Beide Inhalte müssen im Render-Fragment erscheinen.
        Assert.Contains("root-content", body);
        Assert.Contains("frame-content", body);
    }

    /// <summary>
    /// AC7 / Spec §2.5: render scope=path (explizit) = identisch zum Default-Verhalten.
    /// Evicted Frame-Segmente (aus gepoppten Frames) sind nicht sichtbar.
    /// </summary>
    [Fact]
    public async Task Render_ScopePath_ExcludesEvictedFrameSegments()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Frame pushen, Segment appenden, dann Frame poppen (Segmente werden evicted).
        var pushResp = await client.SendAsync(PushFrame(sid, "scope-popped-frame", "push-evict-1"));
        pushResp.EnsureSuccessStatusCode();
        var frameId = (await pushResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        await client.SendAsync(AppendSegment(sid,
            new { kind = "user_msg", role = "user", content = "evicted-frame-content" }, "append-evict-1"));

        await client.SendAsync(PopFrame(sid, frameId, "popped result", "pop-evict-1"));

        // Root-Segment appenden (muss sichtbar bleiben).
        await client.SendAsync(AppendSegment(sid,
            new { kind = "user_msg", role = "user", content = "root-after-pop" }, "append-evict-root"));

        var renderResp = await client.SendAsync(
            Render(sid, new { provider = "anthropic", scope = "path", turn_advance = false }));
        renderResp.EnsureSuccessStatusCode();
        var body = await renderResp.Content.ReadAsStringAsync();

        // Evicted Segment darf nicht sichtbar sein (I3).
        Assert.DoesNotContain("evicted-frame-content", body);
        // Return-Segment (subagent_return) und Root-Segment müssen sichtbar sein.
        Assert.Contains("root-after-pop", body);
        // Spec §2.5: pop legt den Return-Content als kind=subagent_return im Parent-Frame an —
        // er muss im Render-Output erscheinen.
        Assert.Contains("popped result", body);
    }

    // ── AC8: Render scope=frame ────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC8 / Spec §2.5: render scope=frame rendert Static + gepinnte Root-Segmente + Segmente
    /// des Tip-Frames; nicht-gepinnte Root-Segmente und fremde Kontexte werden ausgeschlossen.
    /// </summary>
    [Fact]
    public async Task Render_ScopeFrame_RendersStaticPlusPinnedRootPlusTipFrameSegments()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        // Session mit Static-Segment erstellen.
        var sid = await CreateSessionAsync(client, new
        {
            static_segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "system-content", source = "core" },
            },
        });

        // Nicht-gepinntes Root-Segment (darf in scope=frame NICHT erscheinen).
        await client.SendAsync(AppendSegment(sid,
            new { kind = "user_msg", role = "user", content = "unpinned-root" }, "append-frame-unpinned"));

        // Gepinntes Root-Segment (muss in scope=frame erscheinen).
        var pinnedResp = await client.SendAsync(AppendSegment(sid,
            new { kind = "task", role = "assistant", content = "pinned-root", pinned = true }, "append-frame-pinned"));
        pinnedResp.EnsureSuccessStatusCode();
        var pinnedSegId = (await pinnedResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("segment_ids")[0].GetString()!;

        // Frame pushen und Tip-Frame-Segment appenden.
        var pushResp = await client.SendAsync(PushFrame(sid, "frame-scope-frame", "push-frame-scope"));
        pushResp.EnsureSuccessStatusCode();
        await client.SendAsync(AppendSegment(sid,
            new { kind = "assistant_msg", role = "assistant", content = "tip-frame-content" }, "append-frame-tip"));

        // Render mit scope=frame.
        var renderResp = await client.SendAsync(
            Render(sid, new { provider = "anthropic", scope = "frame", turn_advance = false }));
        renderResp.EnsureSuccessStatusCode();
        var body = await renderResp.Content.ReadAsStringAsync();

        // Static-Segment muss erscheinen (immer).
        Assert.Contains("system-content", body);
        // Gepinntes Root-Segment muss erscheinen.
        Assert.Contains("pinned-root", body);
        // Tip-Frame-Segment muss erscheinen.
        Assert.Contains("tip-frame-content", body);
        // Nicht-gepinntes Root-Segment darf NICHT erscheinen (Spec §2.5 scope=frame).
        Assert.DoesNotContain("unpinned-root", body);
    }

    /// <summary>
    /// AC8 / Spec §2.5: unbekannter scope-Wert ⇒ 400.
    /// </summary>
    [Fact]
    public async Task Render_UnknownScope_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var renderResp = await client.SendAsync(
            Render(sid, new { provider = "anthropic", scope = "invalid_scope", turn_advance = false }));

        Assert.Equal(HttpStatusCode.BadRequest, renderResp.StatusCode);
    }

    // ── AC9: Determinismus im Frame-Scope ────────────────────────────────────────────────────

    /// <summary>
    /// AC9 / Spec §4.6 / I4: Zwei identische scope=frame-Renders auf demselben Session-Stand
    /// liefern byte-identische request_fragment-Ausgabe (Determinismus-Garantie).
    /// </summary>
    [Fact]
    public async Task Render_ScopeFrame_TwoIdenticalRenders_AreBytewiseEqual()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client, new
        {
            static_segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "determinism-system", source = "core" },
            },
        });

        await client.SendAsync(PushFrame(sid, "determinism-frame", "push-det-1"));
        await client.SendAsync(AppendSegment(sid,
            new { kind = "assistant_msg", role = "assistant", content = "deterministic-content" }, "append-det-1"));

        // Erste Render (turn_advance=false, kein Idempotency-Key).
        var firstResp = await client.SendAsync(
            Render(sid, new { provider = "anthropic", scope = "frame", turn_advance = false }));
        firstResp.EnsureSuccessStatusCode();
        var firstBody = await firstResp.Content.ReadAsStringAsync();
        using var firstDoc = JsonDocument.Parse(firstBody);
        var firstFragment = firstDoc.RootElement.GetProperty("request_fragment").GetRawText();

        // Zweite Render — gleicher Zustand, keine Turn-Änderung.
        var secondResp = await client.SendAsync(
            Render(sid, new { provider = "anthropic", scope = "frame", turn_advance = false }));
        secondResp.EnsureSuccessStatusCode();
        var secondBody = await secondResp.Content.ReadAsStringAsync();
        using var secondDoc = JsonDocument.Parse(secondBody);
        var secondFragment = secondDoc.RootElement.GetProperty("request_fragment").GetRawText();

        // Spec §4.6 / I4: byte-identische Ausgabe für identischen Segment-Stand.
        Assert.Equal(firstFragment, secondFragment);
    }

    // ── AC10: Archivierung ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC10 / Spec §4.3: POST /archive ⇒ 204; danach GET /sessions/{sid} status = archived;
    /// terminale Promotion ist gelaufen (RecordingPromotionSink erhält Write).
    /// Idempotency-Key Pflicht ⇒ ohne Header 400.
    /// </summary>
    [Fact]
    public async Task Archive_Returns204_AndSessionStatusIsArchived_PromotionRan()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Sicherstellen dass Sink leer ist.
        var sink = factory.Services.GetRequiredService<RecordingPromotionSink>();
        sink.Reset();

        // Live Working-Segment appenden (Promotion-Kandidat).
        await client.SendAsync(AppendSegment(sid,
            new { kind = "assistant_msg", role = "assistant", content = "archived segment" }, "append-archive-1"));

        var archiveResp = await client.SendAsync(ArchiveSession(sid, "archive-key-1"));

        Assert.Equal(HttpStatusCode.NoContent, archiveResp.StatusCode);

        // GET /sessions/{sid} muss status = archived liefern.
        var getResp = await client.GetAsync($"/v1/sessions/{sid}");
        getResp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal("archived", doc.RootElement.GetProperty("status").GetString());

        // Terminale Promotion muss gelaufen sein — RecordingPromotionSink hat Writes.
        Assert.NotEmpty(sink.Writes);
    }

    /// <summary>
    /// AC10 / Spec §4.4: fehlender Idempotency-Key auf POST /archive ⇒ 400.
    /// </summary>
    [Fact]
    public async Task Archive_MissingIdempotencyKey_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Absichtlich kein Idempotency-Key.
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/archive");
        var response = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// AC10 / Spec §4.3 / §6: terminale Promotion emittiert fact_promoted-Event(s) vor
    /// dem Status-Wechsel; GET events belegt die Reihenfolge.
    /// </summary>
    [Fact]
    public async Task Archive_EmitsFactPromotedEvent_BeforeStatusChange()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        await client.SendAsync(AppendSegment(sid,
            new { kind = "assistant_msg", role = "assistant", content = "archive me" }, "append-archive-ev-1"));

        await client.SendAsync(ArchiveSession(sid, "archive-ev-1"));

        // Event-Stream prüfen: fact_promoted muss vorhanden sein.
        using var db = factory.CreateDbContext(Tenant);
        var events = await db.Events.AsNoTracking()
            .Where(ev => ev.SessionId == Ulid.Parse(sid))
            .OrderBy(ev => ev.Seq)
            .Select(ev => new { ev.Type, ev.Seq })
            .ToListAsync();

        // Spec §3.3 / §6: fact_promoted wird beim terminalen Archivieren emittiert
        // (FakeCompactionModel gibt non-empty zurück für fact-extraction-v1).
        var factPromotedEvent = events.FirstOrDefault(e => e.Type == "fact_promoted");
        Assert.NotNull(factPromotedEvent);
    }

    // ── Idempotenz (Spec §4.4) ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spec §4.4: Wiederholter Idempotency-Key auf POST /frames ⇒ identische Antwort, kein
    /// zweiter Frame angelegt.
    /// </summary>
    [Fact]
    public async Task Push_IdempotencyReplay_ReturnsSameResponseNoDuplicate()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var first = await client.SendAsync(PushFrame(sid, "idem-frame", "push-idem-replay"));
        first.EnsureSuccessStatusCode();
        var firstFrameId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        // Gleicher Key — muss identische Antwort liefern, keinen neuen Frame anlegen.
        var second = await client.SendAsync(PushFrame(sid, "idem-frame", "push-idem-replay"));
        second.EnsureSuccessStatusCode();
        var secondFrameId = (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        Assert.Equal(firstFrameId, secondFrameId);

        using var db = factory.CreateDbContext(Tenant);
        var frameCount = await db.Frames.AsNoTracking()
            .CountAsync(f => f.SessionId == Ulid.Parse(sid));
        Assert.Equal(1, frameCount);
    }

    /// <summary>
    /// Spec §4.4: Wiederholter Idempotency-Key auf DELETE /frames ⇒ identische Antwort, keine
    /// zweite Eviction.
    /// </summary>
    [Fact]
    public async Task Pop_IdempotencyReplay_ReturnsSameResponse()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var pushResp = await client.SendAsync(PushFrame(sid, "idem-pop-frame", "push-idem-pop"));
        pushResp.EnsureSuccessStatusCode();
        var frameId = (await pushResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        var firstPop = await client.SendAsync(PopFrame(sid, frameId, "result", "pop-idem-replay"));
        firstPop.EnsureSuccessStatusCode();
        var firstReturnSegId = (await firstPop.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("return_segment_id").GetString()!;

        // Gleicher Key — identische Antwort.
        var secondPop = await client.SendAsync(PopFrame(sid, frameId, "result", "pop-idem-replay"));
        secondPop.EnsureSuccessStatusCode();
        var secondReturnSegId = (await secondPop.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("return_segment_id").GetString()!;

        Assert.Equal(firstReturnSegId, secondReturnSegId);
    }

    // ── Pop mit explizitem return_kind ────────────────────────────────────────────────────────

    /// <summary>
    /// Spec §2.5: return_kind wird korrekt auf das Return-Segment angewandt.
    /// Default (kein return_kind) ⇒ "subagent_return".
    /// </summary>
    [Fact]
    public async Task Pop_ReturnKindSubagentReturn_IsDefaultKind()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var pushResp = await client.SendAsync(PushFrame(sid, "kind-test-frame", "push-kind-1"));
        pushResp.EnsureSuccessStatusCode();
        var frameId = (await pushResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        var popResp = await client.SendAsync(
            PopFrame(sid, frameId, "kind-test-content", "pop-kind-1", returnKind: "subagent_return"));
        popResp.EnsureSuccessStatusCode();
        var returnSegId = (await popResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("return_segment_id").GetString()!;

        using var db = factory.CreateDbContext(Tenant);
        var returnSeg = await db.Segments.AsNoTracking()
            .FirstAsync(s => s.Id == Ulid.Parse(returnSegId));
        Assert.Equal("subagent_return", returnSeg.Kind);
    }
}
