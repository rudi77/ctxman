using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// Idempotenz auf <c>POST /v1/sessions/{sid}/segments</c> (Spec §4.4, WP2-Akzeptanz 8, 9, 10).
/// Idempotency-Key ist Pflicht; ein wiederholter Key liefert die identische Antwort, ohne ein
/// zweites Segment anzulegen; Einträge älter als 24 h werden nicht mehr berücksichtigt.
/// </summary>
public sealed class SegmentIdempotencyTests
{
    private const string Tenant = "tenant-idem";

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
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;
    }

    private static HttpRequestMessage Append(string sid, object body, string? idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
        {
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return req;
    }

    [Fact] // Akzeptanz 8 / §4.4: Append ohne Idempotency-Key ⇒ 400 (Pflicht-Header).
    public async Task Append_WithoutIdempotencyKey_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var response = await client.SendAsync(
            Append(sid, new { kind = "user_msg", content = "a" }, idempotencyKey: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // Akzeptanz 9 / §4.4: gleicher Key ⇒ identischer Body, kein zweites Segment in der DB.
    public async Task Append_SameIdempotencyKey_ReplaysIdenticalBody_NoSecondSegment()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var first = await client.SendAsync(Append(sid, new { kind = "user_msg", content = "a" }, "idem-replay"));
        var second = await client.SendAsync(Append(sid, new { kind = "user_msg", content = "a" }, "idem-replay"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.Equal(firstBody, secondBody); // §4.4: byte-identische Antwort beim Replay.

        // Genau EIN Segment persistiert (kein Doppel-Append) und context_version nur einmal erhöht.
        using var db = factory.CreateDbContext(Tenant);
        var count = await db.Segments.AsNoTracking().CountAsync(s => s.SessionId == Ulid.Parse(sid));
        Assert.Equal(1, count);

        var detail = await client.GetAsync($"/v1/sessions/{sid}");
        using var doc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("context_version").GetInt64());
    }

    [Fact] // Akzeptanz 10 / §4.4: ein noch nicht abgelaufener Key (innerhalb 24 h) wird repliziert.
    public async Task Append_RecentKey_IsReplayed_AndPersistedWithTimestamp()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        await client.SendAsync(Append(sid, new { kind = "user_msg", content = "a" }, "idem-recent"));

        // Persistierter Record trägt Zeitstempel + ExpiresAt ~ now+24h (§4.4 Aufbewahrung 24 h).
        using (var db = factory.CreateDbContext(Tenant))
        {
            var record = await db.IdempotencyKeys.AsNoTracking().FirstAsync(k => k.Key == "idem-recent");
            Assert.True(record.ExpiresAt > record.CreatedAt);
            var retention = record.ExpiresAt - record.CreatedAt;
            Assert.True(retention >= TimeSpan.FromHours(23) && retention <= TimeSpan.FromHours(25),
                $"retention should be ~24h but was {retention}");
        }

        // Replay liefert weiterhin 201, kein zweites Segment.
        var replay = await client.SendAsync(Append(sid, new { kind = "user_msg", content = "a" }, "idem-recent"));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);

        using (var db = factory.CreateDbContext(Tenant))
        {
            Assert.Equal(1, await db.Segments.AsNoTracking().CountAsync(s => s.SessionId == Ulid.Parse(sid)));
        }
    }

    [Fact] // Akzeptanz 10 / §4.4: abgelaufener Key (> 24 h) wird NICHT repliziert ⇒ neuer Append.
    public async Task Append_ExpiredKey_IsNotReplayed_AppendsAgain()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        await client.SendAsync(Append(sid, new { kind = "user_msg", content = "a" }, "idem-expired"));

        // Record künstlich in die Vergangenheit verschieben (ExpiresAt < now) — reine DB-Manipulation
        // im Test, ohne Produktionscode anzufassen.
        using (var db = factory.CreateDbContext(Tenant))
        {
            var record = await db.IdempotencyKeys.FirstAsync(k => k.Key == "idem-expired");
            db.Entry(record).Property(r => r.ExpiresAt).CurrentValue = DateTimeOffset.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }

        // Gleicher Key, aber abgelaufen ⇒ kein Replay, regulärer zweiter Append.
        var second = await client.SendAsync(Append(sid, new { kind = "user_msg", content = "a" }, "idem-expired"));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using (var db = factory.CreateDbContext(Tenant))
        {
            Assert.Equal(2, await db.Segments.AsNoTracking().CountAsync(s => s.SessionId == Ulid.Parse(sid)));
        }
    }
}
