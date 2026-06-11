using System.Text.Json.Serialization;

namespace Ctxman.Api.Endpoints;

/// <summary>
/// Antwort von <c>GET /v1/sessions/{sid}/refs/{segment_id}</c> bei erfolgreichem Page Fault
/// (Spec §4.3): <c>200 { content, content_type }</c>. Wire-Format snake_case (global konfiguriert).
/// </summary>
internal sealed record ExpandRefResponse(
    string Content,
    string ContentType);

/// <summary>
/// Antwort bei nicht mehr expandierbarem Segment (Spec §4.3 / §7.1):
/// <c>410 { summary, origin? }</c> als bestmögliche Restinformation. <c>origin</c> wird
/// weggelassen, wenn auf dem Segment nicht gesetzt.
/// </summary>
internal sealed record ExpandRefGoneResponse(
    string? Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Origin);
