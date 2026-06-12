using Ctxman.Core.Domain;

namespace Ctxman.Core.Gc;

/// <summary>
/// Ein Externalisierungs-Kandidat aus Phase 2 der Minor Collection (Spec §3.2 Schritt 2).
/// Der Collector führt KEIN I/O aus: er liefert nur das Ziel-Segment plus die zu schreibende
/// <see cref="Content"/> und die berechnete <see cref="Summary"/>. Der GC-Worker (späterer
/// Subtask) führt <c>IBlobStore.Put</c> aus und ruft anschließend <c>Segment.Externalize</c>.
/// </summary>
public sealed record ExternalizationCandidate(
    Segment Segment,
    string Content,
    string? Summary);

/// <summary>
/// Eine durch TTL-Eviction (Phase 3, Spec §3.2.3) entfernte Unit (Spec §2.4). Eine ungekoppelte
/// Unit trägt genau ein Segment; eine gekoppelte tool_call+tool_result-Unit trägt beide entfernten
/// Segmente. <see cref="UnitId"/> ist die Unit-Identität (tool_call_id bei Kopplung, sonst die ID
/// des einzelnen Segments) — der Worker emittiert genau EIN <c>unit_evicted</c> je Unit (Spec §6).
/// </summary>
public sealed record EvictedUnit(string UnitId, IReadOnlyList<Segment> Segments);

/// <summary>
/// Strukturierter Plan einer vollständigen Minor Collection (Spec §3.2). Phase 1 (Clean-Page)
/// evicted refetchable Einzel-Segmente → <see cref="CleanPageEvicted"/> (Worker: <c>segment_evicted</c>);
/// Phase 3 (TTL) evicted Units → <see cref="UnitEvicted"/> (Worker: ein <c>unit_evicted</c> je Unit).
/// Die noch nicht ausgeführten Externalisierungen (Phase 2) stehen in
/// <see cref="ExternalizationCandidates"/> — diese werden vom Worker I/O-behaftet abgearbeitet.
/// Alle Eviction-Segmente sind in-memory bereits mutiert; nur die Phase-2-Kandidaten sind offen.
/// </summary>
public sealed record MinorCollectionPlan(
    IReadOnlyList<Segment> CleanPageEvicted,
    IReadOnlyList<EvictedUnit> UnitEvicted,
    IReadOnlyList<ExternalizationCandidate> ExternalizationCandidates);

/// <summary>
/// Deterministische, I/O-freie Minor Collection (Spec §3.2). Operiert ausschließlich auf
/// In-Memory-Segmentzustand: <see cref="Segment.Evict"/> für lossless/lossy Eviction; die
/// Externalisierung (Blob-Write) wird nur GEPLANT, nicht ausgeführt (kein <c>IBlobStore</c>).
///
/// Reihenfolge ist normativ (Spec §3.2): (1) Clean-Page-Eviction → (2) Externalisierung →
/// (3) TTL-Eviction; billig vor teuer, lossless vor lossy. Gepinnte und Static-Segmente
/// werden in JEDER Phase übersprungen (Spec §2.2 I1 / §3.2).
/// </summary>
public static class MinorCollector
{
    /// <summary>
    /// Notfall-Minor-Collection ohne I/O-Seiteneffekte (Spec §3.1): nur die I/O-freien Phasen
    /// 1 (Clean-Page) und 3 (TTL-Eviction). Phase 2 (Externalisierung) wird übersprungen, weil
    /// sie einen Blob-Write erfordert und im Hot Path verboten ist. Liefert dieselbe Aufteilung
    /// wie <see cref="PlanFull"/> (Clean-Page-Segmente getrennt von Unit-Evictions, Spec §6), nur
    /// ohne Externalisierungs-Kandidaten — der Render-Endpunkt emittiert daraus
    /// <c>segment_evicted</c> + <c>unit_evicted</c>.
    /// </summary>
    public static MinorCollectionPlan CollectEmergencyNoIo(
        IReadOnlyList<Segment> segments,
        PolicyConfig policy,
        int currentTurn)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(policy);

        var units = UnitGrouping.GroupIntoUnits(segments);
        var cleanPageEvicted = new List<Segment>();
        var unitEvicted = new List<EvictedUnit>();

