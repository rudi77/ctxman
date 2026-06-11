using System.Text;
using System.Text.Json;
using Ctxman.Api.Idempotency;
using Ctxman.Core;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Core.Rendering;
using Ctxman.Core.Storage;
using Ctxman.Core.Tokenization;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Endpoints;

/// <summary>
/// Render- und Static-Epoch-Endpunkte (Spec §4.2, §4.3, §4.6).
/// </summary>
public static class RenderEndpoints
{
    private static readonly JsonSerializerOptions EventPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IEndpointRouteBuilder MapRenderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/sessions/{sid}/render", RenderAsync);
        app.MapPut("/v1/sessions/{sid}/static-segments", ReplaceStaticSegmentsAsync);
        return app;
    }

    private static async Task<IResult> RenderAsync(
        string sid,
        RenderRequest request,
        HttpRequest httpRequest,
        CtxmanDbContext db,
        ITenantContext tenant,
        ProviderAdapterRegistry adapters,
        IdempotencyService idempotency,
        CancellationToken ct)
    {
        var turnAdvance = request.TurnAdvance ?? true;

        string? idempotencyKey = null;
        if (httpRequest.Headers.TryGetValue("Idempotency-Key", out var idemHeader)
            && !string.IsNullOrWhiteSpace(idemHeader.ToString()))
        {
            idempotencyKey = idemHeader.ToString();
        }

        // Spec §4.4: Idempotency-Key ist Pflicht bei turn_advance=true.
        if (turnAdvance && idempotencyKey is null)
        {
            return Results.BadRequest(new { error = "The Idempotency-Key header is required when turn_advance is true (Spec §4.4)." });
        }

        if (turnAdvance && idempotencyKey is not null)
        {
            var replay = await idempotency.TryReplayAsync(idempotencyKey, ct);
            if (replay is not null)
            {
                return replay;
            }
        }

        if (!adapters.TryGet(request.Provider, out var adapter) || adapter is null)
        {
            return Results.BadRequest(new
            {
                error = $"Unknown provider '{request.Provider}'.",
                registered_providers = adapters.RegisteredProviders,
            });
        }

        if (!Ulid.TryParse(sid, out var sessionId))
        {
            return Results.NotFound();
        }

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        var segments = await db.Segments
            .Where(s => s.SessionId == sessionId)
            .ToListAsync(ct);

        var plan = RenderPlanner.Plan(session, segments, session.Policy);

        // Spec §2 I5: unvollständige Units ⇒ 422 mit offenen tool_call-IDs.
        if (plan.OpenToolCallIds.Count > 0)
        {
            return Results.Json(
                new { open_tool_call_ids = plan.OpenToolCallIds },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var rendered = adapter.Render(plan.Model);
        var cachePrefixHash = CanonicalJson.ComputeCachePrefixHash(
            RenderStaticPrefix.BuildHashInput(plan.StaticPrefix));

        var now = DateTimeOffset.UtcNow;

        if (turnAdvance)
        {
            session.AdvanceTurn(now);
            session.IncrementVersion(now);
        }

        var maxEventSeq = await db.Events
            .Where(ev => ev.SessionId == sessionId)
            .Select(ev => (long?)ev.Seq)
            .MaxAsync(ct);
        var nextEventSeq = (maxEventSeq ?? -1) + 1;

        var renderPayload = JsonSerializer.Serialize(
            new
            {
                context_version = session.ContextVersion,
                static_epoch = session.StaticEpoch,
                tokens_total = plan.TokensTotal,
                cache_prefix_hash = cachePrefixHash,
            },
            EventPayloadOptions);

        db.Events.Add(new EventRecord
        {
            Id = Ulid.NewUlid(),
            TenantId = tenant.TenantId,
            SessionId = session.Id,
            Type = "render_served",
            Payload = renderPayload,
            Seq = nextEventSeq,
            CreatedAt = now,
        });

        var response = BuildRenderResponse(session, plan, rendered);

        if (turnAdvance && idempotencyKey is not null)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            await idempotency.StageRecordAsync(
                idempotencyKey,
                session.Id,
                StatusCodes.Status200OK,
                response,
                ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(response);
    }

    private static async Task<IResult> ReplaceStaticSegmentsAsync(
        string sid,
        ReplaceStaticSegmentsRequest request,
        HttpRequest httpRequest,
        CtxmanDbContext db,
        ITenantContext tenant,
        ITokenCounter tokenCounter,
        IBlobStore blobStore,
        IdempotencyService idempotency,
        CancellationToken ct)
    {
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var idemHeader)
            || string.IsNullOrWhiteSpace(idemHeader.ToString()))
        {
            return Results.BadRequest(new { error = "The Idempotency-Key header is required (Spec §4.4)." });
        }

        var idempotencyKey = idemHeader.ToString();

        var replay = await idempotency.TryReplayAsync(idempotencyKey, ct);
        if (replay is not null)
        {
            return replay;
        }

        if (!httpRequest.Headers.TryGetValue("If-Match", out var ifMatch)
            || !long.TryParse(ifMatch.ToString(), out var expectedVersion))
        {
            return Results.BadRequest(new { error = "The If-Match header is required and must be a context_version (Spec §4.2)." });
        }

        if (!Ulid.TryParse(sid, out var sessionId))
        {
            return Results.NotFound();
        }

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        if (expectedVersion != session.ContextVersion)
        {
            return Results.Json(
                new { context_version = session.ContextVersion },
                statusCode: StatusCodes.Status409Conflict);
        }

        var allSegments = await db.Segments
            .Where(s => s.SessionId == sessionId)
            .ToListAsync(ct);

        var oldStatic = allSegments.Where(s => s.Region == Region.Static).ToList();
        var working = allSegments.Where(s => s.Region == Region.Working).ToList();

        var newInputs = (request.Segments ?? Array.Empty<StaticSegmentDto>())
            .Select(s => new StaticSegmentInput(s.Kind, s.Role, s.Content, s.Source))
            .ToList();

        var diff = StaticRegionDiff.Compute(oldStatic, newInputs);

        var oldStaticTokens = oldStatic.Sum(s => s.Tokens);
        var newStaticTokens = newInputs.Sum(s => tokenCounter.Count(s.Content));
        var tokensDelta = newStaticTokens - oldStaticTokens;

        var now = DateTimeOffset.UtcNow;
        var oldEpoch = session.StaticEpoch;

        await StaticRegionDiff.ApplyOnToolRemovedAsync(
            session,
            working,
            diff,
            blobStore,
            tenant.TenantId,
            ct);

        db.Segments.RemoveRange(oldStatic);

        long seq = 0;
        var newStaticSegments = new List<Segment>(newInputs.Count);
        foreach (var entry in newInputs)
        {
            if (!TryParseRole(entry.Role, out var role, out var roleError))
            {
                return Results.BadRequest(new { error = roleError });
            }

            var content = entry.Content;
            if (Encoding.UTF8.GetByteCount(content) > 1_048_576)
            {
                return Results.Json(
                    new { error = "Inline content exceeds 1 MB. Upload via POST /v1/sessions/{sid}/blobs (Spec §4.3)." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            newStaticSegments.Add(Segment.CreateLive(
                id: Ulid.NewUlid(),
                sessionId: session.Id,
                tenantId: tenant.TenantId,
                region: Region.Static,
                kind: entry.Kind,
                role: role,
                content: content,
                seq: seq++,
                createdTurn: session.CurrentTurn,
                source: entry.Source,
                tokens: tokenCounter.Count(content)));
        }

        db.Segments.AddRange(newStaticSegments);

        session.BumpStaticEpoch(now);
        session.IncrementVersion(now);

        var diffDto = new StaticRegionDiffDto(
            AddedTools: diff.AddedTools,
            RemovedTools: diff.RemovedTools,
            AddedSources: diff.AddedSources,
            RemovedSources: diff.RemovedSources);

        var response = new ReplaceStaticSegmentsResponse(
            StaticEpoch: session.StaticEpoch,
            ContextVersion: session.ContextVersion,
            Diff: diffDto);

        var maxEventSeq = await db.Events
            .Where(ev => ev.SessionId == sessionId)
            .Select(ev => (long?)ev.Seq)
            .MaxAsync(ct);
        var nextEventSeq = (maxEventSeq ?? -1) + 1;

        var eventPayload = JsonSerializer.Serialize(
            new
            {
                old_epoch = oldEpoch,
                new_epoch = session.StaticEpoch,
                tokens_delta = tokensDelta,
                diff = new
                {
                    added_tools = diff.AddedTools,
                    removed_tools = diff.RemovedTools,
                    added_sources = diff.AddedSources,
                    removed_sources = diff.RemovedSources,
                },
            },
            EventPayloadOptions);

        db.Events.Add(new EventRecord
        {
            Id = Ulid.NewUlid(),
            TenantId = tenant.TenantId,
            SessionId = session.Id,
            Type = "static_epoch_bumped",
            Payload = eventPayload,
            Seq = nextEventSeq,
            CreatedAt = now,
        });

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await idempotency.StageRecordAsync(
            idempotencyKey,
            session.Id,
            StatusCodes.Status200OK,
            response,
            ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Ok(response);
    }

    private static RenderResponse BuildRenderResponse(
        Session session,
        RenderPlanResult plan,
        RenderResult rendered)
    {
        var cacheBreakpoints = rendered.CacheBreakpoints
            .Select(bp => (object)new Dictionary<string, object?>
            {
                ["kind"] = bp.Kind switch
                {
                    CacheBreakpointKind.StaticRegionEnd => "static_region_end",
                    _ => bp.Kind.ToString().ToLowerInvariant(),
                },
                ["index"] = bp.Index,
            })
            .ToList();

        return new RenderResponse(
            RequestFragment: rendered.RequestFragment,
            CacheBreakpoints: cacheBreakpoints,
            BuiltinTools: rendered.BuiltinTools,
            ContextVersion: session.ContextVersion,
            TokensTotal: plan.TokensTotal,
            WatermarkState: plan.WatermarkState);
    }

    private static bool TryParseRole(string? wire, out Role? role, out string error)
    {
        if (wire is null)
        {
            role = null;
            error = string.Empty;
            return true;
        }

        try
        {
            role = EnumWire.ParseRole(wire);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            role = null;
            error = $"Unknown role '{wire}'.";
            return false;
        }
    }
}
