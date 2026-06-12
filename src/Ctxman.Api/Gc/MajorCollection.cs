using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ctxman.Core;
using Ctxman.Core.Compaction;
using Ctxman.Core.Domain;
using Ctxman.Core.Gc;
using Ctxman.Core.Persistence;
using Ctxman.Core.Promotion;
using Ctxman.Core.Tokenization;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Gc;

/// <summary>
/// Dienst für die Major Collection einer Session (Spec §3.3: Promotion vor Compaction).
///
/// Diese Klasse übernimmt denselben DI-Scope-Pattern wie <see cref="MinorGcWorker"/>:
/// Tenant-ID wird gesetzt BEVOR der <see cref="CtxmanDbContext"/> aufgelöst wird, damit der
/// Global Query Filter (§10) korrekt greift.
///
/// Zustandslos und als Transient registriert — pro Job-Dispatch eine Instanz.
/// </summary>
public sealed class MajorCollection
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICompactionModel _compactionModel;
    private readonly IPromotionSink _promotionSink;
    private readonly ITokenCounter _tokenCounter;
    private readonly ILogger<MajorCollection> _logger;

    // Spec §6: Event-Payload ist kanonisches snake_case-JSON (gleiche Optionen wie die Endpunkte).
    private static readonly JsonSerializerOptions EventPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public MajorCollection(
        IServiceScopeFactory scopeFactory,
        ICompactionModel compactionModel,
        IPromotionSink promotionSink,
        ITokenCounter tokenCounter,
        ILogger<MajorCollection> logger)
    {
        _scopeFactory = scopeFactory;
        _compactionModel = compactionModel;
        _promotionSink = promotionSink;
        _tokenCounter = tokenCounter;
        _logger = logger;
    }

    /// <summary>
    /// Führt eine Major Collection für die angegebene Session aus.
    ///
    /// Tenant-Scope: <paramref name="tenantId"/> wird vor der DbContext-Auflösung in den
    /// scoped <see cref="TenantContext"/> geschrieben — gleicher Idiom wie
    /// <see cref="MinorGcWorker.RunMinorAsync"/>.
    ///
    /// Serialisierungs-Garantie (Spec §3.3 step 3 / §8):
    /// Alle GC-Läufe auf einer Session werden durch den gemeinsamen <see cref="SessionGcLocks"/>-
    /// Singleton serialisiert — niemals zwei parallele Collections auf derselben Session.
    /// Das Compaction-Fenster wird durch die Segment-IDs zum Planungszeitpunkt eingefroren
    /// (<c>frozenWindowIds</c>); Segmente, die danach angehängt werden, erhalten eine strikt
    /// höhere <c>seq</c> / gehören zu einer späteren <c>context_version</c> und sind nie
    /// Mitglieder des eingefrorenen Fensters. Daher entsteht kein Schreib-Konflikt für den
    /// Normalfall (Session fügt weiter Segmente an, während GC das alte Fenster kompaktiert).
    /// </summary>
    /// <param name="tenantId">Tenant der Session (aus dem Job-Kontext, nie aus dem Body).</param>
    /// <param name="sessionId">ID der zu kompaktierenden Session.</param>
    /// <param name="ct">Abbruch-Token.</param>
    public async Task ExecuteAsync(string tenantId, Ulid sessionId, CancellationToken ct)
    {
        _logger.LogDebug(
            "Major collection start for session {SessionId} (tenant {TenantId}).",
            sessionId, tenantId);

        using var scope = _scopeFactory.CreateScope();

        // Tenant SETZEN, bevor der DbContext aufgelöst wird (Spec §10 Global Query Filter).
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.TenantId = tenantId;

        var db = scope.ServiceProvider.GetRequiredService<CtxmanDbContext>();

        // Spec §3.3 step 3: Laden + Version snapshot einfrieren BEVOR der LLM-Call.
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            _logger.LogDebug(
                "Major collection: session {SessionId} not found or foreign tenant — skipping.",
                sessionId);
            return;
        }

        var segments = await db.Segments
            .Where(s => s.SessionId == sessionId)
            .ToListAsync(ct);

        var policy = session.Policy;

        // Spec §3.3: Compaction-Fenster berechnen (I/O-frei, deterministisch).
        var plan = MajorCollector.PlanCompaction(segments, policy);

        if (plan.IsNoOp)
        {
            _logger.LogDebug(
                "Major collection: session {SessionId} — no-op (fewer than 2 eligible segments).",
                sessionId);
            return; // Keine Events, keine Transaktionen.
        }

        // Spec §3.3 step 3: Version isolation — IDs des Fensters NOW einfrieren.
        // Alle ab hier anfallenden neuen Segmente sind nie Teil des Plans.
        var frozenWindowIds = plan.SourceSegments.Select(s => s.Id).ToList();
        var currentTurn = session.CurrentTurn;

        // Spec §3.3: die Inhalte aus dem Fenster für den LLM-Aufruf aufbereiten.
        var windowItems = plan.SourceSegments
            .Select(s => new WindowItem(s.Content ?? s.Summary ?? string.Empty, s.Kind))
            .ToList();

        // ── Step 1: PROMOTION (Spec §3.3 step 1) ──────────────────────────────────────────────
        // Spec §3.3: Promotion BEFORE Compaction — compaction is lossy, promotion runs first.
        // Fact-Extraction via SummarizeAsync mit separatem Promotion-Template.
        var promotionTemplateId = "fact-extraction-v1";
        var promotionRequest = new CompactionRequest(windowItems, promotionTemplateId, policy.Compaction.Model);
        var promotionResult = await _compactionModel.SummarizeAsync(promotionRequest, ct);

        // Sink-URL aus der Policy (Spec §5 PromotionConfig).
        var sinkUrl = policy.Promotion.Sink.Url ?? string.Empty;

        var promotionEvents = new List<(Ulid SegmentId, string SinkUrl, string PayloadDigest)>();

        if (!string.IsNullOrEmpty(promotionResult.Summary))
        {
            // Spec §3.3 AC6: Source-Segmente werden durch Promotion NICHT verändert.
            // Oldest source segment id als Herkunfts-Referenz.
            var oldestSourceId = plan.SourceSegments[0].Id;
            var oldestSourceKind = plan.SourceSegments[0].Kind;

            var fact = new PromotedFact(
                Fact: promotionResult.Summary,
                SourceSession: sessionId.ToString(),
                SourceTurn: currentTurn,
                Kind: oldestSourceKind);

            await _promotionSink.WriteAsync(fact, sinkUrl, ct);

            // Payload-Digest: SHA-256 über das kanonische JSON der Sink-Payload (Audit ohne Inhalt).
            var sinkPayloadJson = JsonSerializer.Serialize(fact, EventPayloadOptions);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sinkPayloadJson)))
                .ToLowerInvariant();

            promotionEvents.Add((oldestSourceId, sinkUrl, digest));
        }
        else
        {
            // Spec §3.3: Two distinct cases — both are safe w.r.t. fact-loss:
            //   (a) SummarizeAsync THREW → exception propagated out of this method above;
            //       no compaction or soft-delete of any source segment ever runs. Fact-loss impossible.
            //   (b) SummarizeAsync returned successfully but with an EMPTY summary → the window
            //       contained no durable facts worth promoting. Nothing to write to the sink;
            //       compaction proceeds normally below.
            _logger.LogDebug(
                "Major collection: session {SessionId} — promotion extraction returned empty; skipping fact write.",
                sessionId);
        }

        // ── Step 2: COMPACTION (Spec §3.3 step 2) ─────────────────────────────────────────────
        // LLM-Calls laufen AUSSERHALB der Transaktion (keine langen Locks während Netzwerk-I/O).
        var compactionRequest = new CompactionRequest(windowItems, policy.Compaction.PromptTemplateId, policy.Compaction.Model);
        var compactionResult = await _compactionModel.SummarizeAsync(compactionRequest, ct);

        var summaryContent = compactionResult.Summary;
        var summaryTokens = _tokenCounter.Count(summaryContent);

        // ── Step 3: ATOMARE DB-TRANSAKTION (Spec §3.3 step 3) ─────────────────────────────────
        // Alle Schreiboperationen (neues Summary-Segment, Source-Segmente → compacted, Events)
        // in EINER Transaktion — gleicher Idiom wie MinorGcWorker.
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var now = DateTimeOffset.UtcNow;

            // Spec §6: Event-seq ist pro Session monoton (Outbox-Cursor) — MAX(seq)+1.
            var maxEventSeq = await db.Events
                .Where(ev => ev.SessionId == sessionId)
                .Select(ev => (long?)ev.Seq)
                .MaxAsync(ct);
            var nextEventSeq = (maxEventSeq ?? -1) + 1;

            var events = new List<EventRecord>();

            // fact_promoted Events (Spec §3.3 / §6).
            foreach (var (segId, sink, digest) in promotionEvents)
            {
                events.Add(CreateEvent(tenantId, sessionId, "fact_promoted", new
                {
                    segment_id = segId.ToString(),
                    sink,
                    payload_digest = digest,
                }, nextEventSeq++, now));
            }

            // compaction_started Event (Spec §3.3 / §6) — vor der Summary-Segment-Anlage.
            events.Add(CreateEvent(tenantId, sessionId, "compaction_started", new
            {
                source_ids = frozenWindowIds.Select(id => id.ToString()).ToArray(),
            }, nextEventSeq++, now));

            // Spec §3.3: neues compaction_summary-Segment anlegen (Working-Region, kind = "compaction_summary").
            // seq = älteste Source-Seq → chronologische Kontinuität (Spec §3.3).
            var summarySegmentId = Ulid.NewUlid();
            var summarySegment = Segment.CreateLive(
                id: summarySegmentId,
                sessionId: sessionId,
                tenantId: tenantId,
                region: Region.Working,
                kind: "compaction_summary",
                role: null,
                content: summaryContent,
                seq: plan.OldestSeq,
                createdTurn: currentTurn,
                tokens: summaryTokens);

            db.Segments.Add(summarySegment);

            // Spec §3.3: Source-Segmente → compacted (Soft-Delete, I3).
            // Nur die eingefrorenen Fenster-Segmente werden angefasst — keine neu angefügten.
            foreach (var sourceSegment in plan.SourceSegments)
            {
                sourceSegment.Compact(); // Spec §2.2 I3 / §3.3
            }

            // compaction_completed Event (Spec §3.3 / §6).
            events.Add(CreateEvent(tenantId, sessionId, "compaction_completed", new
            {
                source_ids = frozenWindowIds.Select(id => id.ToString()).ToArray(),
                summary_id = summarySegmentId.ToString(),
                tokens_before = plan.TokensBefore,
                tokens_after = summaryTokens,
            }, nextEventSeq++, now));

            db.Events.AddRange(events);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Major collection completed for session {SessionId}: {SourceCount} segments → 1 summary " +
                "(tokens {TokensBefore} → {TokensAfter}).",
                sessionId, frozenWindowIds.Count, plan.TokensBefore, summaryTokens);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Defense-in-depth: unreachable until an optimistic-concurrency token is added (WP7); harmless guard.
            // The actual serialization guarantee today is provided by SessionGcLocks (see ExecuteAsync doc comment).
            _logger.LogWarning(ex,
                "Major collection: concurrency conflict for session {SessionId} — discarding run.",
                sessionId);
        }
    }

    private static EventRecord CreateEvent(
        string tenantId, Ulid sessionId, string type, object payload, long seq, DateTimeOffset now) =>
        new()
        {
            Id = Ulid.NewUlid(),
            TenantId = tenantId,
            SessionId = sessionId,
            Type = type,
            Payload = JsonSerializer.Serialize(payload, EventPayloadOptions),
            Seq = seq,
            CreatedAt = now,
        };
}
