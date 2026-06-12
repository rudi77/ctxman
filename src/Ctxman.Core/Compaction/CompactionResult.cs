namespace Ctxman.Core.Compaction;

/// <summary>
/// Ergebnis eines <see cref="ICompactionModel.SummarizeAsync"/>-Aufrufs. Der Worker nutzt
/// <see cref="Summary"/> als <c>content</c> des neu angelegten <c>compaction_summary</c>-Segments
/// und zählt dessen Token via <c>ITokenCounter</c> (Spec §3.3).
/// </summary>
/// <param name="Summary">Vom Modell produzierter Zusammenfassungs- oder Fact-Extraction-Text.</param>
public sealed record CompactionResult(string Summary);