        // Spec §3.1: nur Operationen ohne I/O — Clean-Page + TTL-Eviction, keine Externalisierung.
        CollectCleanPage(units, policy, currentTurn, cleanPageEvicted);
        CollectTtl(units, policy, currentTurn, unitEvicted);

        return new MinorCollectionPlan(cleanPageEvicted, unitEvicted, Array.Empty<ExternalizationCandidate>());
    }

    /// <summary>
    /// Vollständige Minor Collection als Plan (Spec §3.2, alle drei Phasen in normativer
    /// Reihenfolge). Phasen 1 und 3 evicten direkt (kein I/O); Phase 2 wird nur als Kandidaten-
    /// Liste zurückgegeben — der Worker führt den Blob-Write aus.
    /// </summary>
    public static MinorCollectionPlan PlanFull(
        IReadOnlyList<Segment> segments,
        PolicyConfig policy,
        int currentTurn)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(policy);

        var units = UnitGrouping.GroupIntoUnits(segments);
        var cleanPageEvicted = new List<Segment>();
        var unitEvicted = new List<EvictedUnit>();
        var externalizationCandidates = new List<ExternalizationCandidate>();

        // Spec §3.2: billig vor teuer, lossless vor lossy.
        CollectCleanPage(units, policy, currentTurn, cleanPageEvicted);     // 1
        CollectExternalization(units, policy, externalizationCandidates);  // 2
        CollectTtl(units, policy, currentTurn, unitEvicted);              // 3

        return new MinorCollectionPlan(cleanPageEvicted, unitEvicted, externalizationCandidates);
    }

    /// <summary>
    /// Phase 1 — Clean-Page-Eviction (Spec §3.2 Schritt 1): refetchable Segmente jenseits ihrer
    /// Kind-TTL → <c>evicted</c>, OHNE Blob-Write (Source of Truth liegt außerhalb). origin bleibt.
    /// </summary>
    private static void CollectCleanPage(
        IReadOnlyList<Unit> units,
        PolicyConfig policy,
        int currentTurn,
        List<Segment> evicted)
    {
        foreach (var unit in units)
        {
            foreach (var segment in unit.Segments)
            {
                if (!IsGcEligible(segment))
                {
                    continue;
                }

                if (!segment.Refetchable)
                {
                    continue;
                }

                if (!IsTtlExpired(segment, policy, currentTurn))
                {
                    continue;
                }

                segment.Evict(); // Spec §3.2.1: ohne Blob-Write; origin/audit bleibt erhalten.
                evicted.Add(segment);
            }
        }
    }

    /// <summary>
    /// Phase 2 — Externalisierung (Spec §3.2 Schritt 2): nicht-refetchable Segmente mit
    /// <c>tokens &gt; externalize_threshold_tokens</c> und Kind-Eignung (<c>Externalize=true</c>)
    /// als Kandidaten. Bei gekoppelten Units (Spec §2.4) wird nur das tool_result externalisiert
    /// — der tool_call bleibt live. KEIN I/O: nur Plan, Worker führt aus.
    /// </summary>
    private static void CollectExternalization(
        IReadOnlyList<Unit> units,
        PolicyConfig policy,
        List<ExternalizationCandidate> candidates)
    {
        foreach (var unit in units)
        {
            foreach (var segment in unit.Segments)
            {
                // Spec §2.4: in einer gekoppelten Unit nur das tool_result externalisieren;
                // der tool_call bleibt live, damit keine verwaisten tool_use-Blöcke entstehen.
                if (unit.IsCoupled && segment.Kind == "tool_call")
                {
                    continue;
                }

                if (!IsGcEligible(segment))
                {
                    continue;
                }

                if (segment.State != SegmentState.Live || segment.Content is null)
                {
                    continue;
                }

                if (segment.Refetchable)
                {
                    continue;
                }

                if (segment.Tokens <= policy.ExternalizeThresholdTokens)
                {
                    continue;
                }

                if (!IsExternalizeEligible(segment, policy))
                {
                    continue;
                }

                var content = segment.Content;
                // Spec §3.2.2: summary := first_n_chars + Strukturhinweis (mirror StaticRegionDiff).
                var summary = content.Length <= 200 ? content : content[..200] + "…";
                candidates.Add(new ExternalizationCandidate(segment, content, summary));
            }
        }
    }

    /// <summary>
    /// Phase 3 — TTL-Eviction (Spec §3.2 Schritt 3): Units, deren Kind-TTL überschritten ist
    /// (<c>current_turn − last_referenced_turn &gt; ttl_turns</c>) und die weder pinned noch Static
    /// sind → <c>evicted</c>. Operiert auf Unit-Ebene: eine gekoppelte Unit verschwindet ganz.
    /// </summary>
    private static void CollectTtl(
        IReadOnlyList<Unit> units,
        PolicyConfig policy,
        int currentTurn,
        List<EvictedUnit> evicted)
    {
        foreach (var unit in units)
        {
            // Spec §2.4: GC auf Unit-Ebene. Ist ein gekoppeltes Segment unantastbar
            // (pinned/Static) oder noch nicht render-tot, bleibt die ganze Unit erhalten.
            var live = unit.Segments
                .Where(s => s.State is SegmentState.Live or SegmentState.Externalized)
                .ToList();

            if (live.Count == 0)
            {
                continue;
            }

            if (live.Any(s => !IsGcEligible(s)))
            {
                continue;
            }

            // Spec §2.4 / §3.2.3: Die TTL einer Unit wird von ihren Segmenten mit definierter
            // Kind-TTL getragen (z. B. tool_result; ein tool_call hat keine eigene TTL). Die Unit
            // ist fällig ⇔ mindestens ein Segment hat eine definierte TTL UND alle definierten
            // TTLs sind überschritten.
            var ttlBearing = live.Where(s => HasDefinedTtl(s, policy)).ToList();
            if (ttlBearing.Count == 0)
            {
                continue;
            }

            if (!ttlBearing.All(s => IsTtlExpired(s, policy, currentTurn)))
            {
                continue;
            }

            foreach (var segment in live)
            {
                segment.Evict();
            }

            // Spec §6: eine Unit-Eviction ist EIN unit_evicted-Event — bei gekoppelten Units
            // deckt es beide entfernten Segmente ab (Spec §2.4).
            evicted.Add(new EvictedUnit(unit.UnitId, live));
        }
    }

    /// <summary>
    /// Spec §2.2 I1 / §3.2: gepinnte und Static-Segmente sind für den GC unantastbar.
    /// Bereits nicht-live/externalized Segmente werden nicht erneut angefasst.
    /// </summary>
    private static bool IsGcEligible(Segment segment) =>
        segment.Region != Region.Static
        && !segment.Pinned
        && segment.State is SegmentState.Live or SegmentState.Externalized;

    /// <summary>
    /// Spec §3.2: TTL überschritten ⇔ <c>current_turn − last_referenced_turn &gt; ttl_turns</c>.
    /// Kind ohne Policy oder mit TtlTurns == null (∞) ist nie TTL-fällig.
    /// </summary>
    private static bool IsTtlExpired(Segment segment, PolicyConfig policy, int currentTurn)
    {
        if (!policy.Kinds.TryGetValue(segment.Kind, out var kindPolicy))
        {
            return false;
        }

        if (kindPolicy.TtlTurns is not { } ttl)
        {
            return false; // ∞ — nie TTL-evicten.
        }

        return currentTurn - segment.LastReferencedTurn > ttl;
    }

    /// <summary>true ⇔ Kind hat eine endliche TTL (Policy vorhanden, TtlTurns != null/∞).</summary>
    private static bool HasDefinedTtl(Segment segment, PolicyConfig policy) =>
        policy.Kinds.TryGetValue(segment.Kind, out var kindPolicy) && kindPolicy.TtlTurns is not null;

    /// <summary>Spec §3.2.2: Kind-Eignung für Externalisierung (KindPolicy.Externalize).</summary>
    private static bool IsExternalizeEligible(Segment segment, PolicyConfig policy) =>
        policy.Kinds.TryGetValue(segment.Kind, out var kindPolicy) && kindPolicy.Externalize;
}
