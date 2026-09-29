using Ctxman.Core.Domain;

namespace Ctxman.Core.Compaction;

/// <summary>
/// Ein einzelnes Element der Fenster-Sammlung, die kompaktiert werden soll.
/// </summary>
/// <param name="Content">Segment-Inhalt oder Summary (nie null/leer — Aufrufer filtert evicted).</param>
/// <param name="Kind">Offenes Vokabular (Spec §2.2), z. B. "assistant_msg". Dient dem Prompt als Kontext-Hinweis.</param>
/// <param name="Source">
/// Logische Herkunft (Spec §2.2) — bei <c>tool_call</c> der Tool-Name. Ohne ihn sieht das
/// Compaction-Modell von einem Tool-Aufruf nur die Argumente, und die Zusammenfassung erfährt
/// nie, WELCHES Werkzeug lief (welche Datei geschrieben, welcher Befehl ausgeführt wurde).
/// </param>
public sealed record WindowItem(
    string Content,
    string? Kind = null,
    string? Source = null)
{
    /// <summary>
    /// Fenster-Element aus einem Segment (Spec §3.3). Externalisierte Segmente steuern ihre
    /// summary bei — ihr Inhalt liegt im Blob Store, nicht im Segment. Gemeinsame Grundlage von
    /// Compaction und Promotion, damit beide Aufrufe dasselbe Fenster sehen.
    /// </summary>
    public static WindowItem From(Segment segment) =>
        new(segment.Content ?? segment.Summary ?? string.Empty, segment.Kind, segment.Source);
}

/// <summary>
/// Eingabe für <see cref="ICompactionModel.SummarizeAsync"/>. Trägt das Segment-Fenster, das
/// zusammengefasst werden soll, zusammen mit der Prompt-Template-ID und dem Modell-Bezeichner
/// aus der Policy (<see cref="Domain.CompactionConfig"/>). Provider-agnostisch und I/O-frei.
/// Spec §8: wird sowohl für Compaction-Summarization als auch für Promotion-Fact-Extraction
/// verwendet (unterschiedliche <see cref="PromptTemplateId"/>).
/// </summary>
/// <param name="Window">Geordnete Folge der Segmente, die in den Prompt einfließen.</param>
/// <param name="PromptTemplateId">Bezeichner des Prompt-Templates, z. B. "default-v1".</param>
/// <param name="Model">Modell-Bezeichner, z. B. "claude-haiku-4-5".</param>
public sealed record CompactionRequest(
    IReadOnlyList<WindowItem> Window,
    string PromptTemplateId,
    string Model);
