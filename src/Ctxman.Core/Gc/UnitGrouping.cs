using Ctxman.Core.Domain;

namespace Ctxman.Core.Gc;

/// <summary>
/// Eine logische Einheit im Sinne von Spec §2.4: ein <c>tool_call</c>-Segment plus sein
/// korrespondierendes <c>tool_result</c> (gekoppelt über <c>tool_call_id</c>). Ungekoppelte
/// Segmente bilden je eine Single-Segment-Unit. GC operiert ausschließlich auf Units, nie auf
/// gekoppelten Einzel-Segmenten (Spec §2.4 / §3.2).
/// </summary>
public sealed record Unit(IReadOnlyList<Segment> Segments)
{
    /// <summary>tool_call der Unit, falls vorhanden (null bei einer reinen result-/Einzel-Unit).</summary>
    public Segment? ToolCall => Segments.FirstOrDefault(s => s.Kind == "tool_call");

    /// <summary>tool_result der Unit, falls vorhanden.</summary>
    public Segment? ToolResult => Segments.FirstOrDefault(s => s.Kind == "tool_result");

    /// <summary>true ⇔ gekoppelte Unit (tool_call + tool_result via tool_call_id, Spec §2.4).</summary>
    public bool IsCoupled => Segments.Count > 1;

    /// <summary>
    /// Unit-Identität (Spec §2.4): die gemeinsame <c>tool_call_id</c> einer gekoppelten Unit,
    /// sonst die ID des einzelnen Segments. Basis für das <c>unit_evicted</c>-Event (Spec §6).
    /// </summary>
    public string UnitId =>
        Segments.Select(s => s.ToolCallId).FirstOrDefault(id => id is not null)
        ?? Segments[0].Id.ToString();
}

/// <summary>
/// Gruppiert eine Segment-Liste in Units (Spec §2.4). Segmente mit gleichem, nicht-leerem
/// <c>tool_call_id</c> der Kinds <c>tool_call</c>/<c>tool_result</c> werden zu einer Unit
/// gekoppelt; alle übrigen Segmente bilden je eine eigene Single-Segment-Unit.
/// I/O-frei und deterministisch.
/// </summary>
public static class UnitGrouping
{
    public static IReadOnlyList<Unit> GroupIntoUnits(IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var units = new List<Unit>();
        var coupled = new Dictionary<string, List<Segment>>(StringComparer.Ordinal);

        foreach (var segment in segments)
        {
            // Spec §2.4: nur tool_call/tool_result werden über tool_call_id gekoppelt.
            if (segment.ToolCallId is { } id
                && segment.Kind is "tool_call" or "tool_result")
            {
                if (!coupled.TryGetValue(id, out var bucket))
                {
                    bucket = new List<Segment>();
                    coupled[id] = bucket;

                    // Platzhalter wahrt die Reihenfolge des ersten Auftretens der Unit.
                    units.Add(new Unit(bucket));
                }

                bucket.Add(segment);
            }
            else
            {
                units.Add(new Unit(new[] { segment }));
            }
        }

        return units;
    }
}
