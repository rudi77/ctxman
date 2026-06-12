using Ctxman.Api.Idempotency;
using Ctxman.Core;
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
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/sessions", CreateSessionAsync);
        app.MapGet("/v1/sessions/{sid}", GetSessionAsync);
        return app;
    }

    private static async Task<IResult> CreateSessionAsync(
        CreateSessionRequest request,
        HttpRequest httpRequest,
        CtxmanDbContext db,
        ITenantContext tenant,
        ITokenCounter tokenCounter,
        IdempotencyService idempotency,
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

        return Results.Created($"/v1/sessions/{session.Id}", response);
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
}
