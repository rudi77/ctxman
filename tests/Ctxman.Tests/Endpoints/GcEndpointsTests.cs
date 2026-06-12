using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Api.Gc;
using Ctxman.Core.Domain;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// AC12 (Spec §4.3 / §8): <c>POST /v1/sessions/{sid}/gc</c>. <c>minor</c> ⇒ 202 { job_id } und der
/// Worker läuft (beobachtbarer Effekt nach <c>WaitForIdleAsync</c>); <c>major</c> ⇒ 202 { job_id }
/// (kein Execute); unbekannter Level ⇒ 400; fremde/unbekannte Session ⇒ 404.
/// </summary>
public sealed class GcEndpointsTests
{
    private const string Tenant = "tenant-gc-ep";
    private const string OtherTenant = "tenant-gc-other";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory, string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        return client;
    }

    private static async Task<Ulid> SeedSessionWithExternalizable(CtxmanWebAppFactory factory)
    {
        Wp4Fixtures.EnsureHost(factory);
        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        var segment = Wp4Fixtures.LiveWorking(
            session.Id, Tenant, kind: "tool_result", tokens: 5000,
            content: new string('x', 12_000), role: Role.Tool);

        using var db = factory.CreateDbContext(Tenant);
        db.Sessions.Add(session);
        db.Segments.Add(segment);
        await db.SaveChangesAsync();
        return session.Id;
    }

    [Fact]
    public async Task Gc_Minor_Returns202_AndWorkerRuns()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory, Tenant);
        var sessionId = await SeedSessionWithExternalizable(factory);

        var response = await client.PostAsJsonAsync($"/v1/sessions/{sessionId}/gc", new { level = "minor" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.TryGetProperty("job_id", out var jobId));
            Assert.True(Ulid.TryParse(jobId.GetString(), out _));
        }

        await factory.Services.GetRequiredService<IGcJobQueue>().WaitForIdleAsync();

        // Effekt: der eine externalisierbare Kandidat ist nach dem Minor-Lauf externalisiert.
        using var verify = factory.CreateDbContext(Tenant);
        var externalized = await verify.Segments.AsNoTracking()
            .CountAsync(s => s.SessionId == sessionId && s.State == SegmentState.Externalized);
        Assert.Equal(1, externalized);
    }

    [Fact]
    public async Task Gc_Major_Returns202_WithJobId()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory, Tenant);
        var sessionId = await SeedSessionWithExternalizable(factory);

        var response = await client.PostAsJsonAsync($"/v1/sessions/{sessionId}/gc", new { level = "major" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("job_id", out var jobId));
        Assert.True(Ulid.TryParse(jobId.GetString(), out _));
    }

    [Fact]
    public async Task Gc_UnknownLevel_Returns400()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory, Tenant);
        var sessionId = await SeedSessionWithExternalizable(factory);

        var response = await client.PostAsJsonAsync($"/v1/sessions/{sessionId}/gc", new { level = "mega" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Gc_ForeignSession_Returns404()
    {
        await using var factory = new CtxmanWebAppFactory();
        var sessionId = await SeedSessionWithExternalizable(factory);

        // Anderer Tenant darf die Session nicht sehen (Spec §10 Global Query Filter) ⇒ 404.
        var otherClient = ClientFor(factory, OtherTenant);
        var response = await otherClient.PostAsJsonAsync($"/v1/sessions/{sessionId}/gc", new { level = "minor" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
