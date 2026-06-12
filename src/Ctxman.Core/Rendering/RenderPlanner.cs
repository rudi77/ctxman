using Ctxman.Core.Domain;

namespace Ctxman.Core.Rendering;

/// <summary>
/// Deterministische Render-Pipeline (Spec §4.6): erzeugt aus dem Segment-Stand einer Session ein
/// neutrales <see cref="RenderModel"/> samt Render-Metadaten. Zustandslos — alle Entscheidungen
/// folgen ausschließlich aus den Eingaben, nie aus Insertion-Order (I4).
///
/// Erzwingt:
/// I3 — <c>evicted | compacted</c> Segmente erscheinen nie im Output.
/// I4 — strikt <c>Static → Working</c>; Static kanonisch nach <c>(source, kind, content_hash)</c>,
///      Working strikt nach <c>seq</c>; gepinnte Segmente bleiben an ihrer chronologischen Position.
/// I5 — unvollständige Units (tool_call ohne render-eligibles tool_result) werden gemeldet, nicht
///      geworfen; der Endpunkt entscheidet über das 422.
/// </summary>
public static class RenderPlanner
{
    /// <summary>
    /// Plant den deterministischen Render-Output für die gegebenen Segmente (Spec §4.6).
    /// </summary>
    /// <param name="session">Die Session.</param>
    /// <param name="segments">Alle Segmente der Session.</param>
    /// <param name="policy">Effektive Policy (Token-Budget etc.).</param>
    /// <param name="scope">Render-Scope-Filter (Spec §2.5). Default: <see cref="RenderScope.Path"/>.</param>
    /// <param name="openFrameIds">IDs aller offenen Frames; leer = Root-Level (kein Frame aktiv).</param>
    /// <param name="tipFrameId">Der oberste offene Frame (Stack-Tip); null wenn kein Frame offen.</param>
    public static RenderPlanResult Plan(
        Session session,
        IReadOnlyList<Segment> segments,
        PolicyConfig policy,
        RenderScope scope = RenderScope.Path,
        IReadOnlyCollection<Ulid>? openFrameIds = null,
        Ulid? tipFrameId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(policy);

        // Spec §2.2 I3: nur live | externalized sind render-eligible; evicted | compacted nie.
        var eligible = segments
            .Where(s => s.State is SegmentState.Live or SegmentState.Externalized)
            .ToList();

        // Spec §4.6 / I4: Static kanonisch nach (source, kind, content_hash) — nie Insertion-Order.
        // Static-Segmente sind innerhalb der Epoche immutable und damit live ⇒ content != null (I1/I2).
        var staticItems = eligible
            .Where(s => s.Region == Region.Static)
            .Select(s => new RenderStaticItem(
                Source: s.Source,
                Kind: s.Kind,
                Content: s.Content ?? string.Empty,
                ContentHash: CanonicalJson.ContentHash(s.Content ?? string.Empty),
                Tokens: s.Tokens))
            .OrderBy(i => i.Source ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(i => i.Kind, StringComparer.Ordinal)
            .ThenBy(i => i.ContentHash, StringComparer.Ordinal)
            .ToList();

        // Spec §2.5: Scope-Filter auf dem Working-Set VOR dem sort/coalescing anwenden (I4).
        // Static wird nie gefiltert. openFrameIds = null/leer bedeutet Root-Level (kein Frame aktiv).
        var effectiveOpenFrameIds = openFrameIds is { Count: > 0 }
            ? openFrameIds.ToHashSet()
            : (ISet<Ulid>)new HashSet<Ulid>();

        var eligibleWorking = eligible.Where(s => s.Region == Region.Working);

        var scopedWorking = scope switch
        {
            // Spec §2.5 scope=path (default): Root-Segmente (frame_id = null) ODER Segmente eines
            // offenen Frames. Geschlossene/evicted Frames werden ausgeblendet.
            RenderScope.Path => eligibleWorking.Where(s =>
                s.FrameId is null || effectiveOpenFrameIds.Contains(s.FrameId.Value)),

            // Spec §2.5 scope=frame: gepinnte Root-Segmente (frame_id = null && pinned) PLUS
            // Segmente des Tip-Frames. Ohne Tip degeneriert zu gepinnten Root-Segmenten.
            RenderScope.Frame => eligibleWorking.Where(s =>
                (s.FrameId is null && s.Pinned) ||
                (tipFrameId.HasValue && s.FrameId == tipFrameId.Value)),

            _ => eligibleWorking,
        };

        // Spec §4.6 / I4: Working strikt nach seq aufsteigend; gepinnte Segmente bleiben an ihrer
        // chronologischen Position (kein Herausziehen, kein Reordering).
        var working = scopedWorking
            .OrderBy(s => s.Seq)
            .ToList();

        // Spec §2 I5: vollständige Unit = tool_call + korrespondierendes tool_result (via tool_call_id).
        // Render-eligible tool_results (live | externalized) schließen einen Call.
        var fulfilledToolCallIds = working
            .Where(s => s.Kind == "tool_result" && s.ToolCallId is not null)
            .Select(s => s.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        var openToolCallIds = working
            .Where(s => s.Kind == "tool_call"
                && s.ToolCallId is not null
                && !fulfilledToolCallIds.Contains(s.ToolCallId))
            .Select(s => s.ToolCallId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Working → Content-Blocks, dann Coalescing benachbarter gleicher Rollen (Spec §4.6).
        var messages = Coalesce(working);

        var emitExpandContextRef = eligible.Any(s => s.State == SegmentState.Externalized);

        // tokens_total: Summe der bereits gezählten pro-Segment-Tokens (WP2-Heuristik); keine
        // Neuberechnung über einen Provider-Tokenizer.
        var tokensTotal = eligible.Sum(s => s.Tokens);

        var watermarkState = WatermarkState.Derive(tokensTotal, policy);

        var model = new RenderModel(
            StaticItems: staticItems,
            Messages: messages,
            EmitExpandContextRef: emitExpandContextRef,
            TokensTotal: tokensTotal);

        return new RenderPlanResult(
            Model: model,
            OpenToolCallIds: openToolCallIds,
            TokensTotal: tokensTotal,
            WatermarkState: watermarkState,
            StaticPrefix: staticItems);
    }

    /// <summary>
    /// Fasst benachbarte Working-Segmente gleicher Rolle (nach seq-Sort) zu einer
    /// <see cref="RenderMessage"/> mit mehreren Content-Blocks zusammen (Spec §4.6, Coalescing).
    /// </summary>
    private static List<RenderMessage> Coalesce(IReadOnlyList<Segment> workingSorted)
    {
        var messages = new List<RenderMessage>();
        List<RenderContentBlock>? currentBlocks = null;
        Role currentRole = default;

        foreach (var segment in workingSorted)
        {
            // Working-Segmente ohne Rolle sind nicht message-render-fähig — defensiv überspringen.
            if (segment.Role is not { } role)
            {
                continue;
            }

            var block = ToBlock(segment);

            if (currentBlocks is not null && role == currentRole)
            {
                currentBlocks.Add(block);
                continue;
            }

            if (currentBlocks is not null)
            {
                messages.Add(new RenderMessage(currentRole, currentBlocks));
            }

            currentRole = role;
            currentBlocks = new List<RenderContentBlock> { block };
        }

        if (currentBlocks is not null)
        {
            messages.Add(new RenderMessage(currentRole, currentBlocks));
        }

        return messages;
    }

    /// <summary>
    /// Baut den Content-Block eines Working-Segments. Externalisierte Segmente werden durch
    /// <c>summary</c> + Ref-Hinweis auf die Segment-ID ersetzt (Spec §2.4) — nie der Roh-Content.
    /// </summary>
    private static RenderContentBlock ToBlock(Segment segment)
    {
        var kind = segment.Kind switch
        {
            "tool_call" => RenderBlockKind.ToolCall,
            "tool_result" => RenderBlockKind.ToolResult,
            _ => RenderBlockKind.Text,
        };

        // Spec §2.4: externalisiertes Result ⇒ summary + Ref-Hinweis, der auf das Segment zeigt.
        var text = segment.State == SegmentState.Externalized
            ? BuildRefHint(segment)
            : segment.Content;

        var toolName = segment.Kind == "tool_call" ? segment.Source : null;

        return new RenderContentBlock(
            Kind: kind,
            Text: text,
            ToolCallId: segment.ToolCallId,
            ToolName: toolName);
    }

    private static string BuildRefHint(Segment segment)
    {
        var summary = string.IsNullOrEmpty(segment.Summary) ? "(externalized)" : segment.Summary;
        return $"{summary}\n[content externalized — expand_context_ref(segment_id=\"{segment.Id}\")]";
    }
}
