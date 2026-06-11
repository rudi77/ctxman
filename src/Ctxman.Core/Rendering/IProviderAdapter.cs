namespace Ctxman.Core.Rendering;

/// <summary>
/// Art eines <see cref="CacheBreakpoint"/>. Mindestens das Ende der Static-Region wird markiert
/// (Spec §4.6) — sofern der Provider Prompt-Caching unterstützt, sonst leere Breakpoint-Liste.
/// </summary>
public enum CacheBreakpointKind
{
    StaticRegionEnd,
}

/// <summary>
/// Markierung, an welcher Position des gerenderten Outputs ein Provider-Cache-Breakpoint
/// platziert werden soll (Spec §4.6). <see cref="Index"/> ist die provider-spezifische Position
/// (z. B. Message-Index), die der Adapter auflöst.
/// </summary>
public sealed record CacheBreakpoint(
    CacheBreakpointKind Kind,
    int Index);

/// <summary>
/// Ergebnis eines <see cref="IProviderAdapter.Render"/>-Aufrufs (Spec §4.3, §4.6).
/// <see cref="RequestFragment"/> ist provider-spezifisch (Anthropic: <c>{ system, tools[], messages[] }</c>,
/// OpenAI: <c>{ tools[], messages[] }</c>). <see cref="BuiltinTools"/> enthält das
/// <c>expand_context_ref</c>-Tool im Provider-Schema.
/// </summary>
public sealed record RenderResult(
    object RequestFragment,
    IReadOnlyList<CacheBreakpoint> CacheBreakpoints,
    IReadOnlyList<object> BuiltinTools);

/// <summary>
/// Provider-Adapter (Spec §4.6): erzeugt aus dem neutralen <see cref="RenderModel"/> das
/// Wire-Format eines konkreten LLM-Providers. ctxman ist provider-agnostisch; das Domänenmodell
/// kennt keinen Provider, erst der Adapter mappt Rollen/Kinds, platziert System-Prompt und
/// Tool-Defs und empfiehlt Cache-Breakpoints. Adapter sind zustandslos (Spec §11).
/// </summary>
public interface IProviderAdapter
{
    /// <summary>Offener Provider-Bezeichner, gegen den <c>render</c> auflöst (Spec §4.3).</summary>
    string Provider { get; }

    RenderResult Render(RenderModel model);
}
