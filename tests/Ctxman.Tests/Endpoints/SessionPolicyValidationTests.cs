using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// AC14 (Spec §5): Validierung von compaction- und promotion-Policy-Overrides beim
/// <c>POST /v1/sessions</c>. Gültige Werte ⇒ 201 + effektive Policy gespeichert;
/// ungültige Werte ⇒ 400.
/// </summary>
public sealed class SessionPolicyValidationTests
{
    private const string Tenant = "tenant-policy-val";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    // AC14: gültige compaction-Overrides ⇒ 201, Policy gespeichert.
    [Fact]
    public async Task CreateSession_ValidCompactionOverride_Returns201_AndEffectivePolicyStored()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                compaction = new
                {
                    model = "my-model",
                    prompt_template_id = "my-tmpl-v1",
                    max_share = 0.6,
                },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var sid = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;

        using var db = factory.CreateDbContext(Tenant);
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == Ulid.Parse(sid));

        Assert.Equal("my-model", session.Policy.Compaction.Model);
        Assert.Equal("my-tmpl-v1", session.Policy.Compaction.PromptTemplateId);
        Assert.Equal(0.6, session.Policy.Compaction.MaxShare, precision: 9);
    }

    // AC14: gültige promotion/sink-Overrides ⇒ 201, Policy gespeichert.
    [Fact]
    public async Task CreateSession_ValidPromotionOverride_Returns201_AndEffectivePolicyStored()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                promotion = new
                {
                    sink = new
                    {
                        type = "webhook",
                        url = "https://my-sink.example/ingest",
                    },
                },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var sid = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;

        using var db = factory.CreateDbContext(Tenant);
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == Ulid.Parse(sid));

        Assert.Equal("webhook", session.Policy.Promotion.Sink.Type);
        Assert.Equal("https://my-sink.example/ingest", session.Policy.Promotion.Sink.Url);
    }

    // AC14: compaction.max_share = 0 (Grenze ≤ 0) ⇒ 400.
    [Fact]
    public async Task CreateSession_CompactionMaxShare_Zero_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                compaction = new { max_share = 0.0 },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC14: compaction.max_share > 1 ⇒ 400.
    [Fact]
    public async Task CreateSession_CompactionMaxShare_GreaterThanOne_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                compaction = new { max_share = 1.5 },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC14: compaction.max_share negativ ⇒ 400.
    [Fact]
    public async Task CreateSession_CompactionMaxShare_Negative_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                compaction = new { max_share = -0.1 },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC14: compaction.model leer ⇒ 400.
    [Fact]
    public async Task CreateSession_CompactionModel_Empty_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                compaction = new
                {
                    model = "",
                    prompt_template_id = "tmpl",
                    max_share = 0.5,
                },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC14: compaction.prompt_template_id leer ⇒ 400.
    [Fact]
    public async Task CreateSession_CompactionPromptTemplateId_Empty_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                compaction = new
                {
                    model = "my-model",
                    prompt_template_id = "",
                    max_share = 0.5,
                },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC14: promotion.sink.type unbekannt ("kafka") ⇒ 400.
    [Fact]
    public async Task CreateSession_PromotionSinkType_Unknown_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                promotion = new
                {
                    sink = new
                    {
                        type = "kafka",
                        url = "kafka://broker:9092/topic",
                    },
                },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC14: promotion.sink.type = "webhook" mit fehlender URL ⇒ 400.
    [Fact]
    public async Task CreateSession_WebhookSink_MissingUrl_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                promotion = new
                {
                    sink = new
                    {
                        type = "webhook",
                        // url absichtlich weggelassen
                    },
                },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC14: promotion.sink.type = "webhook" mit ungültiger URL (nicht absolut) ⇒ 400.
    [Fact]
    public async Task CreateSession_WebhookSink_InvalidUrl_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                promotion = new
                {
                    sink = new
                    {
                        type = "webhook",
                        url = "not-a-valid-uri",
                    },
                },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC14: compaction.max_share = 1.0 (Grenzwert, exakt erlaubt) ⇒ 201.
    [Fact]
    public async Task CreateSession_CompactionMaxShare_ExactlyOne_Returns201()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            policy_overrides = new
            {
                compaction = new { max_share = 1.0 },
            },
        };

        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
