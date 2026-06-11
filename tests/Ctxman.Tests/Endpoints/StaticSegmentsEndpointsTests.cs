using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// API-Tests für <c>PUT /v1/sessions/{sid}/static-segments</c> (Spec §4.2; WP3-Akzeptanz 13–17).
/// </summary>
public sealed class StaticSegmentsEndpointsTests
{
    private const string Tenant = "tenant-static";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    private static async Task<(string Sid, long ContextVersion)> CreateSessionAsync(HttpClient client)
    {
        var body = new
        {
            static_segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "sys", source = "core" },
                new { kind = "tool_def", role = (string?)null, content = """{"name":"git","description":"Git"}""", source = "core" },
                new { kind = "tool_def", role = (string?)null, content = """{"name":"search","description":"Search"}""", source = "mcp:github" },
            },
        };
        var created = await client.PostAsJsonAsync("/v1/sessions", body);
        created.EnsureSuccessStatusCode();
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        return (doc.GetProperty("session_id").GetString()!, doc.GetProperty("context_version").GetInt64());
    }

    private static HttpRequestMessage PutStatic(string sid, long ifMatch, object body, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/v1/sessions/{sid}/static-segments")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        req.Headers.TryAddWithoutValidation("If-Match", ifMatch.ToString());
        return req;
    }

    [Fact]
    public async Task ReplaceStaticSegments_BumpsEpoch_ReturnsDiff()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var (sid, cv) = await CreateSessionAsync(client);

        var response = await client.SendAsync(PutStatic(sid, cv, new
        {
            segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "sys", source = "core" },
                new { kind = "tool_def", role = (string?)null, content = """{"name":"git","description":"Git"}""", source = "core" },
            },
        }, "static-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("static_epoch").GetInt32());
        Assert.True(doc.RootElement.GetProperty("diff").GetProperty("removed_tools").EnumerateArray().Any(t => t.GetString() == "search"));
    }

    [Fact]
    public async Task ReplaceStaticSegments_IfMatchConflict_Returns409()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var (sid, _) = await CreateSessionAsync(client);

        var response = await client.SendAsync(PutStatic(sid, 999, new { segments = Array.Empty<object>() }, "static-409"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ReplaceStaticSegments_SameIdempotencyKey_ReplaysWithoutSecondBump()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var (sid, cv) = await CreateSessionAsync(client);

        var body = new
        {
            segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "sys", source = "core" },
            },
        };

        var first = await client.SendAsync(PutStatic(sid, cv, body, "static-idem"));
        var second = await client.SendAsync(PutStatic(sid, cv, body, "static-idem"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        using var db = factory.CreateDbContext(Tenant);
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == Ulid.Parse(sid));
        Assert.Equal(1, session.StaticEpoch);
    }

    [Fact]
    public async Task ReplaceStaticSegments_OnToolRemovedDefault_ExternalizesToolResult()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var (sid, cv) = await CreateSessionAsync(client);

        var appendCall = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(new
            {
                kind = "tool_call",
                role = "assistant",
                content = "{}",
                source = "search",
                tool_call_id = "call-ext",
            }),
        };
        appendCall.Headers.Add("Idempotency-Key", "append-call");
        await client.SendAsync(appendCall);

        var appendResult = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(new
            {
                kind = "tool_result",
                role = "tool",
                content = "long result content for externalize test",
                tool_call_id = "call-ext",
            }),
        };
        appendResult.Headers.Add("Idempotency-Key", "append-result");
        var appended = await client.SendAsync(appendResult);
        appended.EnsureSuccessStatusCode();
        cv = (await appended.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context_version").GetInt64();

        var bump = await client.SendAsync(PutStatic(sid, cv, new
        {
            segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "sys", source = "core" },
                new { kind = "tool_def", role = (string?)null, content = """{"name":"git","description":"Git"}""", source = "core" },
            },
        }, "static-ext"));
        bump.EnsureSuccessStatusCode();

        using var db = factory.CreateDbContext(Tenant);
        var result = await db.Segments.AsNoTracking()
            .FirstAsync(s => s.Kind == "tool_result" && s.ToolCallId == "call-ext");
        Assert.Equal(SegmentState.Externalized, result.State);
        Assert.NotNull(result.BlobRef);
    }

    [Fact]
    public async Task ReplaceStaticSegments_OnToolRemovedEvict_EvictsWorkingUnit()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var created = await client.PostAsJsonAsync("/v1/sessions", new
        {
            policy_overrides = new { on_tool_removed = "evict" },
            static_segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "sys", source = "core" },
                new { kind = "tool_def", role = (string?)null, content = """{"name":"search","description":"Search"}""", source = "mcp:github" },
            },
        });
        created.EnsureSuccessStatusCode();
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        var sid = doc.GetProperty("session_id").GetString()!;
        var cv = doc.GetProperty("context_version").GetInt64();

        var appendCall = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(new { kind = "tool_call", role = "assistant", content = "{}", source = "search", tool_call_id = "evict-call" }),
        };
        appendCall.Headers.Add("Idempotency-Key", "append-call-evict");
        await client.SendAsync(appendCall);

        var appendResult = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(new { kind = "tool_result", role = "tool", content = "body", tool_call_id = "evict-call" }),
        };
        appendResult.Headers.Add("Idempotency-Key", "append-result-evict");
        var appended = await client.SendAsync(appendResult);
        cv = (await appended.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context_version").GetInt64();

        await client.SendAsync(PutStatic(sid, cv, new
        {
            segments = new[] { new { kind = "system_prompt", role = "system", content = "sys", source = "core" } },
        }, "static-evict"));

        using var db = factory.CreateDbContext(Tenant);
        var segments = await db.Segments.AsNoTracking()
            .Where(s => s.ToolCallId == "evict-call")
            .ToListAsync();
        Assert.All(segments, s => Assert.Equal(SegmentState.Evicted, s.State));
    }

    [Fact]
    public async Task ReplaceStaticSegments_OnToolRemovedKeep_LeavesWorkingUnitLive()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var created = await client.PostAsJsonAsync("/v1/sessions", new
        {
            policy_overrides = new { on_tool_removed = "keep" },
            static_segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "sys", source = "core" },
                new { kind = "tool_def", role = (string?)null, content = """{"name":"search","description":"Search"}""", source = "mcp:github" },
            },
        });
        created.EnsureSuccessStatusCode();
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        var sid = doc.GetProperty("session_id").GetString()!;
        var cv = doc.GetProperty("context_version").GetInt64();

        var appendResult = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(new { kind = "tool_result", role = "tool", content = "keep-body", tool_call_id = "keep-call", source = "search" }),
        };
        appendResult.Headers.Add("Idempotency-Key", "append-keep");
        await client.SendAsync(appendResult);

        var appendCall = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(new { kind = "tool_call", role = "assistant", content = "{}", source = "search", tool_call_id = "keep-call" }),
        };
        appendCall.Headers.Add("Idempotency-Key", "append-keep-call");
        var appended = await client.SendAsync(appendCall);
        cv = (await appended.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context_version").GetInt64();

        await client.SendAsync(PutStatic(sid, cv, new
        {
            segments = new[] { new { kind = "system_prompt", role = "system", content = "sys", source = "core" } },
        }, "static-keep"));

        using var db = factory.CreateDbContext(Tenant);
        var result = await db.Segments.AsNoTracking()
            .FirstAsync(s => s.Kind == "tool_result" && s.ToolCallId == "keep-call");
        Assert.Equal(SegmentState.Live, result.State);
        Assert.Equal("keep-body", result.Content);
    }

    [Fact]
    public async Task ReplaceStaticSegments_WritesStaticEpochBumpedEvent()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var (sid, cv) = await CreateSessionAsync(client);

        await client.SendAsync(PutStatic(sid, cv, new
        {
            segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "sys", source = "core" },
            },
        }, "static-event"));

        using var db = factory.CreateDbContext(Tenant);
        var ev = await db.Events.AsNoTracking().SingleAsync(e => e.Type == "static_epoch_bumped");
        using var payload = JsonDocument.Parse(ev.Payload);
        Assert.Equal(0, payload.RootElement.GetProperty("old_epoch").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("new_epoch").GetInt32());
        Assert.True(payload.RootElement.TryGetProperty("diff", out _));
    }
}
