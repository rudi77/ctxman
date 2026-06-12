namespace Ctxman.Api.Endpoints;

/// <summary>
/// Request-Body für <c>POST /v1/sessions/{sid}/frames</c> (Spec §4.3 / §2.5).
/// Wire-Format ist snake_case (global konfiguriert). Es gibt bewusst kein tenant_id-Feld —
/// der Tenant kommt ausschließlich aus dem aufgelösten <c>ITenantContext</c> (Spec §4.1 / §10),
/// nie aus dem Body.
/// </summary>
internal sealed record PushFrameRequest(string? Label);

/// <summary>
/// Antwort von <c>POST /v1/sessions/{sid}/frames</c> (Spec §4.3 / §2.5):
/// <c>201 { frame_id }</c>. <c>frame_id</c> ist die ULID des neu angelegten Frames.
/// </summary>
internal sealed record PushFrameResponse(string FrameId);

/// <summary>
/// Request-Body für <c>DELETE /v1/sessions/{sid}/frames/{fid}</c> (Spec §2.5 pop / §3.3).
/// <see cref="ReturnContent"/> wird als return-Segment mit Kind <see cref="ReturnKind"/>
/// im Parent-Frame angelegt. Wire-Format ist snake_case (global konfiguriert).
/// </summary>
internal sealed record PopFrameRequest(
    string ReturnContent,
    string? ReturnKind);

/// <summary>
/// Antwort von <c>DELETE /v1/sessions/{sid}/frames/{fid}</c> (Spec §2.5 pop):
/// <c>200 { return_segment_id, context_version }</c>.
/// </summary>
internal sealed record PopFrameResponse(
    string ReturnSegmentId,
    long ContextVersion);
