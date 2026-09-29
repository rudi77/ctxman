using Ctxman.Core.Domain;

namespace Ctxman.Core.Gc;

/// <summary>
/// Der Compaction-Plan für eine Major Collection (Spec §3.3 Schritt 2).
/// Enthält die geordneten Quell-Segmente (älteste→jüngste nach <see cref="Segment.Seq"/>),
/// die Gesamttokenanzahl vor der Komprimierung und die älteste Seq-Position,
/// an der das Zusammenfassungs-Segment eingefügt wird.
///
/// Ein leerer Plan (<see cref="IsNoOp"/> == true) signalisiert, dass keine Komprimierung
/// stattfindet — weniger als 2 auswählbare Units im Compaction-Fenster.
/// </summary>
public sealed record CompactionPlan(
    IReadOnlyList<Segment> SourceSegments,
    int TokensBefore,
    long OldestSeq)
{
    /// <summary>
    /// Kein nutzbarer Compaction-Plan: weniger als 2 Segmente im Fenster
    /// (Spec §3.3: ein Einzel-Segment lohnt keinen LLM-Call).
    /// </summary>
    public bool IsNoOp => SourceSegments.Count < 2;

    /// <summary>Leerer/No-op-Plan ohne Quell-Segmente.</summary>
    public static CompactionPlan Empty { get; } =
        new(Array.Empty<Segment>(), TokensBefore: 0, OldestSeq: 0);
}

/// <summary>
/// Deterministische, I/O-freie Major Collection (Spec §3.3 Schritt 2).
/// Berechnet das Compaction-Fenster aus allen nicht-gepinnten Working-<b>Units</b> (Spec §3.3:
/// „alle nicht gepinnten Working-Units, von alt nach jung") — sortiert nach
/// <see cref="Segment.Seq"/> aufsteigend. Akkumuliert Token-Counts, bis die nächste Unit
/// <c>max_share × BudgetTokens</c> überschreiten würde; dort stoppt das Fenster.
/// Kein I/O, keine Mutationen.
/// </summary>
public static class MajorCollector
{
    /// <summary>
    /// Berechnet den Compaction-Plan für die übergebenen Segmente (Spec §3.3 Schritt 2).
    ///
    /// Selektions-Invarianten:
    /// <list type="bullet">
    ///   <item><see cref="Region.Working"/> — Static-Region ausgenommen (Spec §2.2 I1).</item>
    ///   <item>Render-eligible (<see cref="SegmentState.Live"/> | <see cref="SegmentState.Externalized"/>)
    ///   — compacted/evicted sind raus. Externalisierte Segmente MÜSSEN mit ins Unit-Grouping:
    ///   sonst fiele ein externalisiertes tool_result aus seiner Unit, der live tool_call würde
    ///   allein kompaktiert und das Result verwaiste im Render (<c>role: tool</c> ohne Aufruf —
    ///   der Provider lehnt den ganzen Request ab; I5 meldet nur die Gegenrichtung). Ihr
    ///   Fenster-Beitrag ist die summary.</item>
    ///   <item>Nicht gepinnt — auf UNIT-Ebene (Spec §2.2 I1 / §3.3): ein gepinntes Segment schützt
    ///   seine ganze Unit, sonst entstünde derselbe Orphan über den Pin.</item>
    /// </list>
    /// Akkumulations-Abbruch: Hinzufügen der nächsten Unit würde
    /// <c>max_share × BudgetTokens</c> überschreiten → stopp (Spec §3.3).
    ///
    /// Spec §2.4: Compaction operiert auf Units, nie auf Einzel-Segmenten. Eine gekoppelte
    /// tool_call+tool_result-Unit wird atomar behandelt — entweder ganz oder gar nicht
    /// ausgewählt, damit keine verwaisten tool_use/tool_result-Blöcke im Render entstehen. // Spec §2.4
    /// </summary>
    /// <param name="segments">Alle Segmente der Session (beliebige Reihenfolge).</param>
    /// <param name="policy">Effektive Policy der Session.</param>
    /// <returns>
    /// <see cref="CompactionPlan"/> mit den ausgewählten Quell-Segmenten; bei weniger als 2
    /// auswählbaren Units wird <see cref="CompactionPlan.Empty"/> zurückgegeben.
    /// </returns>
    public static CompactionPlan PlanCompaction(
        IEnumerable<Segment> segments,
        PolicyConfig policy)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(policy);

        // Spec §3.3: Compaction-Fenster = render-eligible Working-Segmente (live UND
        // externalisiert); Static ausgeschlossen (I1), compacted/evicted nicht berücksichtigt.
        // Pins werden NICHT hier gefiltert, sondern erst auf Unit-Ebene (s. u.).
        var candidates = segments
            .Where(s =>
                s.Region == Region.Working
                && s.State is SegmentState.Live or SegmentState.Externalized)
            .OrderBy(s => s.Seq)
            .ToList();

        // Spec §2.4: Segmente in Units gruppieren — gekoppelte tool_call+tool_result-Paare
        // bilden eine Unit und werden atomar behandelt. Eine Unit mit mindestens einem
        // gepinnten Segment fällt als Ganzes heraus (Spec §3.3: „nicht gepinnte Working-Units").
        var units = UnitGrouping.GroupIntoUnits(candidates)
            .Where(u => u.Segments.All(s => !s.Pinned))
            .ToList();

        // Spec §3.3 / §2.4: Tokenbudget-Obergrenze = max_share × BudgetTokens.
        // Akkumulieren UNIT BY UNIT — eine Unit wird ganz aufgenommen oder gar nicht. // Spec §2.4
        var tokenLimit = policy.Compaction.MaxShare * policy.BudgetTokens; // Spec §3.3
        var selectedUnits = new List<Unit>();
        var accumulated = 0;

        foreach (var unit in units)
        {
            var unitTokens = unit.Segments.Sum(s => s.Tokens);

            // Spec §3.3: stopp BEFORE die nächste Unit das Limit überschreiten würde.
            if (selectedUnits.Count > 0 && accumulated + unitTokens > tokenLimit)
            {
                break;
            }

            selectedUnits.Add(unit);
            accumulated += unitTokens;
        }

        // Spec §3.3 / §2.4: weniger als 2 Units → kein sinnvoller LLM-Compaction-Call.
        if (selectedUnits.Count < 2)
        {
            return CompactionPlan.Empty;
        }

        // Flatten der ausgewählten Units zurück in eine Segment-Liste (älteste→jüngste nach seq).
        var selected = selectedUnits
            .SelectMany(u => u.Segments)
            .OrderBy(s => s.Seq)
            .ToList();

        // Spec §3.3: die älteste Seq-Position bestimmt den Einfügepunkt des Summary-Segments.
        var oldestSeq = selected[0].Seq; // bereits aufsteigend sortiert // Spec §3.3

        return new CompactionPlan(selected, TokensBefore: accumulated, OldestSeq: oldestSeq);
    }
}
