using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ctxman.Core.Compaction;
using Ctxman.Core.Domain;
using Ctxman.Core.Promotion;

namespace Ctxman.Api.Promotion;

/// <summary>
/// Scoped service that extracts facts from a segment window and writes them to the configured
/// promotion sink (Spec §3.3 step 1: Promotion before Compaction).
///
/// Returns a list of <see cref="PendingPromotionEvent"/> — lightweight records that carry the
/// event type and serialized payload but have NO seq assigned. The calling endpoint assigns
/// <c>seq = MAX+1</c> within its own DB transaction (so that <c>fact_promoted</c> events are
/// ordered before any frame or archive events in the same commit).
///
/// The LLM call (<see cref="ICompactionModel.SummarizeAsync"/>) runs here, intentionally
/// OUTSIDE any DB transaction — long network I/O must not hold DB locks (Spec §3.3 step 3).
/// </summary>
public sealed class PromotionService
{
    private readonly ICompactionModel _compactionModel;
    private readonly IPromotionSink _promotionSink;
    private readonly ILogger<PromotionService> _logger;

    // Spec §6: Event-Payload ist kanonisches snake_case-JSON (gleiche Optionen wie die Endpunkte).
    private static readonly JsonSerializerOptions EventPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const string FactExtractionTemplateId = "fact-extraction-v1";

    public PromotionService(
        ICompactionModel compactionModel,
        IPromotionSink promotionSink,
        ILogger<PromotionService> logger)
    {
        _compactionModel = compactionModel;
        _promotionSink = promotionSink;
        _logger = logger;
    }

    /// <summary>
    /// Runs fact-extraction on <paramref name="segments"/> and writes to the promotion sink.
    /// Returns one <see cref="PendingPromotionEvent"/> per promoted fact (zero or one in
    /// practice), with no <c>seq</c> assigned — the caller must assign monotone seq before
    /// committing to the DB.
    /// </summary>
    /// <param name="segments">The segments to promote (caller-selected window).</param>
    /// <param name="policy">Session policy (provides compaction model name and sink URL).</param>
    /// <param name="sessionId">Session ID (written into the <c>fact_promoted</c> payload).</param>
    /// <param name="tenantId">Tenant ID (written into the <c>fact_promoted</c> payload).</param>
    /// <param name="currentTurn">Current session turn (written into the promoted fact).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Zero or more pending events to be appended by the caller — empty when the extraction
    /// returned an empty summary (no durable facts in the window, Spec §3.3).
    /// </returns>
    public async Task<IReadOnlyList<PendingPromotionEvent>> ExtractAndSinkAsync(
        IReadOnlyList<Segment> segments,
        PolicyConfig policy,
        Ulid sessionId,
        string tenantId,
        int currentTurn,
        CancellationToken ct)
    {
        if (segments.Count == 0)
        {
            return [];
        }

        // Build the window for the LLM — mirror MajorCollection.windowItems construction exactly.
        var windowItems = segments
            .Select(s => new WindowItem(s.Content ?? s.Summary ?? string.Empty, s.Kind))
            .ToList();

        var promotionRequest = new CompactionRequest(
            windowItems,
            FactExtractionTemplateId,
            policy.Compaction.Model);

        // LLM call — intentionally outside any DB transaction (Spec §3.3 step 3).
        var promotionResult = await _compactionModel.SummarizeAsync(promotionRequest, ct);

        if (string.IsNullOrEmpty(promotionResult.Summary))
        {
            // Spec §3.3: empty summary means no durable facts — no sink write, no events.
            _logger.LogDebug(
                "PromotionService: session {SessionId} — fact-extraction returned empty; no promotion.",
                sessionId);
            return [];
        }

        // Oldest segment is the canonical source reference (mirrors MajorCollection).
        var oldestSegment = segments[0];

        var fact = new PromotedFact(
            Fact: promotionResult.Summary,
            SourceSession: sessionId.ToString(),
            SourceTurn: currentTurn,
            Kind: oldestSegment.Kind);

        var sinkUrl = policy.Promotion.Sink.Url ?? string.Empty;

        await _promotionSink.WriteAsync(fact, sinkUrl, ct);

        // Payload-Digest: SHA-256 over canonical JSON of the sink payload (audit without content).
        // Mirrors MajorCollection digest computation exactly.
        var sinkPayloadJson = JsonSerializer.Serialize(fact, EventPayloadOptions);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sinkPayloadJson)))
            .ToLowerInvariant();

        // Return a PendingPromotionEvent — caller assigns seq and CreatedAt before DB commit.
        var pendingPayload = JsonSerializer.Serialize(new
        {
            segment_id = oldestSegment.Id.ToString(),
            sink = sinkUrl,
            payload_digest = digest,
        }, EventPayloadOptions);

        return [new PendingPromotionEvent("fact_promoted", pendingPayload, tenantId, sessionId)];
    }
}

/// <summary>
/// Lightweight intermediate returned by <see cref="PromotionService.ExtractAndSinkAsync"/>.
/// The caller converts this into an <see cref="Core.Persistence.EventRecord"/> with the
/// correct monotone <c>seq</c> value within its own DB transaction.
///
/// Pattern: the endpoint computes <c>seq = MAX+1</c>, creates the <see cref="Core.Persistence.EventRecord"/>
/// inline, and appends <c>fact_promoted</c> events BEFORE any subsequent events (e.g.
/// <c>frame_popped</c>, <c>session_archived</c>) to preserve logical ordering.
/// </summary>
/// <param name="Type">Event type string, e.g. <c>fact_promoted</c>.</param>
/// <param name="Payload">Already-serialized snake_case JSON payload string.</param>
/// <param name="TenantId">Tenant ID for the EventRecord row.</param>
/// <param name="SessionId">Session ID for the EventRecord row.</param>
public sealed record PendingPromotionEvent(
    string Type,
    string Payload,
    string TenantId,
    Ulid SessionId);
