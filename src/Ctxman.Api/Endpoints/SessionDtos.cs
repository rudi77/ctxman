namespace Ctxman.Api.Endpoints;

/// <summary>
/// Request-Body für <c>POST /v1/sessions</c> (Spec §4.3). Wire-Format ist snake_case
/// (global konfiguriert). Es gibt bewusst <em>kein</em> tenant_id-Feld — der Tenant kommt
/// ausschließlich aus dem aufgelösten <c>ITenantContext</c> (Spec §4.1 / §10), nie aus dem Body.
/// </summary>
internal sealed record CreateSessionRequest(
    string? AgentTemplateId,
    PolicyOverridesDto? PolicyOverrides,
    IReadOnlyList<StaticSegmentDto>? StaticSegments);

/// <summary>
/// Optionale Overrides der effektiven Policy (Spec §5). Nur skalare Top-Level-Felder, die
/// in diesem Workpaket überschrieben werden können; nicht gesetzte Felder behalten den
/// <c>PolicyConfig.Default()</c>-Wert. Validierung beim Anlegen (Spec §5).
/// </summary>
internal sealed record PolicyOverridesDto(
    int? BudgetTokens,
    WatermarksDto? Watermarks,
    int? ExternalizeThresholdTokens,
    string? Tokenizer);

/// <summary>Override der drei Watermark-Schwellen relativ zum Budget (Spec §3.1 / §5).</summary>
internal sealed record WatermarksDto(
    double? Soft,
    double? Hard,
    double? Emergency);

/// <summary>
/// Ein initiales Static-Region-Segment (Spec §4.3: Static wird hier initial gesetzt). System-
/// Prompt / Tool-Definitionen. <c>role</c> ist optional (snake_case-Wire), <c>content</c> ist Pflicht.
/// </summary>
internal sealed record StaticSegmentDto(
    string Kind,
    string? Role,
    string Content,
    string? Source);

/// <summary>Antwort von <c>POST /v1/sessions</c> (Spec §4.3): <c>201 { session_id, context_version }</c>.</summary>
internal sealed record CreateSessionResponse(
    string SessionId,
    long ContextVersion);

/// <summary>
/// Antwort von <c>GET /v1/sessions/{sid}</c> (Spec §4.3): Session-Metadaten + Budget-Status
/// (<c>tokens_used</c>, <c>watermark_state</c>).
/// </summary>
internal sealed record SessionDetailResponse(
    string SessionId,
    string? AgentTemplateId,
    string Status,
    long ContextVersion,
    int StaticEpoch,
    int CurrentTurn,
    int TokensUsed,
    string WatermarkState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
