using Ctxman.Core.Domain;

namespace Ctxman.Core.Rendering;

/// <summary>
/// Region eines Content-Blocks innerhalb einer <see cref="RenderMessage"/> (Spec §4.6).
/// </summary>
public enum RenderBlockKind
{
    Text,
    ToolCall,
    ToolResult,
}

/// <summary>
/// Ein Content-Block einer Working-Message. Provider-agnostisch — der Adapter mappt ihn
/// in das Wire-Format des Providers (z. B. <c>tool_result</c> als User-Block bei Anthropic
/// vs. <c>role: tool</c> bei OpenAI; Spec §4.6).
/// </summary>
public sealed record RenderContentBlock(
    RenderBlockKind Kind,
    string? Text,
    string? ToolCallId,
    string? ToolName);

/// <summary>
/// Ein Element der Static-Region (system-Prompt oder <c>tool_def</c>; Spec §2.3, §4.6).
/// Static-Items sind bereits vom Planner kanonisch sortiert. <see cref="ContentHash"/> dient
/// dem deterministischen Render-Prefix (Spec §4.6, I4).
/// </summary>
public sealed record RenderStaticItem(
    string? Source,
    string Kind,
    string Content,
    string ContentHash,
    int Tokens);

/// <summary>
/// Eine Working-Region-Message: eine Rolle und ihre — durch Coalescing zusammengefassten —
/// Content-Blocks (Spec §4.6). Die Liste ist bereits seq-sortiert und coalesced vom Planner.
/// </summary>
public sealed record RenderMessage(
    Role Role,
    IReadOnlyList<RenderContentBlock> Blocks);

/// <summary>
/// Neutrales, bereits sortiertes und coalesced Render-Input für einen
/// <see cref="IProviderAdapter"/> (Spec §4.6). Trägt keine Provider-Semantik — der Adapter
/// erzeugt daraus das Wire-Format.
/// </summary>
public sealed record RenderModel(
    IReadOnlyList<RenderStaticItem> StaticItems,
    IReadOnlyList<RenderMessage> Messages,
    bool EmitExpandContextRef,
    int TokensTotal);
