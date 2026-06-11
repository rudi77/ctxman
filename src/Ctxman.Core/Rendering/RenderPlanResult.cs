namespace Ctxman.Core.Rendering;

/// <summary>
/// Ergebnis des deterministischen Render-Plans (Spec §4.6). Trägt das neutrale
/// <see cref="RenderModel"/> (bereits Static→Working sortiert, coalesced; I4), die offenen
/// Units für die I5-Vollständigkeitsprüfung sowie die abgeleiteten Render-Metadaten.
///
/// Der Planner wirft bei unvollständigen Units NICHT — er meldet sie in
/// <see cref="OpenToolCallIds"/>; der Render-Endpunkt entscheidet über das <c>422</c> (Spec §2 I5).
/// </summary>
/// <param name="Model">Neutrales, sortiertes und coalesced Render-Input für den Provider-Adapter.</param>
/// <param name="OpenToolCallIds">
/// tool_call_ids gerenderter <c>tool_call</c>-Segmente ohne korrespondierendes render-eligibles
/// <c>tool_result</c>. Leer ⇔ alle Units vollständig (Spec §2 I5).
/// </param>
/// <param name="TokensTotal">
/// Summe der pro-Segment gezählten Tokens der gerenderten Segmente (WP2-Heuristik auf den
/// Segmenten; keine Neuberechnung über einen Provider-Tokenizer).
/// </param>
/// <param name="WatermarkState">Abgeleiteter Watermark-Status ok|soft|hard|emergency (Spec §3.1).</param>
/// <param name="StaticPrefix">
/// Kanonisches Static-Prefix-Objekt (Tool-Defs + System-Prompt), aus dem der Endpunkt den
/// <c>cache_prefix_hash</c> über <see cref="CanonicalJson.ComputeCachePrefixHash"/> berechnet (Spec §6, I4).
/// </param>
public sealed record RenderPlanResult(
    RenderModel Model,
    IReadOnlyList<string> OpenToolCallIds,
    int TokensTotal,
    string WatermarkState,
    IReadOnlyList<RenderStaticItem> StaticPrefix);
