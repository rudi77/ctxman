namespace Ctxman.Core.Promotion;

/// <summary>
/// Ein dauerhafter Fakt, der aus einem Segment durch die Major-Collection extrahiert wurde
/// und an die konfigurierte Promotion-Senke geschrieben wird (Spec §3.3).
/// Wire-Format ist snake_case (Serialisierungs-Belang des Adapters).
/// </summary>
/// <param name="Fact">Der extrahierte Fakt-Text.</param>
/// <param name="SourceSession">ID der Session, aus der der Fakt stammt.</param>
/// <param name="SourceTurn">Turn-Nummer innerhalb der Session.</param>
/// <param name="Kind">Segment-Kind (offenes Vokabular, z. B. "decision"), Spec §2.2.</param>
public sealed record PromotedFact(
    string Fact,
    string SourceSession,
    int SourceTurn,
    string Kind);
