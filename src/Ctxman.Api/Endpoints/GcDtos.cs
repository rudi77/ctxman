namespace Ctxman.Api.Endpoints;

/// <summary>
/// Request-Body für <c>POST /v1/sessions/{sid}/gc</c> (Spec §4.3): <c>{ level }</c> mit
/// <c>minor | major</c>. Wire-Format ist snake_case (global konfiguriert).
/// </summary>
internal sealed record GcRequest(string? Level);

/// <summary>
/// Antwort von <c>POST /v1/sessions/{sid}/gc</c> (Spec §4.3): <c>202 { job_id }</c>.
/// <c>job_id</c> ist die ULID des eingereihten GC-Jobs.
/// </summary>
internal sealed record GcResponse(string JobId);
