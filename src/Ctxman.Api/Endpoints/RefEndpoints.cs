using System.Text.Json;
using Ctxman.Core;
using Ctxman.Core.Auth;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Endpoints;

/// <summary>
/// Page-Fault-Endpunkt (Spec §3.4 / §4.3): <c>GET /v1/sessions/{sid}/refs/{segment_id}</c>.
/// Lazy-Expansion eines externalisierten Segments. Der Tenant kommt ausschließlich aus dem
/// aufgelösten <see cref="ITenantContext"/> bzw. dem globalen Query-Filter (Spec §10); unbekannte
/// oder fremde Session/Segment-IDs liefern null ⇒ 404 ohne Existenz-Leak.
/// </summary>
public static class RefEndpoints
{
    // Spec §3.2.2: Externalisierung schreibt Blobs als text/plain; charset=utf-8. Falls der
    // BlobRef ausnahmsweise keinen Content-Type trägt, ist das der sinnvolle Default.
    private const string DefaultContentType = "text/plain; charset=utf-8";

    // Spec §6: Event-Payload ist kanonisches snake_case-JSON.
    private static readonly JsonSerializerOptions EventPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IEndpointRouteBuilder MapRefEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/sessions/{sid}/refs/{segment_id}", ExpandRefAsync)
            .WithMetadata(new ResourceAction("ref", null, "read")); // Spec §4.1
        return app;
    }

    private static async Task<IResult> ExpandRefAsync(
        string sid,
        string segment_id,
        CtxmanDbContext db,
        ITenantContext tenant,
        IBlobStore blobStore,
        CancellationToken ct)
    {
        if (!Ulid.TryParse(sid, out var sessionId) || !Ulid.TryParse(segment_id, out var segmentId))
        {
            // Ungültige ID kann nichts adressieren — wie unbekannt behandeln (kein Leak).
            return Results.NotFound();
        }

        // Spec §10: globaler Query-Filter ⇒ unbekanntes/fremdes Segment liefert null ⇒ 404.
        var segment = await db.Segments
            .FirstOrDefaultAsync(s => s.Id == segmentId && s.SessionId == sessionId, ct);
        if (segment is null)
        {
            return Results.NotFound();
        }

        // Spec §3.4 / §7.1: nur ein externalisiertes Segment mit noch vorhandenem Blob lässt sich
        // expandieren. evicted/compacted ODER gesweepter Blob ⇒ 410 mit Restinformation.
        if (segment.State == SegmentState.Externalized
            && segment.BlobRef is { } blobRef
            && await blobStore.Exists(tenant.TenantId, blobRef.Key, ct))
        {
            var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
            if (session is null)
            {
                return Results.NotFound();
            }

            string content;
            await using (var stream = await blobStore.Get(tenant.TenantId, blobRef.Key, ct))
            using (var reader = new StreamReader(stream))
            {
                content = await reader.ReadToEndAsync(ct);
            }

            var contentType = string.IsNullOrWhiteSpace(blobRef.ContentType)
                ? DefaultContentType
                : blobRef.ContentType;

            // Spec §3.4: Page Fault setzt last_referenced_turn des Ursprungssegments := current_turn.
            segment.MarkReferenced(session.CurrentTurn);

            // Spec §6: jede Mutation emittiert ein Event (Outbox, append-only, MAX(seq)+1).
            var maxEventSeq = await db.Events
                .Where(ev => ev.SessionId == sessionId)
                .Select(ev => (long?)ev.Seq)
                .MaxAsync(ct);
            var nextEventSeq = (maxEventSeq ?? -1) + 1;

            db.Events.Add(new EventRecord
            {
                Id = Ulid.NewUlid(),
                TenantId = tenant.TenantId,
                SessionId = session.Id,
                Type = "ref_expanded",
                Payload = JsonSerializer.Serialize(
                    new { segment_id = segment.Id.ToString() },
                    EventPayloadOptions),
                Seq = nextEventSeq,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(ct);

            return Results.Ok(new ExpandRefResponse(content, contentType));
        }

        // Spec §4.3 / §7.1: nicht mehr live (evicted/compacted/gesweept) ⇒ 410 mit bestmöglicher
        // Restinformation (summary + origin, origin nur falls gesetzt).
        return Results.Json(
            new ExpandRefGoneResponse(segment.Summary, segment.Origin),
            statusCode: StatusCodes.Status410Gone);
    }
}
