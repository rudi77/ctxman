namespace Ctxman.Core.Domain;

/// <summary>
/// Wird geworfen, wenn eine mutierende Operation auf einem Segment mit
/// <see cref="Region.Static"/> versucht wird. Spec §2.2 I1: Static-Segmente sind
/// innerhalb einer Static-Epoche immutable — kein Update, keine Eviction, keine
/// Externalisierung, keine Compaction. Änderungen laufen ausschließlich über den
/// Epoch-Bump-Endpunkt (§4.2); direkte Schreibversuche ⇒ 409 Conflict (vom API-Layer
/// aus dieser Exception abgeleitet).
/// </summary>
public sealed class StaticRegionImmutableException : InvalidOperationException
{
    public StaticRegionImmutableException(Ulid segmentId)
        : base($"Segment {segmentId} liegt in der Static-Region und ist innerhalb der "
            + "Static-Epoche immutable (Spec §2.2 I1). Änderungen nur via Epoch-Bump (§4.2).")
    {
        SegmentId = segmentId;
    }

    public Ulid SegmentId { get; }
}

/// <summary>
/// Wird geworfen, wenn ein Segment mit <see cref="SegmentState.Live"/> oder
/// <see cref="SegmentState.Externalized"/> weder <c>content</c> noch <c>blob_ref</c> trägt.
/// Spec §2.2 I2: <c>content</c> und <c>blob_ref</c> sind nie beide null bei
/// <c>state = live | externalized</c>.
/// </summary>
public sealed class SegmentContentInvariantException : InvalidOperationException
{
    public SegmentContentInvariantException(SegmentState state)
        : base($"Segment mit state '{state.ToWire()}' muss content oder blob_ref tragen "
            + "(Spec §2.2 I2: nie beide null bei live | externalized).")
    {
        State = state;
    }

    public SegmentState State { get; }
}
