using System.Text;
using System.Text.Json;
using Ctxman.Api.Idempotency;
using Ctxman.Core;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Core.Tokenization;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Endpoints;

/// <summary>
/// Segment-Endpunkte (Spec §4.3): <c>POST /v1/sessions/{sid}/segments</c> (Single + Batch).
/// Gruppiert pro Ressource (CLAUDE.md). Der Tenant kommt ausschließlich aus dem aufgelösten
/// <see cref="ITenantContext"/> (Spec §10); der globale Query-Filter liefert für unbekannte
/// oder fremde Sessions null ⇒ 404 ohne Existenz-Leak.
/// </summary>
public static class SegmentEndpoints
{
    // Spec §2.3: Kinds, deren typische Region Static ist. Append in die Static-Region ist nur
    // über den Epoch-Bump (§4.2) zulässig; ein direkter Append ⇒ 409 (I1).
    private static readonly HashSet<string> StaticKinds = new(StringComparer.Ordinal)
    {
        "system_prompt",
        "tool_def",
        "skill_index",
    };

    // Spec §4.3: Inline-content über 1 MiB ⇒ 413; großer Inhalt läuft über den Blob-Upload-Pfad.
    private const int MaxInlineContentBytes = 1_048_576;

    // Spec §6: Event-Payload ist kanonisches snake_case-JSON.
    private static readonly JsonSerializerOptions EventPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IEndpointRouteBuilder MapSegmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/sessions/{sid}/segments", AppendSegmentsAsync);
        app.MapPost("/v1/sessions/{sid}/segments/{segid}/pin", PinSegmentAsync);
        app.MapDelete("/v1/sessions/{sid}/segments/{segid}/pin", UnpinSegmentAsync);
        return app;
    }

    // Spec §4.3: POST .../pin ⇒ Segment.Pin() ⇒ 204. Static-Segment ⇒ 409 (I1).
    private static Task<IResult> PinSegmentAsync(
        string sid,
        string segid,
        CtxmanDbContext db,
        CancellationToken ct) =>
        SetPinnedAsync(sid, segid, pinned: true, db, ct);

    // Spec §4.3: DELETE .../pin ⇒ Segment.Unpin() ⇒ 204. Static-Segment ⇒ 409 (I1).
    private static Task<IResult> UnpinSegmentAsync(
        string sid,
        string segid,
        CtxmanDbContext db,
        CancellationToken ct) =>
        SetPinnedAsync(sid, segid, pinned: false, db, ct);

    private static async Task<IResult> SetPinnedAsync(
        string sid,
        string segid,
        bool pinned,
        CtxmanDbContext db,
        CancellationToken ct)
    {
        if (!Ulid.TryParse(sid, out var sessionId) || !Ulid.TryParse(segid, out var segmentId))
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

        try
        {
            // Spec §2.2 I1: Pin/Unpin auf einem Static-Segment ⇒ StaticRegionImmutableException ⇒ 409.
            if (pinned)
            {
                segment.Pin();
            }
            else
            {
                segment.Unpin();
            }
        }
        catch (StaticRegionImmutableException)
        {
            return Results.Json(
                new { error = "Pin/unpin is not allowed on a static-region segment (Spec §2.2 I1)." },
                statusCode: StatusCodes.Status409Conflict);
        }

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> AppendSegmentsAsync(
        string sid,
        AppendSegmentsRequest request,
        HttpRequest httpRequest,
        CtxmanDbContext db,
        ITenantContext tenant,
        ITokenCounter tokenCounter,
        IdempotencyService idempotency,
        CancellationToken ct)
    {
        // Spec §4.4: Idempotency-Key ist auf POST segments Pflicht. Fehlend/leer ⇒ 400 vor jedem Write.
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var idemHeader)
            || string.IsNullOrWhiteSpace(idemHeader.ToString()))
        {
            return Results.BadRequest(new { error = "The Idempotency-Key header is required (Spec §4.4)." });
        }

        var idempotencyKey = idemHeader.ToString();

        // Spec §4.4: wiederholter Key ⇒ identische Antwort, kein Doppel-Append. Replay vor dem Write.
        var replay = await idempotency.TryReplayAsync(idempotencyKey, ct);
        if (replay is not null)
        {
            return replay;
        }

        if (!Ulid.TryParse(sid, out var sessionId))
        {
            // Ungültige ID kann keine Session adressieren — wie unbekannt behandeln (kein Leak).
            return Results.NotFound();
        }

        // Spec §10: globaler Query-Filter ⇒ unbekannte ODER fremde Session liefert null ⇒ 404.
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        // Spec §4.4: optimistic concurrency. If-Match (optional) = erwartete context_version.
        if (httpRequest.Headers.TryGetValue("If-Match", out var ifMatch)
            && long.TryParse(ifMatch.ToString(), out var expectedVersion)
            && expectedVersion != session.ContextVersion)
        {
            // Konflikt: Client muss re-rendern. Kein Write.
            return Results.Json(
                new { context_version = session.ContextVersion },
                statusCode: StatusCodes.Status409Conflict);
        }

        var items = request.ToList();

        // Spec §2.2 I-order: seq server-vergeben, strikt monoton pro Session. Nie vom Client.
        var maxSeq = await db.Segments
            .Where(s => s.SessionId == sessionId)
            .Select(s => (long?)s.Seq)
            .MaxAsync(ct);
        var nextSeq = (maxSeq ?? -1) + 1;

        // Spec §6: Event-seq ist pro Session monoton (Outbox-Cursor, §4.3 after_seq).
        var maxEventSeq = await db.Events
            .Where(ev => ev.SessionId == sessionId)
            .Select(ev => (long?)ev.Seq)
            .MaxAsync(ct);
        var nextEventSeq = (maxEventSeq ?? -1) + 1;

        var now = DateTimeOffset.UtcNow;
        var newSegments = new List<Segment>(items.Count);
        var newEvents = new List<EventRecord>(items.Count);

        foreach (var item in items)
        {
            var kind = item.Kind ?? string.Empty;

            // Spec §2.2 I1: Append in die Static-Region ist nur via Epoch-Bump (§4.2) zulässig.
            // Static-Signal: explizites region=static ODER ein Static-Kind (§2.3).
            var requestsStatic = string.Equals(item.Region, "static", StringComparison.Ordinal)
                || StaticKinds.Contains(kind);
            if (requestsStatic)
            {
                return Results.Json(
                    new { error = "Append to the static region is only allowed via the epoch-bump endpoint (Spec §4.2 / I1)." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            Role? role = null;
            if (item.Role is not null)
            {
                try
                {
                    role = EnumWire.ParseRole(item.Role);
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { error = $"Unknown role '{item.Role}'." });
                }
            }

            Segment segment;
            if (item.BlobRef is not null)
            {
                // Spec §4.3 / §2.6: blob_ref ⇒ von Anfang an externalisiert. Keine 1-MB-Prüfung
                // (gilt nur für Inline-content). Tokens aus verfügbarem content oder 0.
                var blobRef = new BlobRef(
                    Store: item.BlobRef.Store,
                    Key: item.BlobRef.Key,
                    SizeBytes: item.BlobRef.SizeBytes,
                    ContentType: item.BlobRef.ContentType);

                var tokens = item.Content is null ? 0 : tokenCounter.Count(item.Content);

                segment = Segment.CreateExternalized(
                    id: Ulid.NewUlid(),
                    sessionId: session.Id,
                    tenantId: tenant.TenantId,
                    region: Region.Working,
                    kind: kind,
                    role: role,
                    blobRef: blobRef,
                    summary: item.Content,
                    seq: nextSeq++,
                    createdTurn: session.CurrentTurn,
                    source: item.Source,
                    toolCallId: item.ToolCallId,
                    pinned: item.Pinned ?? false,
                    tokens: tokens);
            }
            else
            {
                var content = item.Content ?? string.Empty;

                // Spec §4.3: Inline-content über 1 MiB ⇒ 413, Hinweis auf den Blob-Upload-Pfad.
                if (Encoding.UTF8.GetByteCount(content) > MaxInlineContentBytes)
                {
                    return Results.Json(
                        new
                        {
                            error = "Inline content exceeds 1 MB. Upload via POST /v1/sessions/{sid}/blobs and append with blob_ref + summary (Spec §4.3).",
                        },
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                }

                segment = Segment.CreateLive(
                    id: Ulid.NewUlid(),
                    sessionId: session.Id,
                    tenantId: tenant.TenantId,
                    region: Region.Working,
                    kind: kind,
                    role: role,
                    content: content,
                    seq: nextSeq++,
                    createdTurn: session.CurrentTurn,
                    source: item.Source,
                    toolCallId: item.ToolCallId,
                    pinned: item.Pinned ?? false,
                    tokens: tokenCounter.Count(content));
            }

            newSegments.Add(segment);

            // Spec §6: jede Mutation emittiert ein Event (Outbox, §7/§10 append-only).
            var payload = JsonSerializer.Serialize(
                new
                {
                    segment_id = segment.Id.ToString(),
                    kind = segment.Kind,
                    region = segment.Region.ToWire(),
                    seq = segment.Seq,
                    tokens = segment.Tokens,
                },
                EventPayloadOptions);

            newEvents.Add(new EventRecord
            {
                Id = Ulid.NewUlid(),
                TenantId = tenant.TenantId,
                SessionId = session.Id,
                Type = "segment_appended",
                Payload = payload,
                Seq = nextEventSeq++,
                CreatedAt = now,
            });
        }

        // Spec §4.4: append + Version-Increment + Events atomar in einer Transaktion.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Segments.AddRange(newSegments);
        db.Events.AddRange(newEvents);

        // Spec §4.4: context_version genau EINMAL pro Request erhöhen (auch bei Batch).
        session.IncrementVersion(now);

        var response = new AppendSegmentsResponse(
            SegmentIds: newSegments.Select(s => s.Id.ToString()).ToList(),
            ContextVersion: session.ContextVersion);

        // Spec §4.4: Append + Idempotency-Snapshot atomar in DERSELBEN Transaktion — ein Replay
        // findet stets einen konsistenten Eintrag (kein Doppel-Append). Der gespeicherte Body wird
        // mit den App-JSON-Optionen (snake_case) serialisiert ⇒ byte-identisch zur 201-Antwort.
        await idempotency.StageRecordAsync(
            idempotencyKey,
            session.Id,
            StatusCodes.Status201Created,
            response,
            ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Created($"/v1/sessions/{session.Id}/segments", response);
    }
}
