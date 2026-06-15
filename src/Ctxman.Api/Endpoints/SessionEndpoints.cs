using System.Text;
using System.Text.Json;
using Ctxman.Api.Idempotency;
using Ctxman.Api.Notifications;
using Ctxman.Api.Promotion;
using Ctxman.Api.Storage;
using Ctxman.Core;
using Ctxman.Core.Auth;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Core.Tokenization;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Endpoints;

/// <summary>
/// Session-Endpunkte (Spec §4.3): <c>POST /v1/sessions</c> und <c>GET /v1/sessions/{sid}</c>.
/// Gruppiert pro Ressource (CLAUDE.md). Der Tenant kommt ausschließlich aus dem aufgelösten
/// <see cref="ITenantContext"/> (Spec §4.1 / §10); der globale Query-Filter des
/// <see cref="CtxmanDbContext"/> erzwingt die Isolation — unbekannte oder fremde Sessions
/// liefern 404 ohne Existenz-Leak.
/// </summary>
public static class SessionEndpoints
{
    // Spec §6: SSE-Payloads sind kanonisches snake_case-JSON (wie die Outbox-Events).
    private static readonly JsonSerializerOptions SseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/sessions", CreateSessionAsync)
            .WithMetadata(new ResourceAction("session", null, "write")); // Spec §4.1
        // Literale Pfade — höhere Routing-Präzedenz als /v1/sessions/{sid}, daher kein Konflikt
        // (ein ULID ist nie "events"). Discovery-Snapshot bzw. Live-Stream über alle Sessions.
        app.MapGet("/v1/sessions", ListSessionsAsync)
            .WithMetadata(new ResourceAction("session", null, "read")); // Spec §4.1
        app.MapGet("/v1/sessions/events", StreamSessionEventsAsync)
            .WithMetadata(new ResourceAction("session", null, "read")); // Spec §4.1
        app.MapGet("/v1/sessions/{sid}", GetSessionAsync)
            .WithMetadata(new ResourceAction("session", null, "read")); // Spec §4.1
        app.MapPost("/v1/sessions/{sid}/archive", ArchiveSessionAsync)
            .WithMetadata(new ResourceAction("session", null, "archive")); // Spec §4.1
        return app;
    }

    private static async Task<IResult> CreateSessionAsync(
        CreateSessionRequest request,
        HttpRequest httpRequest,
        CtxmanDbContext db,
        ITenantContext tenant,
        ITokenCounter tokenCounter,
        IdempotencyService idempotency,
        SessionNotificationHub notifications,
        CancellationToken ct)
    {
        // Spec §4.4: auf POST /sessions ist der Idempotency-Key OPTIONAL. Ist er gesetzt, gilt die
        // gleiche Replay-/Store-Logik wie auf den mutierenden Endpunkten; fehlt er, läuft der
        // Handler wie bisher (kein 400).
        string? idempotencyKey = null;
        if (httpRequest.Headers.TryGetValue("Idempotency-Key", out var idemHeader)
            && !string.IsNullOrWhiteSpace(idemHeader.ToString()))
        {
            idempotencyKey = idemHeader.ToString();

            var replay = await idempotency.TryReplayAsync(idempotencyKey, ct);
            if (replay is not null)
            {
                return replay;
            }
        }

        // Spec §5: effektive Policy = Default + Overrides, beim Anlegen validiert und als
        // Snapshot eingefroren (Reproduzierbarkeit).
        if (!TryBuildPolicy(request.PolicyOverrides, out var policy, out var error))
        {
            return Results.BadRequest(new { error });
        }

        var now = DateTimeOffset.UtcNow;
        var session = new Session(
            id: Ulid.NewUlid(),
            tenantId: tenant.TenantId, // Spec §10: nur aus dem TenantContext, nie aus dem Body.
            agentTemplateId: request.AgentTemplateId,
            policy: policy,
            contextVersion: 0,
            staticEpoch: 0, // Spec §4.2 / §4.3: Static-Region wird hier initial gesetzt (Epoche 0).
            currentTurn: 0,
            status: SessionStatus.Active,
            createdAt: now,
            updatedAt: now);

        // Spec §4.3: jeder static_segments-Eintrag wird zu einem Static-Segment mit
        // server-vergebener seq ab 0, static_epoch=0, Tokens via ITokenCounter gezählt.
        long seq = 0;
        var segments = new List<Segment>();
        foreach (var entry in request.StaticSegments ?? Array.Empty<StaticSegmentDto>())
        {
            if (!TryParseRole(entry.Role, out var role, out var roleError))
            {
                return Results.BadRequest(new { error = roleError });
            }

            segments.Add(Segment.CreateLive(
                id: Ulid.NewUlid(),
                sessionId: session.Id,
                tenantId: tenant.TenantId,
                region: Region.Static,
                kind: entry.Kind,
                role: role,
                content: entry.Content,
                seq: seq++,
                createdTurn: 0,
                source: entry.Source,
                tokens: tokenCounter.Count(entry.Content)));
        }

        // Spec §4.3: Session + Segmente in einem SaveChanges persistieren.
        db.Sessions.Add(session);
        db.Segments.AddRange(segments);

        var response = new CreateSessionResponse(session.Id.ToString(), session.ContextVersion);

        // Spec §4.4: bei gesetztem Key den Response-Snapshot im selben SaveChanges persistieren —
        // ein Replay liefert die byte-identische 201-Antwort, ohne erneut anzulegen.
        if (idempotencyKey is not null)
        {
            await idempotency.StageRecordAsync(
                idempotencyKey,
                session.Id,
                StatusCodes.Status201Created,
                response,
                ct);
        }

        await db.SaveChangesAsync(ct);

        // Live-Discovery: nach erfolgreichem Commit an verbundene SSE-Clients pushen. Ein
        // Idempotency-Replay kehrt oben früh zurück und publiziert daher nicht erneut.
        notifications.Publish(new SessionNotification(
            "session_created", tenant.TenantId, session.Id.ToString(), now));

        return Results.Created($"/v1/sessions/{session.Id}", response);
    }

    /// <summary>
    /// <c>GET /v1/sessions</c>: alle Sessions des aufgelösten Tenants (Discovery-Snapshot),
    /// neueste zuerst, mit Budget-Status wie im Detail-Endpunkt. Der globale Query-Filter
    /// (Spec §10) erzwingt die Tenant-Isolation; fremde Sessions erscheinen nie.
    /// </summary>
    private static async Task<IResult> ListSessionsAsync(
        CtxmanDbContext db,
        CancellationToken ct)
    {
        // Sortierung client-seitig: SQLite (Test-Provider) kann nicht nach DateTimeOffset in ORDER BY
        // sortieren; bei der überschaubaren Session-Zahl pro Tenant ist In-Memory-Sort unkritisch.
        var sessions = await db.Sessions
            .AsNoTracking()
            .ToListAsync(ct);
        sessions = sessions.OrderByDescending(s => s.CreatedAt).ToList();

        if (sessions.Count == 0)
        {
            return Results.Ok(new SessionListResponse(Array.Empty<SessionDetailResponse>()));
        }

        // tokens_used je Session in EINER gruppierten Query (kein N+1). render-eligible =
        // live | externalized (I3), gespiegelt aus GetSessionAsync.
        var ids = sessions.Select(s => s.Id).ToList();
        var tokensBySession = await db.Segments
            .AsNoTracking()
            .Where(s => ids.Contains(s.SessionId)
                && (s.State == SegmentState.Live || s.State == SegmentState.Externalized))
            .GroupBy(s => s.SessionId)
            .Select(g => new { SessionId = g.Key, Tokens = g.Sum(x => x.Tokens) })
            .ToDictionaryAsync(x => x.SessionId, x => x.Tokens, ct);

        var summaries = sessions.Select(session =>
        {
            var tokensUsed = tokensBySession.TryGetValue(session.Id, out var t) ? t : 0;
            return new SessionDetailResponse(
                SessionId: session.Id.ToString(),
                AgentTemplateId: session.AgentTemplateId,
                Status: session.Status.ToWire(),
                ContextVersion: session.ContextVersion,
                StaticEpoch: session.StaticEpoch,
                CurrentTurn: session.CurrentTurn,
                TokensUsed: tokensUsed,
                WatermarkState: WatermarkState.Derive(tokensUsed, session.Policy),
                CreatedAt: session.CreatedAt,
                UpdatedAt: session.UpdatedAt);
        }).ToList();

        return Results.Ok(new SessionListResponse(summaries));
    }

    /// <summary>
    /// <c>GET /v1/sessions/events</c> (SSE): langlebiger Stream der Session-Lifecycle-Ereignisse
    /// des aufgelösten Tenants. Sendet zuerst einen <c>snapshot</c>-Frame (alle aktuellen
    /// Session-IDs — schließt das Fenster zwischen Snapshot-Read und Stream-Connect), dann live
    /// <c>session_created</c>/<c>session_archived</c> aus dem <see cref="SessionNotificationHub"/>.
    /// Heartbeat-Kommentare halten die Verbindung offen; Abbruch über <c>RequestAborted</c>.
    /// </summary>
    private static IResult StreamSessionEventsAsync(
        CtxmanDbContext db,
        ITenantContext tenant,
        SessionNotificationHub notifications,
        CancellationToken ct)
    {
        return new SessionEventStreamResult(db, tenant.TenantId, notifications);
    }

    private static async Task<IResult> GetSessionAsync(
        string sid,
        CtxmanDbContext db,
        CancellationToken ct)
    {
        if (!Ulid.TryParse(sid, out var sessionId))
        {
            // Ungültige ID kann keine Session adressieren — wie unbekannt behandeln (kein Leak).
            return Results.NotFound();
        }

        // Spec §10: globaler Query-Filter ⇒ unbekannte ODER fremde Session liefert null ⇒ 404.
        var session = await db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        // Spec §4.3: tokens_used = Summe der Tokens über render-fähige Segmente
        // (live | externalized, alle Regionen). compacted/evicted erscheinen nie im Render (I3).
        var tokensUsed = await db.Segments
            .AsNoTracking()
            .Where(s => s.SessionId == sessionId
                && (s.State == SegmentState.Live || s.State == SegmentState.Externalized))
            .SumAsync(s => s.Tokens, ct);

        var watermarkState = DeriveWatermarkState(tokensUsed, session.Policy);

        return Results.Ok(new SessionDetailResponse(
            SessionId: session.Id.ToString(),
            AgentTemplateId: session.AgentTemplateId,
            Status: session.Status.ToWire(),
            ContextVersion: session.ContextVersion,
            StaticEpoch: session.StaticEpoch,
            CurrentTurn: session.CurrentTurn,
            TokensUsed: tokensUsed,
            WatermarkState: watermarkState,
            CreatedAt: session.CreatedAt,
            UpdatedAt: session.UpdatedAt));
    }

    /// <summary>
    /// Archiviert eine Session (Spec §4.3): terminal promotion über alle verbleibenden
    /// Working-Segmente, dann Status → archived, Version increment, Idempotency-Snapshot.
    /// </summary>
    private static async Task<IResult> ArchiveSessionAsync(
        string sid,
        HttpRequest httpRequest,
        CtxmanDbContext db,
        ITenantContext tenant,
        IdempotencyService idempotency,
        PromotionService promotionService,
        RetentionConfig retention,
        IColdStorageExporter coldStorageExporter,
        SessionNotificationHub notifications,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Spec §4.4: Idempotency-Key ist auf state-mutierenden Endpunkten Pflicht. Fehlend/leer ⇒ 400.
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var idemHeader)
            || string.IsNullOrWhiteSpace(idemHeader.ToString()))
        {
            return Results.BadRequest(new { error = "The Idempotency-Key header is required (Spec §4.4)." });
        }

        var idempotencyKey = idemHeader.ToString();

        // Spec §4.4: wiederholter Key ⇒ identische Antwort, kein Doppel-Archivierung. Replay vor dem Write.
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
        var session = await db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        // Spec §3.3: Promotion-Eligible = live/externalized Working-Segmente (nicht pinned-exclusion
        // da die Selektionslogik die gleiche wie beim frame-pop ist).
        var promotionCandidates = await db.Segments
            .Where(s => s.SessionId == sessionId
                && s.Region == Region.Working
                && (s.State == SegmentState.Live || s.State == SegmentState.Externalized))
            .OrderBy(s => s.Seq)
            .ToListAsync(ct);

        // Spec §7.1: Blob-Keys aus den Promotion-Kandidaten ableiten (Working + Live/Externalized
        // deckt alle blob-referenzierenden Segmente ab, die beim Archivieren relevant sind).
        var blobKeys = promotionCandidates
            .Where(s => s.BlobRef != null)
            .Select(s => s.BlobRef!.Key)
            .Distinct()
            .ToList();

        // Spec §3.3: LLM-Call AUSSERHALB der DB-Transaktion (keine langen Locks während Netzwerk-I/O).
        IReadOnlyList<PendingPromotionEvent> pendingPromotions;
        try
        {
            pendingPromotions = await promotionService.ExtractAndSinkAsync(
                promotionCandidates,
                session.Policy,
                sessionId,
                tenant.TenantId,
                session.CurrentTurn,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Client-Abbruch — kein Fehlerfall des Servers.
        }
        catch (Exception ex)
        {
            // Spec-Lücke §2.5/§3.3, Operator-Entscheidung 2026-06-12: terminale Promotion ist
            // zwingend (§3.3) — bei Promotion-Fehler KEINE Archivierung, sondern definierter
            // retrybarer Fehler. Es wurde noch nichts mutiert (der LLM-/Sink-Call läuft vor der
            // Transaktion); Retry mit demselben Idempotency-Key ist sicher.
            loggerFactory.CreateLogger(typeof(SessionEndpoints).FullName!).LogError(ex,
                "Terminal promotion failed during session archive (session {SessionId}); returning 503.",
                sessionId);
            return Results.Json(
                new { error = "promotion_failed", retryable = true },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // MAX(seq) für Events bestimmen.
        var maxEventSeq = await db.Events
            .Where(ev => ev.SessionId == sessionId)
            .Select(ev => (long?)ev.Seq)
            .MaxAsync(ct);
        var nextEventSeq = (maxEventSeq ?? -1) + 1;

        var now = DateTimeOffset.UtcNow;

        // Spec §4.3: alles atomar in EINER Transaktion.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // fact_promoted-Events (von PromotionService, Spec §3.3).
        var eventsToAdd = new List<EventRecord>();
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

        db.Events.AddRange(eventsToAdd);

        // Spec §4.3: Status → archived, context_version erhöhen (Spec §4.4: genau EINMAL pro Request).
        session.Archive(now);
        session.IncrementVersion(now);

        // Spec §4.4: Archiv-Snapshot atomar in DERSELBEN Transaktion — 204 No Content hat keinen Body;
        // leeres Objekt als Platzhalter (der Replay-Pfad schreibt keinen sinnvollen Body bei 204).
        await idempotency.StageRecordAsync(
            idempotencyKey,
            session.Id,
            StatusCodes.Status204NoContent,
            new { },
            ct);

        // Spec §4.4: Concurrency-Konflikt ⇒ 409; Idempotency-Key-Race ⇒ Replay statt Doppel-Archiv.
        var conflict = await MutationCommit.TryCommitAsync(db, tx, idempotency, idempotencyKey, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        // Live-Discovery: Statuswechsel an verbundene SSE-Clients pushen (nach Commit; ein
        // Idempotency-Replay kehrt oben früh zurück und publiziert daher nicht erneut).
        notifications.Publish(new SessionNotification(
            "session_archived", tenant.TenantId, session.Id.ToString(), now));

        // Spec §7.1: Cold-Storage-Export nach erfolgreichem Commit (best-effort — Session ist
        // bereits archiviert; ein Export-Fehler darf kein 500 produzieren).
        if (retention.ArchivedSessionBlobs == "cold_storage" && blobKeys.Count > 0)
        {
            try
            {
                await coldStorageExporter.ExportSessionBlobsAsync(tenant.TenantId, session.Id, blobKeys, ct);
            }
            catch
            {
                // Spec §7.1: best-effort — Export-Fehler loggen ist optional; Archive bereits committed.
            }
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Legt die Overrides über <see cref="PolicyConfig.Default()"/> und validiert das Ergebnis
    /// (Spec §5). Nicht gesetzte Felder behalten den Default. Ungültig ⇒ false + Fehlertext (→ 400).
    /// </summary>
    private static bool TryBuildPolicy(
        PolicyOverridesDto? overrides,
        out PolicyConfig policy,
        out string error)
    {
        var defaults = PolicyConfig.Default();

        var budget = overrides?.BudgetTokens ?? defaults.BudgetTokens;
        var externalizeThreshold = overrides?.ExternalizeThresholdTokens ?? defaults.ExternalizeThresholdTokens;
        var tokenizer = overrides?.Tokenizer ?? defaults.Tokenizer;
        var onToolRemoved = overrides?.OnToolRemoved ?? defaults.OnToolRemoved;

        var watermarks = new Watermarks(
            Soft: overrides?.Watermarks?.Soft ?? defaults.Watermarks.Soft,
            Hard: overrides?.Watermarks?.Hard ?? defaults.Watermarks.Hard,
            Emergency: overrides?.Watermarks?.Emergency ?? defaults.Watermarks.Emergency);

        // Spec §5: compaction overrides — missing fields fall back to defaults.
        var compactionModel = overrides?.Compaction?.Model ?? defaults.Compaction.Model;
        var compactionTemplateId = overrides?.Compaction?.PromptTemplateId ?? defaults.Compaction.PromptTemplateId;
        var compactionMaxShare = overrides?.Compaction?.MaxShare ?? defaults.Compaction.MaxShare;
        var compaction = new CompactionConfig(compactionModel, compactionTemplateId, compactionMaxShare);

        // Spec §5: promotion/sink overrides — missing fields fall back to defaults.
        // When the caller explicitly supplies a sink block, the url must come from that override
        // (not from the default) so that a missing url is caught by validation (AC14).
        var overrideSink = overrides?.Promotion?.Sink;
        var sinkType = overrideSink?.Type ?? defaults.Promotion.Sink.Type;
        var sinkUrl = overrideSink is not null
            ? overrideSink.Url                         // null when caller omitted url → validation fails below
            : defaults.Promotion.Sink.Url;             // no sink override → keep default, skip validation
        var promotion = new PromotionConfig(new PromotionSink(sinkType, sinkUrl));

        policy = defaults with
        {
            BudgetTokens = budget,
            ExternalizeThresholdTokens = externalizeThreshold,
            Tokenizer = tokenizer,
            Watermarks = watermarks,
            OnToolRemoved = onToolRemoved,
            Compaction = compaction,
            Promotion = promotion,
        };

        // Spec §5/§3.1: Budget positiv; Watermarks Anteile in (0,1]; soft ≤ hard ≤ emergency.
        if (budget <= 0)
        {
            error = "budget_tokens must be greater than 0.";
            return false;
        }

        if (externalizeThreshold < 0)
        {
            error = "externalize_threshold_tokens must be greater than or equal to 0.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(tokenizer))
        {
            error = "tokenizer must not be empty.";
            return false;
        }

        if (!IsValidWatermark(watermarks.Soft) || !IsValidWatermark(watermarks.Hard) || !IsValidWatermark(watermarks.Emergency))
        {
            error = "watermarks must be fractions in the range (0, 1].";
            return false;
        }

        if (!(watermarks.Soft <= watermarks.Hard && watermarks.Hard <= watermarks.Emergency))
        {
            error = "watermarks must satisfy soft <= hard <= emergency.";
            return false;
        }

        // Spec §4.2: on_tool_removed ∈ { keep | externalize | evict }.
        if (onToolRemoved is not ("keep" or "externalize" or "evict"))
        {
            error = "on_tool_removed must be one of: keep, externalize, evict.";
            return false;
        }

        // Spec §5: compaction validation.
        if (string.IsNullOrWhiteSpace(compactionModel))
        {
            error = "compaction.model must not be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(compactionTemplateId))
        {
            error = "compaction.prompt_template_id must not be empty.";
            return false;
        }

        if (compactionMaxShare <= 0.0 || compactionMaxShare > 1.0)
        {
            error = "compaction.max_share must be in the range (0, 1].";
            return false;
        }

        // Spec §5: promotion sink validation.
        if (string.IsNullOrWhiteSpace(sinkType))
        {
            error = "promotion.sink.type must not be empty.";
            return false;
        }

        if (sinkType is not "webhook")
        {
            error = "promotion.sink.type must be one of: webhook.";
            return false;
        }

        // Spec §5 / AC14: only validate the webhook url when the caller explicitly supplied a
        // sink override; the default placeholder url is intentionally not re-validated here.
        if (sinkType == "webhook" && overrideSink is not null)
        {
            if (string.IsNullOrWhiteSpace(sinkUrl))
            {
                error = "promotion.sink.url is required when promotion.sink.type is 'webhook'.";
                return false;
            }

            if (!Uri.TryCreate(sinkUrl, UriKind.Absolute, out _))
            {
                error = "promotion.sink.url must be a well-formed absolute URI.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsValidWatermark(double value) => value > 0.0 && value <= 1.0;

    /// <summary>
    /// Leitet den Watermark-Status aus tokens_used relativ zum Budget ab (Spec §3.1). Delegiert an
    /// <see cref="WatermarkState.Derive"/> in Core, damit Render-Pipeline und Endpunkt dieselbe Logik teilen.
    /// </summary>
    private static string DeriveWatermarkState(int tokensUsed, PolicyConfig policy) =>
        WatermarkState.Derive(tokensUsed, policy);

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

    /// <summary>
    /// Langlebige SSE-Antwort für <c>GET /v1/sessions/events</c>: snapshot-Frame (aktuelle
    /// Session-IDs des Tenants) gefolgt von Live-Frames aus dem <see cref="SessionNotificationHub"/>,
    /// tenant-gefiltert. Heartbeat-Kommentare alle 15 s halten Proxys/Browser-Verbindung offen;
    /// Ende über <c>RequestAborted</c> (Client trennt) — dann sauberes Unsubscribe.
    /// </summary>
    private sealed class SessionEventStreamResult : IResult
    {
        private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

        private readonly CtxmanDbContext _db;
        private readonly string _tenantId;
        private readonly SessionNotificationHub _hub;

        public SessionEventStreamResult(CtxmanDbContext db, string tenantId, SessionNotificationHub hub)
        {
            _db = db;
            _tenantId = tenantId;
            _hub = hub;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no"; // Nginx: kein Buffering des Streams.

            var ct = httpContext.RequestAborted;

            // Erst subscriben, DANN den Snapshot lesen — so geht ein Create im Zeitfenster
            // dazwischen nicht verloren (der Client dedupliziert per session_id).
            var sub = _hub.Subscribe();
            try
            {
                // Sortierung client-seitig (SQLite kann kein ORDER BY DateTimeOffset; siehe ListSessions).
                var rows = await _db.Sessions
                    .AsNoTracking()
                    .Select(s => new { s.Id, s.CreatedAt })
                    .ToListAsync(ct);
                var ids = rows
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => r.Id.ToString())
                    .ToArray();

                var snapshot = JsonSerializer.Serialize(new { session_ids = ids }, SseOptions);
                await WriteFrameAsync(response, "snapshot", snapshot, ct);

                while (!ct.IsCancellationRequested)
                {
                    SessionNotification notification;
                    try
                    {
                        using var hb = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        hb.CancelAfter(Heartbeat);
                        notification = await sub.Reader.ReadAsync(hb.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Heartbeat-Tick ohne Notification — Kommentar-Frame, Verbindung offen halten.
                        await response.WriteAsync(": ping\n\n", ct);
                        await response.Body.FlushAsync(ct);
                        continue;
                    }

                    // Tenant-Isolation (Spec §10): nur Ereignisse des aufgelösten Tenants ausliefern.
                    if (notification.TenantId != _tenantId)
                    {
                        continue;
                    }

                    var data = JsonSerializer.Serialize(
                        new { session_id = notification.SessionId, at = notification.At },
                        SseOptions);
                    await WriteFrameAsync(response, notification.Type, data, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Client hat die Verbindung getrennt — regulärer Stream-Abschluss.
            }
            finally
            {
                _hub.Unsubscribe(sub.Id);
            }
        }

        private static async Task WriteFrameAsync(HttpResponse response, string eventType, string data, CancellationToken ct)
        {
            var frame = new StringBuilder()
                .Append("event: ").Append(eventType).Append('\n')
                .Append("data: ").Append(data).Append("\n\n")
                .ToString();
            await response.WriteAsync(frame, ct);
            await response.Body.FlushAsync(ct);
        }
    }
}
