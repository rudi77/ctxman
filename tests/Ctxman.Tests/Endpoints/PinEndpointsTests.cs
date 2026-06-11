using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// AC13 (Spec §4.3 / §2.2 I1): <c>POST/DELETE /v1/sessions/{sid}/segments/{segid}/pin</c>.
/// Pin/Unpin eines Working-Segments ⇒ 204; Pin/Unpin eines Static-Segments ⇒ 409 (I1 immutable).
/// Das „Minor GC fasst gepinntes Segment nie an"-Verhalten testet <c>MinorGcWorkerTests</c>.
/// </summary>
public sealed class PinEndpointsTests
{
    private const string Tenant = "tenant-pin";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    private static async Task<(Ulid SessionId, Ulid WorkingId, Ulid StaticId)> SeedAsync(CtxmanWebAppFactory factory)
    {
        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));

        var working = Wp4Fixtures.LiveWorking(
            session.Id, Tenant, kind: "user_msg", tokens: 10, role: Role.User);

        var staticSeg = Segment.CreateLive(
            id: Ulid.NewUlid(),
            sessionId: session.Id,
            tenantId: Tenant,
            region: Region.Static,
            kind: "system_prompt",
            role: Role.System,
            content: "sys",
            seq: Wp4Fixtures.NextSeq(),
            createdTurn: 0,
            tokens: 1);

        using var db = factory.CreateDbContext(Tenant);
        db.Sessions.Add(session);
        db.Segments.AddRange(working, staticSeg);
        await db.SaveChangesAsync();
        return (session.Id, working.Id, staticSeg.Id);
    }

    [Fact]
    public async Task PinThenUnpin_WorkingSegment_Returns204_AndMutatesPinned()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var (sessionId, workingId, _) = await SeedAsync(factory);

        var pin = await client.PostAsync($"/v1/sessions/{sessionId}/segments/{workingId}/pin", null);
        Assert.Equal(HttpStatusCode.NoContent, pin.StatusCode);

        using (var db = factory.CreateDbContext(Tenant))
        {
            var seg = await db.Segments.AsNoTracking().SingleAsync(s => s.Id == workingId);
            Assert.True(seg.Pinned);
        }

        var unpin = await client.DeleteAsync($"/v1/sessions/{sessionId}/segments/{workingId}/pin");
        Assert.Equal(HttpStatusCode.NoContent, unpin.StatusCode);

        using (var db = factory.CreateDbContext(Tenant))
        {
            var seg = await db.Segments.AsNoTracking().SingleAsync(s => s.Id == workingId);
            Assert.False(seg.Pinned);
        }
    }

    [Fact]
    public async Task Pin_StaticSegment_Returns409()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var (sessionId, _, staticId) = await SeedAsync(factory);

        var response = await client.PostAsync($"/v1/sessions/{sessionId}/segments/{staticId}/pin", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Unpin_StaticSegment_Returns409()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var (sessionId, _, staticId) = await SeedAsync(factory);

        var response = await client.DeleteAsync($"/v1/sessions/{sessionId}/segments/{staticId}/pin");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
