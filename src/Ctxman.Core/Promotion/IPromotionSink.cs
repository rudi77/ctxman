namespace Ctxman.Core.Promotion;

/// <summary>
/// Write-only Senke für extrahierte Fakten aus der Segment-Promotion (Spec §3.3, Non-Goal N2).
/// ctxman schreibt Fakten ausschließlich; ein Rücklesen ist strukturell ausgeschlossen —
/// diese Schnittstelle hat bewusst KEINE Lese- oder Abfrage-Methode (AC7).
/// </summary>
public interface IPromotionSink
{
    // Spec §3.3: Promotion schreibt dauerhaft extrahierte Fakten an die konfigurierte Senke.
    // Non-Goal N2: ctxman liest niemals aus der Senke zurück — daher keine Query-Methode.
    Task WriteAsync(PromotedFact fact, string sinkUrl, CancellationToken ct);
}
