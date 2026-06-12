using System.Text.Json;
using Ctxman.Api.Idempotency;
using Ctxman.Api.Promotion;
using Ctxman.Core;
using Ctxman.Core.Auth;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Endpoints;

/// <summary>
/// Frame-Endpunkte (Spec §2.5 / §4.3): <c>POST /v1/sessions/{sid}/frames</c>.
/// Gruppiert pro Ressource (CLAUDE.md). Der Tenant kommt ausschließlich aus dem aufgelösten
/// <see cref="ITenantContext"/> (Spec §10); der globale Query-Filter liefert für unbekannte
/// oder fremde Sessions null ⇒ 404 ohne Existenz-Leak.
/// </summary>
public static class FrameEndpoints
{
    // Spec §6: Event-Payload ist kanonisches snake_case-JSON.
    private static readonly JsonSerializerOptions EventPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IEndpointRouteBuilder MapFrameEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/sessions/{sid}/frames", PushFrameAsync)
            .WithMetadata(new ResourceAction("frame", null, "write")); // Spec §4.1
        app.MapDelete("/v1/sessions/{sid}/frames/{fid}", PopFrameAsync)
            .WithMetadata(new ResourceAction("frame", null, "delete")); // Spec §4.1
        return app;
    }

    /// <summary>
    /// Ermittelt den Stack-Tip: den offenen Frame (<see cref="FrameStatus.Open"/>), der nicht als
    /// <c>parent_frame_id</c> eines anderen offenen Frames referenziert wird. Wegen LIFO-Disziplin
    /// (Spec §2.5) ist dieser eindeutig. Gibt <c>null</c> zurück wenn kein offener Frame existiert
    /// (Root-Level). Subtasks 2 und 3 nutzen dieselbe Logik.
    /// </summary>
    internal static Ulid? FindTipFrame(IReadOnlyList<Frame> frames)
    {
        // Spec §2.5: LIFO. Der Tip ist der offene Frame, der nicht als parent_frame_id eines
        // anderen offenen Frames auftaucht.
        var openFrames = frames.Where(f => f.Status == FrameStatus.Open).ToList();
        if (openFrames.Count == 0)
        {
            return null;
        }

        var referencedAsParent = openFrames
            .Where(f => f.ParentFrameId.HasValue)
            .Select(f => f.ParentFrameId!.Value)
            .ToHashSet();

        var tip = openFrames.FirstOrDefault(f => !referencedAsParent.Contains(f.Id));
        return tip?.Id;
    }

    private static async Task<IResult> PushFrameAsync(
        string sid,
        PushFrameRequest request,
        HttpRequest httpRequest,
        CtxmanDbContext db,
        ITenantContext tenant,
        IdempotencyService idempotency,
        CancellationToken ct)
    {
        // Spec §4.4: Idempotency-Key ist auf POST frames Pflicht. Fehlend/leer ⇒ 400 vor jedem Write.
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var idemHeader)
            || string.IsNullOrWhiteSpace(idemHeader.ToString()))
        {
            return Results.BadRequest(new { error = "The Idempotency-Key header is required (Spec §4.4)." });
        }

        var idempotencyKey = idemHeader.ToString();

        // Spec §4.4: wiederholter Key ⇒ identische Antwort, kein Doppel-Push. Replay vor dem Write.
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
        // Frames mit einladen (werden für Tip-Berechnung benötigt).
        var session = await db.Sessions
            .Include(s => s.Frames)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        // Spec §2.5: parent_frame_id = Stack-Tip (topmost open frame) oder null (= Root).
        var parentFrameId = FindTipFrame(session.Frames);

        var frameId = Ulid.NewUlid();
        var label = request.Label ?? string.Empty;

        var frame = new Frame(
            id: frameId,
            sessionId: session.Id,
            tenantId: tenant.TenantId, // Spec §10: nur aus dem TenantContext, nie aus dem Body.
            parentFrameId: parentFrameId,
            label: label,
            openedTurn: session.CurrentTurn); // Spec §2.5

        session.AddFrame(frame);

        // Spec §6: Event-seq ist pro Session monoton (Outbox-Cursor, §4.3 after_seq).
        var maxEventSeq = await db.Events
            .Where(ev => ev.SessionId == sessionId)
            .Select(ev => (long?)ev.Seq)
            .MaxAsync(ct);
        var nextEventSeq = (maxEventSeq ?? -1) + 1;

        var now = DateTimeOffset.UtcNow;

        // Spec §6: jede Mutation emittiert ein Event (Outbox, §7/§10 append-only).
        var payload = JsonSerializer.Serialize(
            new
            {
                frame_id = frame.Id.ToString(),
                parent_frame_id = frame.ParentFrameId?.ToString(),
                label = frame.Label,
                opened_turn = frame.OpenedTurn,
            },
            EventPayloadOptions);

        var eventRecord = new EventRecord
        {
            Id = Ulid.NewUlid(),
            TenantId = tenant.TenantId,
            SessionId = session.Id,
            Type = "frame_pushed",
            Payload = payload,
            Seq = nextEventSeq,
            CreatedAt = now,
        };

        // Spec §4.4: frame + Version-Increment + Event atomar in einer Transaktion.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Frames.Add(frame);
        db.Events.Add(eventRecord);

        // Spec §4.4: context_version genau EINMAL pro Request erhöhen.
        session.IncrementVersion(now);

        var response = new PushFrameResponse(FrameId: frame.Id.ToString());

        // Spec §4.4: Push + Idempotency-Snapshot atomar in DERSELBEN Transaktion.
        await idempotency.StageRecordAsync(
            idempotencyKey,
            session.Id,
            StatusCodes.Status201Created,
            response,
            ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Created($"/v1/sessions/{session.Id}/frames/{frame.Id}", response);
    }

    private static async Task<IResult> PopFrameAsync(
        string sid,
        string fid,
        [FromBody] PopFrameRequest request,
        HttpRequest httpRequest,
        CtxmanDbContext db,
        ITenantContext tenant,
        IdempotencyService idempotency,
        PromotionService promotionService,
        CancellationToken ct)
    {
        // Spec §4.4: Idempotency-Key ist auf DELETE frames Pflicht. Fehlend/leer ⇒ 400 vor jedem Write.
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var idemHeader)
            || string.IsNullOrWhiteSpace(idemHeader.ToString()))
        {
            return Results.BadRequest(new { error = "The Idempotency-Key header is required (Spec §4.4)." });
        }

        var idempotencyKey = idemHeader.ToString();

        // Spec §4.4: wiederholter Key ⇒ identische Antwort, kein Doppel-Pop. Replay vor dem Write.
        var replay = await idempotency.TryReplayAsync(idempotencyKey, ct);
        if (replay is not null)
        {
            return replay;
        }

        if (!Ulid.TryParse(sid, out var sessionId) || !Ulid.TryParse(fid, out var frameId))
        {
            // Ungültige IDs können nichts adressieren — wie unbekannt behandeln (kein Leak).
            return Results.NotFound();
        }

        // Spec §10: globaler Query-Filter ⇒ unbekannte ODER fremde Session liefert null ⇒ 404.
        // Frames mit einladen (werden für LIFO-Prüfung benötigt).
        var session = await db.Sessions
            .Include(s => s.Frames)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        // Spec §2.5: Frame im Frames-Stack der Session suchen.
        var frame = session.Frames.FirstOrDefault(f => f.Id == frameId);
        if (frame is null)
        {
            return Results.NotFound();
        }

        // Spec §2.5 LIFO: wenn irgendein anderer OPEN Frame als Parent diesen Frame hat,
        // gibt es offene Kinder — Pop verboten ⇒ 409.
        var hasOpenChildren = session.Frames
            .Any(f => f.Status == FrameStatus.Open && f.ParentFrameId == frameId);
        if (hasOpenChildren)
        {
            return Results.Json(
                new { error = "Frame has open child frames; pop is only allowed on the stack tip (Spec §2.5 LIFO)." },
                statusCode: StatusCodes.Status409Conflict);
        }

        // Spec §3.3: Segmente des Frames für Promotion-Eligible-Prüfung laden (live/externalized
        // Working-Segmente des zu poppenden Frames).
        var frameSegments = await db.Segments
            .Where(s => s.SessionId == sessionId && s.FrameId == frameId)
            .OrderBy(s => s.Seq)
            .ToListAsync(ct);

        // Promotion-Eligible = Working-Segmente mit State Live oder Externalized (Spec §3.3).
        var promotionCandidates = frameSegments
            .Where(s => s.Region == Region.Working
                && (s.State == SegmentState.Live || s.State == SegmentState.Externalized))
            .ToList();

        // Spec §3.3: LLM-Call AUSSERHALB der DB-Transaktion (keine langen Locks während Netzwerk-I/O).
        var policy = session.Policy;
        var pendingPromotions = await promotionService.ExtractAndSinkAsync(
            promotionCandidates,
            policy,
            sessionId,
            tenant.TenantId,
            session.CurrentTurn,
            ct);

        // MAX(seq) für neue Segmente + Events bestimmen.
        var maxSegSeq = await db.Segments
            .Where(s => s.SessionId == sessionId)
            .Select(s => (long?)s.Seq)
            .MaxAsync(ct);
        var nextSegSeq = (maxSegSeq ?? -1) + 1;

        var maxEventSeq = await db.Events
            .Where(ev => ev.SessionId == sessionId)
            .Select(ev => (long?)ev.Seq)
            .MaxAsync(ct);
        var nextEventSeq = (maxEventSeq ?? -1) + 1;

        var now = DateTimeOffset.UtcNow;

        // Spec §2.5 pop, §3.3: alles atomar in EINER Transaktion.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1) Alle Segmente des Frames evicten (Spec §2.2 I3: Daten bleiben, nur State = evicted).
        foreach (var seg in frameSegments)
        {
            try
            {
                seg.Evict(); // Spec §2.2 I3
            }
            catch (StaticRegionImmutableException)
            {
                // Static-Segmente gehören nicht zu Frames — defensiv ignorieren.
            }
        }

        // 2) Return-Segment im Parent-Frame anlegen (Spec §2.5 pop).
        var returnKind = request.ReturnKind ?? "subagent_return";
        var returnSegment = Segment.CreateLive(
            id: Ulid.NewUlid(),
            sessionId: session.Id,
            tenantId: tenant.TenantId, // Spec §10: nur aus dem TenantContext.
            region: Region.Working,
            kind: returnKind,
            role: Role.Assistant, // Spec §2.5: subagent return is assistant output; role required for RenderPlanner.Coalesce.
            content: request.ReturnContent,
            seq: nextSegSeq,
            createdTurn: session.CurrentTurn,
            frameId: frame.ParentFrameId); // null = Root-Frame (Spec §2.5)

        db.Segments.Add(returnSegment);

        // 3) Frame als gepoppt markieren (Spec §2.5).
        frame.Pop();

        // 4) Events anhängen (Spec §6): fact_promoted ZUERST, dann frame_popped.
        var eventsToAdd = new List<EventRecord>();

        // fact_promoted-Events (von PromotionService, Spec §3.3).
        foreach (var pending in pendingPromotions)
        {
            eventsToAdd.Add(new EventRecord
            {
                Id = Ulid.NewUlid(),
                TenantId = pending.TenantId,
                SessionId = pending.SessionId,
                Type = pending.Type, // "fact_promoted"
                Payload = pending.Payload,
                Seq = nextEventSeq++,
                CreatedAt = now,
            });
        }

        // frame_popped-Event (Spec §2.5 pop / §6).
        var poppedPayload = JsonSerializer.Serialize(
            new
            {
                frame_id = frame.Id.ToString(),
                return_segment_id = returnSegment.Id.ToString(),
            },
            EventPayloadOptions);

        eventsToAdd.Add(new EventRecord
        {
            Id = Ulid.NewUlid(),
            TenantId = tenant.TenantId,
            SessionId = session.Id,
            Type = "frame_popped",
            Payload = poppedPayload,
            Seq = nextEventSeq++,
            CreatedAt = now,
        });

        db.Events.AddRange(eventsToAdd);

        // 5) context_version erhöhen (Spec §4.4: genau EINMAL pro Request).
        session.IncrementVersion(now);

        var response = new PopFrameResponse(
            ReturnSegmentId: returnSegment.Id.ToString(),
            ContextVersion: session.ContextVersion);

        // Spec §4.4: Pop + Idempotency-Snapshot atomar in DERSELBEN Transaktion.
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
}
