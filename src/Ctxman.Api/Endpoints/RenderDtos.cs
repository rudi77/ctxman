namespace Ctxman.Api.Endpoints;

/// <summary>Request-Body für <c>POST /v1/sessions/{sid}/render</c> (Spec §4.3).</summary>
internal sealed record RenderRequest(
    string Provider,
    string? Scope,
    bool? TurnAdvance);

/// <summary>Antwort von <c>POST /v1/sessions/{sid}/render</c> (Spec §4.3).</summary>
internal sealed record RenderResponse(
    object RequestFragment,
    IReadOnlyList<object> CacheBreakpoints,
    IReadOnlyList<object> BuiltinTools,
    long ContextVersion,
    int TokensTotal,
    string WatermarkState);

/// <summary>Request-Body für <c>PUT /v1/sessions/{sid}/static-segments</c> (Spec §4.2).</summary>
internal sealed record ReplaceStaticSegmentsRequest(
    IReadOnlyList<StaticSegmentDto> Segments);

/// <summary>Antwort von <c>PUT /v1/sessions/{sid}/static-segments</c> (Spec §4.2).</summary>
internal sealed record ReplaceStaticSegmentsResponse(
    int StaticEpoch,
    long ContextVersion,
    StaticRegionDiffDto Diff);

/// <summary>Epoch-Diff über Tool-Namen und Sources (Spec §4.2).</summary>
internal sealed record StaticRegionDiffDto(
    IReadOnlyList<string> AddedTools,
    IReadOnlyList<string> RemovedTools,
    IReadOnlyList<string> AddedSources,
    IReadOnlyList<string> RemovedSources);
