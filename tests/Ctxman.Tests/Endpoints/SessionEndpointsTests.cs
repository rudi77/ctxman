using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// API-Tests für die Session-Endpunkte (Spec §4.3, WP2-Akzeptanz 1, 2, 7, 15). Der Tenant wird
/// über den <c>X-Tenant-Id</c>-Header gesetzt (Spec §4.1, Modus none); DB-Zustand wird über die
/// test-only <see cref="CtxmanWebAppFactory.CreateDbContext"/> direkt inspiziert.
/// </summary>
public sealed class SessionEndpointsTests
{
    private const string Tenant = "tenant-s";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    [Fact] // Akzeptanz 1 / §4.3: POST /v1/sessions ⇒ 201 mit { session_id, context_version }.
    public async Task CreateSession_Returns201_WithSessionIdAndContextVersion()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var response = await client.PostAsJsonAsync("/v1/sessions", new { agent_template_id = "tmpl-1" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("session_id", out var sid), "must contain snake_case session_id");
        Assert.False(string.IsNullOrWhiteSpace(sid.GetString()));
        Assert.True(doc.RootElement.TryGetProperty("context_version", out var cv), "must contain snake_case context_version");
        Assert.Equal(0, cv.GetInt64()); // §2.1: context_version startet bei 0.
    }

    [Fact] // Akzeptanz 1 / §4.3: static_segments landen als Static-Region mit static_epoch=0.
    public async Task CreateSession_StaticSegments_LandAsStaticRegion_Epoch0()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new
        {
            static_segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "You are a helpful agent." },
                new { kind = "tool_def", role = (string?)null, content = "tool: search" },
            },
        };
        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        response.EnsureSuccessStatusCode();
        var sid = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;

        using var db = factory.CreateDbContext(Tenant);
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == Ulid.Parse(sid));
        Assert.Equal(0, session.StaticEpoch); // §4.3: Static-Region initial auf Epoche 0.

        var segments = await db.Segments.AsNoTracking()
            .Where(s => s.SessionId == Ulid.Parse(sid))
            .ToListAsync();
        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Equal(Region.Static, s.Region));
    }

    [Fact] // Akzeptanz 1 / §5: ungültige policy_overrides ⇒ 400 (validiert beim Anlegen).
    public async Task CreateSession_InvalidPolicyOverride_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        // soft > hard verletzt soft <= hard <= emergency (§3.1 / §5).
        var body = new
        {
            policy_overrides = new
            {
                watermarks = new { soft = 0.9, hard = 0.5, emergency = 0.95 },
            },
        };
        var response = await client.PostAsJsonAsync("/v1/sessions", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // Akzeptanz 1 / §5: policy_overrides werden als Snapshot eingefroren (budget_tokens wirkt).
    public async Task CreateSession_PolicyOverride_IsFrozenOnSession()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var body = new { policy_overrides = new { budget_tokens = 12345 } };
        var response = await client.PostAsJsonAsync("/v1/sessions", body);
        response.EnsureSuccessStatusCode();
        var sid = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;

        using var db = factory.CreateDbContext(Tenant);
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == Ulid.Parse(sid));
        Assert.Equal(12345, session.Policy.BudgetTokens);
    }

    [Fact] // Akzeptanz 2 / §4.3: GET liefert Metadaten + tokens_used + watermark_state (snake_case).
    public async Task GetSession_Returns_Metadata_TokensUsed_WatermarkState()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var created = await client.PostAsJsonAsync("/v1/sessions", new { });
        created.EnsureSuccessStatusCode();
        var sid = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;

        var response = await client.GetAsync($"/v1/sessions/{sid}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(sid, root.GetProperty("session_id").GetString());
        Assert.True(root.TryGetProperty("tokens_used", out var tokensUsed), "must contain snake_case tokens_used");
        Assert.Equal(0, tokensUsed.GetInt32()); // frische Session ohne Working-Segmente.
        Assert.True(root.TryGetProperty("watermark_state", out var wm), "must contain snake_case watermark_state");
        Assert.Equal("ok", wm.GetString()); // 0 Tokens ⇒ unterhalb soft (§3.1).
        Assert.True(root.TryGetProperty("context_version", out _));
        Assert.True(root.TryGetProperty("static_epoch", out _));
    }

    [Fact] // Akzeptanz 2 / §4.3: unbekannte Session ⇒ 404.
    public async Task GetSession_Unknown_Returns404()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var response = await client.GetAsync($"/v1/sessions/{Ulid.NewUlid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact] // Akzeptanz 2 / §10: fremde Session ⇒ 404 (kein Existenz-Leak — NICHT 403).
    public async Task GetSession_OtherTenant_Returns404_NotLeaking()
    {
        using var factory = new CtxmanWebAppFactory();

        var ownerClient = factory.CreateClient();
        ownerClient.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-owner");
        var created = await ownerClient.PostAsJsonAsync("/v1/sessions", new { });
        created.EnsureSuccessStatusCode();
        var sid = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;

        var intruderClient = factory.CreateClient();
        intruderClient.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-intruder");
        var response = await intruderClient.GetAsync($"/v1/sessions/{sid}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact] // Akzeptanz 15 / §10: Request ohne tenant-Feld im Body gelingt, wenn der Tenant-Header gesetzt ist.
    public async Task CreateSession_NoTenantInBody_SucceedsViaHeaderContext()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        // Body trägt KEIN tenant_id-Feld; der Tenant kommt allein aus dem Header-Context.
        var response = await client.PostAsJsonAsync("/v1/sessions", new { agent_template_id = "x" });
        response.EnsureSuccessStatusCode();
        var sid = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;

        using var db = factory.CreateDbContext(Tenant);
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == Ulid.Parse(sid));
        Assert.Equal(Tenant, session.TenantId); // tenant_id stammt ausschließlich aus dem Context.
    }
}
