using Ctxman.Core.Compaction;

namespace Ctxman.Api.Compaction;

/// <summary>
/// Prompt-Templates und Fenster-Aufbereitung der Compaction-Backends (Spec §8). Beide Adapter
/// (Anthropic, Azure OpenAI) teilen sie — die Templates sind Verhalten von ctxman, nicht vom
/// Provider, und sollen nicht auseinanderlaufen.
/// </summary>
internal static class CompactionPrompts
{
    /// <summary>Template-ID der Fact-Extraction (Spec §3.3 Schritt 1).</summary>
    public const string FactExtractionTemplateId = "fact-extraction-v1";

    /// <summary>
    /// Feste Abschnitte statt „fasse knapp zusammen": nach der Compaction sieht das Modell des
    /// Agents NUR noch diese Zusammenfassung. Was sie nicht enthält — was schon erledigt ist,
    /// welcher Ansatz schon gescheitert ist —, das tut der Agent noch einmal.
    ///
    /// Der Satz zur früheren Zusammenfassung ist nicht optional: ein älteres
    /// <c>compaction_summary</c>-Segment ist live und landet beim nächsten Major GC im Fenster.
    /// Ohne die Anweisung wird die Zusammenfassung einer Zusammenfassung von Lauf zu Lauf
    /// dünner, und die Zeit vor der ersten Compaction verschwindet.
    /// </summary>
    private const string SummarizePrompt =
        "Summarize the following segments of an agent's context so that the agent can continue its " +
        "work WITHOUT the original segments. If they contain an earlier summary (kind " +
        "compaction_summary), carry its content over completely — it is replaced by yours. " +
        "Use these sections (terse bullet points, but complete facts; omit empty sections):\n" +
        "GOAL: the task in one sentence.\n" +
        "DONE: what has been done.\n" +
        "CHANGED: artifacts that were changed (e.g. files) — what changed.\n" +
        "FINDINGS: facts that are still needed (causes, locations, commands that work).\n" +
        "FAILED ATTEMPTS: what did not work and why — so it is not repeated.\n" +
        "OPEN: what to do next.";

    /// <summary>
    /// Der letzte Satz ist die Voraussetzung dafür, dass Spec §3.3 „leeres Summary = keine
    /// dauerhaften Fakten" überhaupt eintreten kann: ohne ihn antwortet ein Modell immer mit
    /// irgendetwas, und jede Promotion schreibt einen Pseudo-Fakt in die Senke.
    /// </summary>
    private const string FactExtractionPrompt =
        "Extract the durable facts from the segments below — decisions, constraints and learned " +
        "invariants that remain true beyond this conversation — as a concise bulleted list. " +
        "If there are none, reply with an empty response: no text at all, not even \"none\".";

    public static string SystemPrompt(string templateId) => templateId switch
    {
        // Minimale Built-in-Templates — Spec §8 verlangt keine spezifische Template-Registry.
        FactExtractionTemplateId => FactExtractionPrompt,
        _ => SummarizePrompt, // "default-v1" und alle anderen
    };

    /// <summary>
    /// Das Fenster als lesbarer Text. Die Kopfzeile trägt Kind UND Herkunft: bei
    /// <c>tool_call</c> ist das der Tool-Name — ohne ihn sähe das Modell nur Argumente.
    /// </summary>
    public static string UserContent(IReadOnlyList<WindowItem> window)
    {
        var parts = window.Select(item =>
        {
            var header = (item.Kind, item.Source) switch
            {
                ({ } kind, { } source) => $"[{kind}: {source}]",
                ({ } kind, null) => $"[{kind}]",
                (null, { } source) => $"[{source}]",
                _ => null,
            };
            return header is null ? item.Content : $"{header}\n{item.Content}";
        });

        return string.Join("\n\n---\n\n", parts);
    }
}
