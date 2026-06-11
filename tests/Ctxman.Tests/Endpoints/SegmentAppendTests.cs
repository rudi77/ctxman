using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// API-Tests für <c>POST /v1/sessions/{sid}/segments</c> (Spec §4.3 / §4.4, WP2-Akzeptanz
/// 3, 4, 5, 7, 11, 12, 13, 14). Der Tenant kommt über den <c>X-Tenant-Id</c>-Header; der
/// Idempotency-Key ist auf diesem Endpunkt Pflicht und wird je Request frisch vergeben.
/// </summary>
public sealed class SegmentAppendTests
{
    private const string Tenant = "tenant-seg";

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

    private static HttpRequestMessage Append(string sid, object body, string idempotencyKey, long? ifMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        if (ifMatch is not null)
        {
            req.Headers.TryAddWithoutValidation("If-Match", ifMatch.Value.ToString());
        }
        return req;
    }

    [Fact] // Akzeptanz 3 / §4.3: Single-Form ⇒ 201 { segment_ids[], context_version } (snake_case).
    public async Task Append_Single_Returns201_WithSegmentIdsAndVersion()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var response = await client.SendAsync(
            Append(sid, new { kind = "user_msg", role = "user", content = "hi" }, "idem-single-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("segment_ids");
        Assert.Equal(JsonValueKind.Array, ids.ValueKind);
        Assert.Equal(1, ids.GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("context_version").GetInt64()); // create v0 ⇒ append v1.
    }

    [Fact] // Akzeptanz 3 / §4.3: Batch-Form ⇒ 201 mit segment_ids für jeden Eintrag.
    public async Task Append_Batch_Returns201_WithAllSegmentIds()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var body = new
        {
            segments = new[]
            {
                new { kind = "user_msg", role = "user", content = "first" },
                new { kind = "assistant_msg", role = "assistant", content = "second" },
                new { kind = "user_msg", role = "user", content = "third" },
            },
        };
        var response = await client.SendAsync(Append(sid, body, "idem-batch-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, doc.RootElement.GetProperty("segment_ids").GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("context_version").GetInt64()); // genau +1 trotz Batch (§4.4).
    }

    [Theory] // Akzeptanz 4 / I1 (§2.2): Append in die Static-Region ⇒ 409.
    [InlineData("system_prompt", null)]
    [InlineData("tool_def", null)]
    [InlineData("skill_index", null)]
    [InlineData("user_msg", "static")]
    public async Task Append_ToStaticRegion_Returns409(string kind, string? region)
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        object body = region is null
            ? new { kind, content = "x" }
            : new { kind, content = "x", region };
        var response = await client.SendAsync(Append(sid, body, $"idem-static-{kind}-{region}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact] // Akzeptanz 5 / §4.3: Inline-content > 1 MB ⇒ 413.
    public async Task Append_InlineContentOver1MB_Returns413()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var oversized = new string('a', 1_048_576 + 1); // 1 MiB + 1 Byte (ASCII ⇒ 1 Byte/char).
        var response = await client.SendAsync(
            Append(sid, new { kind = "user_msg", role = "user", content = oversized }, "idem-413"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact] // Akzeptanz 11 / §4.4: jede Mutation erhöht context_version monoton (+1 je Append).
    public async Task Append_IncrementsContextVersion_Monotonically()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var first = await client.SendAsync(Append(sid, new { kind = "user_msg", content = "a" }, "idem-v-1"));
        var second = await client.SendAsync(Append(sid, new { kind = "user_msg", content = "b" }, "idem-v-2"));
        var third = await client.SendAsync(Append(sid, new { kind = "user_msg", content = "c" }, "idem-v-3"));

        Assert.Equal(1, await VersionAsync(first));
        Assert.Equal(2, await VersionAsync(second));
        Assert.Equal(3, await VersionAsync(third));
    }

    [Fact] // Akzeptanz 12 / §4.4: If-Match mit veralteter Version ⇒ 409 + aktuelle Version im Body.
    public async Task Append_StaleIfMatch_Returns409_WithCurrentVersion()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Erst eine Mutation ⇒ aktuelle Version ist 1.
        await client.SendAsync(Append(sid, new { kind = "user_msg", content = "a" }, "idem-ifm-1"));

        // If-Match: 0 ist jetzt veraltet ⇒ 409 mit context_version=1.
        var response = await client.SendAsync(
            Append(sid, new { kind = "user_msg", content = "b" }, "idem-ifm-2", ifMatch: 0));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("context_version").GetInt64());
    }

    [Fact] // Akzeptanz 12 / §4.4: If-Match mit aktueller Version ⇒ Append gelingt.
    public async Task Append_MatchingIfMatch_Succeeds()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var response = await client.SendAsync(
            Append(sid, new { kind = "user_msg", content = "a" }, "idem-ifm-ok", ifMatch: 0));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact] // Akzeptanz 13 / §2.2: seq ist serverseitig vergeben und strikt monoton, auch über Batches.
    public async Task Append_SeqIsServerAssigned_StrictlyMonotonic_AcrossBatches()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        // Single, dann Batch von 3, dann noch ein Single ⇒ erwartet seq 0..4 lückenlos & aufsteigend.
        await client.SendAsync(Append(sid, new { kind = "user_msg", content = "s0" }, "idem-seq-1"));
        await client.SendAsync(Append(sid, new
        {
            segments = new[]
            {
                new { kind = "user_msg", content = "s1" },
                new { kind = "assistant_msg", content = "s2" },
                new { kind = "user_msg", content = "s3" },
            },
        }, "idem-seq-2"));
        await client.SendAsync(Append(sid, new { kind = "user_msg", content = "s4" }, "idem-seq-3"));

        using var db = factory.CreateDbContext(Tenant);
        var seqs = await db.Segments.AsNoTracking()
            .Where(s => s.SessionId == Ulid.Parse(sid))
            .OrderBy(s => s.Seq)
            .Select(s => s.Seq)
            .ToListAsync();

        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, seqs);
        // Strikt monoton: jeder Wert echt größer als der vorige (keine Duplikate).
        for (var i = 1; i < seqs.Count; i++)
        {
            Assert.True(seqs[i] > seqs[i - 1], $"seq[{i}]={seqs[i]} must be > seq[{i - 1}]={seqs[i - 1]}");
        }
    }

    [Fact] // Akzeptanz 14 / §2.2: tokens werden via ITokenCounter gesetzt (> 0 für nicht-leeren Content).
    public async Task Append_NonEmptyContent_TokensUsedGreaterThanZero()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        await client.SendAsync(Append(sid,
            new { kind = "user_msg", role = "user", content = "the quick brown fox jumps over the lazy dog" },
            "idem-tokens-1"));

        var detail = await client.GetAsync($"/v1/sessions/{sid}");
        detail.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("tokens_used").GetInt32() > 0);
    }

    private static async Task<long> VersionAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("context_version").GetInt64();
    }
}
