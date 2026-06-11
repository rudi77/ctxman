namespace Ctxman.Api.Endpoints;

/// <summary>
/// Request-Body für <c>POST /v1/sessions/{sid}/segments</c> (Spec §4.3). Akzeptiert beide
/// Formen über <em>ein</em> DTO: Single — die Append-Felder liegen direkt im Body; Batch —
/// <see cref="Segments"/> trägt die Liste. Welche Form vorliegt, entscheidet sich daran, ob
/// <see cref="Segments"/> gesetzt ist (Spec §4.3). Wire-Format ist snake_case (global
/// konfiguriert); es gibt bewusst <em>kein</em> tenant_id-Feld — der Tenant kommt
/// ausschließlich aus dem aufgelösten <c>ITenantContext</c> (Spec §4.1 / §10), nie aus dem Body.
/// </summary>
internal sealed record AppendSegmentsRequest(
    // Batch-Form (Spec §4.3: { segments: [...] }).
    IReadOnlyList<AppendSegmentDto>? Segments,
    // Single-Form (Spec §4.3: { kind, role, content | blob_ref, tool_call_id?, pinned?, source? }).
    string? Kind,
    string? Role,
    string? Content,
    BlobRefDto? BlobRef,
    string? ToolCallId,
    bool? Pinned,
    string? Source,
    string? Region)
{
    /// <summary>
    /// Normalisiert beide Formen auf eine Segment-Liste in Body-Reihenfolge: Batch liefert
    /// <see cref="Segments"/>, Single faltet die Top-Level-Felder zu genau einem Eintrag.
    /// </summary>
    public IReadOnlyList<AppendSegmentDto> ToList() =>
        Segments ?? new[]
        {
            new AppendSegmentDto(Kind, Role, Content, BlobRef, ToolCallId, Pinned, Source, Region),
        };
}

/// <summary>
/// Ein einzelnes anzuhängendes Segment (Spec §4.3). <c>content</c> XOR <c>blob_ref</c>;
/// <c>blob_ref</c> ⇒ das Segment gilt von Anfang an als externalisiert (Spec §4.3 / §2.6).
/// </summary>
internal sealed record AppendSegmentDto(
    string? Kind,
    string? Role,
    string? Content,
    BlobRefDto? BlobRef,
    string? ToolCallId,
    bool? Pinned,
    string? Source,
    string? Region);

/// <summary>Pointer in den Blob Store (Spec §2.6) im Wire-Format snake_case.</summary>
internal sealed record BlobRefDto(
    string Store,
    string Key,
    long SizeBytes,
    string ContentType);

/// <summary>
/// Antwort von <c>POST /v1/sessions/{sid}/segments</c> (Spec §4.3):
/// <c>201 { segment_ids[], context_version }</c>. <c>segment_ids</c> sind die ULIDs der
/// angelegten Segmente in Body-Reihenfolge; <c>context_version</c> ist die neue Version
/// nach dem (genau einmaligen) Increment.
/// </summary>
internal sealed record AppendSegmentsResponse(
    IReadOnlyList<string> SegmentIds,
    long ContextVersion);
