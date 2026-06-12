using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// Promotion-Failure-Verhalten bei Frame-Pop und Session-Archive (Spec-Lücke §2.5/§3.3,
/// Operator-Entscheidung 2026-06-12): schlägt die terminale Promotion fehl (LLM-Call oder
/// Sink-Write), wird die Operation NICHT ausgeführt — definierte Antwort
/// 503 { error: "promotion_failed", retryable: true } statt unbehandeltem 500.
/// Da der Promotion-Call vor der DB-Transaktion läuft, ist nichts mutiert; ein Retry mit
/// demselben Idempotency-Key läuft sauber neu (kein Replay der 503).
/// </summary>
public sealed class PromotionFailureTests
{
    private const string Tenant = "tenant-promotion-failure";

    // ── HTTP-Hilfsmethoden (Muster wie FrameEndpointsTests) ──────────────────────────────────

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/v1/sessions", new { });
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

    private static HttpRequestMessage PopFrame(string sid, string fid, string returnContent, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/v1/sessions/{sid}/frames/{fid}")
        {
            Content = JsonContent.Create(new { return_content = returnContent }),
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

    private static HttpRequestMessage ArchiveSession(string sid, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/archive");
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return req;
    }

    /// <summary>Push Frame + ein Promotion-eligibles Segment, gibt die Frame-ID zurück.</summary>
    private static async Task<string> PushFrameWithSegmentAsync(HttpClient client, string sid)
    {
        var pushResp = await client.SendAsync(PushFrame(sid, "promo-fail-frame", "push-pf-1"));
        pushResp.EnsureSuccessStatusCode();
        var frameId = (await pushResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frame_id").GetString()!;

        var appendResp = await client.SendAsync(AppendSegment(sid,
            new { kind = "decision", role = "assistant", content = "must not be lost" }, "append-pf-1"));
        appendResp.EnsureSuccessStatusCode();

        return frameId;
    }

    private static async Task AssertPromotionFailedBodyAsync(HttpResponseMessage response)
    {
        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Expected 503 ServiceUnavailable, got {(int)response.StatusCode} {response.StatusCode}. Body: {rawBody}");
        using var doc = JsonDocument.Parse(rawBody);
        Assert.Equal("promotion_failed", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.GetProperty("retryable").GetBoolean());
    }

    // ── Frame-Pop ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wirft der LLM-Call (ICompactionModel.SummarizeAsync) während der terminalen Promotion,
    /// antwortet DELETE /frames/{fid} mit 503 promotion_failed statt unbehandeltem 500.
    /// Kein State-Change: Frame bleibt open, Segment bleibt live, keine Events.
    /// </summary>
    [Fact]
    public async Task Pop_PromotionModelThrows_Returns503_NoStateChange()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);
        var frameId = await PushFrameWithSegmentAsync(client, sid);

        var fakeModel = factory.Services.GetRequiredService<FakeCompactionModel>();
        fakeModel.ThrowOnPromotion = new HttpRequestException(
            "Response status code does not indicate success: 401 (Unauthorized).");

        var popResp = await client.SendAsync(PopFrame(sid, frameId, "result", "pop-pf-503"));

        await AssertPromotionFailedBodyAsync(popResp);

        // Kein State-Change: Frame bleibt open, Segment bleibt live, keine Pop-/Promotion-Events.
        using var db = factory.CreateDbContext(Tenant);
        var frame = await db.Frames.AsNoTracking().FirstAsync(f => f.Id == Ulid.Parse(frameId));
        Assert.Equal(FrameStatus.Open, frame.Status);

        var frameSegments = await db.Segments.AsNoTracking()
            .Where(s => s.FrameId == Ulid.Parse(frameId))
            .ToListAsync();
        Assert.All(frameSegments, s => Assert.Equal(SegmentState.Live, s.State));

        var eventTypes = await db.Events.AsNoTracking()
            .Where(ev => ev.SessionId == Ulid.Parse(sid))
            .Select(ev => ev.Type)
            .ToListAsync();
        Assert.DoesNotContain("frame_popped", eventTypes);
        Assert.DoesNotContain("fact_promoted", eventTypes);
    }

    /// <summary>
    /// Wirft der Sink-Write (IPromotionSink.WriteAsync, z. B. Webhook EnsureSuccessStatusCode),
    /// gilt dasselbe: 503 promotion_failed, kein Pop.
    /// </summary>
    [Fact]
    public async Task Pop_PromotionSinkThrows_Returns503_FrameStaysOpen()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);
        var frameId = await PushFrameWithSegmentAsync(client, sid);

        var fakeSink = factory.Services.GetRequiredService<RecordingPromotionSink>();
        fakeSink.ThrowOnWrite = new HttpRequestException(
            "Response status code does not indicate success: 502 (Bad Gateway).");

        var popResp = await client.SendAsync(PopFrame(sid, frameId, "result", "pop-pf-sink"));

        await AssertPromotionFailedBodyAsync(popResp);

