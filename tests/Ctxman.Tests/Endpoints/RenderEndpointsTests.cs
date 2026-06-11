using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// API-Tests für <c>POST /v1/sessions/{sid}/render</c> (Spec §4.3, §4.4, I5; WP3-Akzeptanz 1–6, 17).
/// </summary>
public sealed class RenderEndpointsTests
{
    private const string Tenant = "tenant-render";

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
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;
    }

    private static HttpRequestMessage Render(string sid, object body, string? idempotencyKey = "render-idem")
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

    [Fact]
    public async Task Render_Returns200_WithAllSpecFields()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client, new
        {
            static_segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "sys", source = "core" },
            },
        });

        var response = await client.SendAsync(Render(sid, new { provider = "anthropic" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("request_fragment", out _));
        Assert.True(root.TryGetProperty("cache_breakpoints", out _));
        Assert.True(root.TryGetProperty("builtin_tools", out _));
        Assert.True(root.TryGetProperty("context_version", out _));
        Assert.True(root.TryGetProperty("tokens_total", out _));
        Assert.True(root.TryGetProperty("watermark_state", out _));
    }

    [Fact]
    public async Task Render_UnknownProvider_Returns400_WithRegisteredList()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var response = await client.SendAsync(Render(sid, new { provider = "gemini" }, "idem-1"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("registered_providers", out var providers));
        var list = providers.EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("anthropic", list);
        Assert.Contains("openai", list);
    }

    [Fact]
    public async Task Render_TurnAdvanceWithoutIdempotencyKey_Returns400()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var response = await client.SendAsync(Render(sid, new { provider = "anthropic", turn_advance = true }, idempotencyKey: null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Render_OpenToolCall_Returns422_WithIds()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var append = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(new
            {
                kind = "tool_call",
                role = "assistant",
                content = "calling",
                source = "search",
                tool_call_id = "open-call-1",
            }),
        };
        append.Headers.Add("Idempotency-Key", "append-open");
        await client.SendAsync(append);

        var response = await client.SendAsync(Render(sid, new { provider = "anthropic" }, "render-open"));
        Assert.Equal((HttpStatusCode)422, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("open_tool_call_ids").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("open-call-1", ids);
    }

    [Fact]
    public async Task Render_TurnAdvance_IncrementsCurrentTurn_OnceWithIdempotency()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var first = await client.SendAsync(Render(sid, new { provider = "anthropic" }, "turn-idem"));
        var second = await client.SendAsync(Render(sid, new { provider = "anthropic" }, "turn-idem"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        using var db = factory.CreateDbContext(Tenant);
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == Ulid.Parse(sid));
        Assert.Equal(1, session.CurrentTurn);
    }

    [Fact]
    public async Task Render_WritesRenderServedEvent()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        await client.SendAsync(Render(sid, new { provider = "openai", turn_advance = false }, idempotencyKey: null));

        using var db = factory.CreateDbContext(Tenant);
        var ev = await db.Events.AsNoTracking().SingleAsync(e => e.Type == "render_served");
        using var payload = JsonDocument.Parse(ev.Payload);
        Assert.True(payload.RootElement.TryGetProperty("cache_prefix_hash", out _));
        Assert.True(payload.RootElement.TryGetProperty("tokens_total", out _));
    }

    [Fact]
    public async Task Render_BuiltinTools_ContainExpandContextRef()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var response = await client.SendAsync(Render(sid, new { provider = "anthropic", turn_advance = false }, idempotencyKey: null));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("expand_context_ref", body);
        Assert.Contains("segment_id", body);
    }
}
