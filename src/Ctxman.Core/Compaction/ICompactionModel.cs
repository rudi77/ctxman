namespace Ctxman.Core.Compaction;

/// <summary>
/// Abstraktion für das ctxman-eigene Compaction-LLM-Backend (Spec §8).
/// Ruft NIEMALS das LLM des Agents auf (Non-Goal N1) — es ist ein separates,
/// günstiges Modell, das ausschließlich für Summarization und Fact-Extraction
/// eingesetzt wird.
/// </summary>
public interface ICompactionModel
{
    /// <summary>
    /// Fasst das übergebene Segment-Fenster gemäß dem angegebenen Prompt-Template zusammen
    /// oder extrahiert Fakten daraus (je nach <see cref="CompactionRequest.PromptTemplateId"/>).
    /// </summary>
    /// <param name="request">Fensterinhalt, Prompt-Template-ID und Modell-Bezeichner.</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <returns>Das produzierte Summary oder die extrahierten Fakten.</returns>
    Task<CompactionResult> SummarizeAsync(CompactionRequest request, CancellationToken ct);
}