        using var db = factory.CreateDbContext(Tenant);
        var frame = await db.Frames.AsNoTracking().FirstAsync(f => f.Id == Ulid.Parse(frameId));
        Assert.Equal(FrameStatus.Open, frame.Status);
    }

    /// <summary>
    /// Die 503 darf NICHT als Idempotency-Snapshot gespeichert werden: ein Retry mit demselben
    /// Idempotency-Key nach behobenem Fehler führt den Pop regulär aus (200, Frame popped).
    /// </summary>
    [Fact]
    public async Task Pop_RetryWithSameIdempotencyKey_SucceedsAfterPromotionRecovers()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);
        var frameId = await PushFrameWithSegmentAsync(client, sid);

        var fakeModel = factory.Services.GetRequiredService<FakeCompactionModel>();
        fakeModel.ThrowOnPromotion = new HttpRequestException("401 (Unauthorized)");

        const string idempotencyKey = "pop-pf-retry";
        var failedResp = await client.SendAsync(PopFrame(sid, frameId, "result", idempotencyKey));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedResp.StatusCode);

        // Fehlerursache beheben, Retry mit DEMSELBEN Key — muss regulär durchlaufen (kein
        // Replay der 503, da der Idempotency-Snapshot nur innerhalb der Transaktion entsteht).
        fakeModel.ThrowOnPromotion = null;
        var retryResp = await client.SendAsync(PopFrame(sid, frameId, "result", idempotencyKey));

        Assert.Equal(HttpStatusCode.OK, retryResp.StatusCode);

        using var db = factory.CreateDbContext(Tenant);
        var frame = await db.Frames.AsNoTracking().FirstAsync(f => f.Id == Ulid.Parse(frameId));
        Assert.Equal(FrameStatus.Popped, frame.Status);

        var eventTypes = await db.Events.AsNoTracking()
            .Where(ev => ev.SessionId == Ulid.Parse(sid))
            .Select(ev => ev.Type)
            .ToListAsync();
        Assert.Contains("fact_promoted", eventTypes);
        Assert.Contains("frame_popped", eventTypes);
    }

    // ── Session-Archive ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gleiches Muster bei POST /archive: wirft die terminale Promotion, antwortet der Endpoint
    /// mit 503 promotion_failed; die Session bleibt aktiv (kein Status-Wechsel, keine Events).
    /// </summary>
    [Fact]
    public async Task Archive_PromotionModelThrows_Returns503_SessionStaysActive()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Promotion-eligibles Working-Segment (sonst läuft keine Promotion).
        var appendResp = await client.SendAsync(AppendSegment(sid,
            new { kind = "assistant_msg", role = "assistant", content = "archive me" }, "append-pf-arch"));
        appendResp.EnsureSuccessStatusCode();

        var fakeModel = factory.Services.GetRequiredService<FakeCompactionModel>();
        fakeModel.ThrowOnPromotion = new HttpRequestException("401 (Unauthorized)");

        var archiveResp = await client.SendAsync(ArchiveSession(sid, "archive-pf-503"));

        await AssertPromotionFailedBodyAsync(archiveResp);

        // Session bleibt aktiv, kein fact_promoted-Event.
        var getResp = await client.GetAsync($"/v1/sessions/{sid}");
        getResp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.NotEqual("archived", doc.RootElement.GetProperty("status").GetString());

        using var db = factory.CreateDbContext(Tenant);
        var eventTypes = await db.Events.AsNoTracking()
            .Where(ev => ev.SessionId == Ulid.Parse(sid))
            .Select(ev => ev.Type)
            .ToListAsync();
        Assert.DoesNotContain("fact_promoted", eventTypes);
    }

    /// <summary>
    /// Archive-Retry mit demselben Idempotency-Key nach behobenem Promotion-Fehler ⇒ 204,
    /// Session archived (kein Replay der 503).
    /// </summary>
    [Fact]
    public async Task Archive_RetryWithSameIdempotencyKey_SucceedsAfterPromotionRecovers()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var appendResp = await client.SendAsync(AppendSegment(sid,
            new { kind = "assistant_msg", role = "assistant", content = "archive me" }, "append-pf-arch-retry"));
        appendResp.EnsureSuccessStatusCode();

        var fakeModel = factory.Services.GetRequiredService<FakeCompactionModel>();
        fakeModel.ThrowOnPromotion = new HttpRequestException("401 (Unauthorized)");

        const string idempotencyKey = "archive-pf-retry";
        var failedResp = await client.SendAsync(ArchiveSession(sid, idempotencyKey));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedResp.StatusCode);

        fakeModel.ThrowOnPromotion = null;
        var retryResp = await client.SendAsync(ArchiveSession(sid, idempotencyKey));

        Assert.Equal(HttpStatusCode.NoContent, retryResp.StatusCode);

        var getResp = await client.GetAsync($"/v1/sessions/{sid}");
        getResp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal("archived", doc.RootElement.GetProperty("status").GetString());
    }
}
