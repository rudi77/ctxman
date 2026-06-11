using System.Text.Json.Nodes;
using Ctxman.Core.Domain;

namespace Ctxman.Core.Rendering;

/// <summary>
/// Provider-Adapter für die Anthropic Messages API (Spec §4.6). Erzeugt aus dem neutralen,
/// bereits sortierten und coalesced <see cref="RenderModel"/> das Anthropic-Wire-Format
/// <c>{ system, tools[], messages[] }</c>: der System-Prompt ist Top-Level-Feld (keine Message),
/// <c>tool_def</c>-Items wandern in den separaten <c>tools</c>-Parameter (nie in die Message-Liste),
/// <c>tool_result</c> wird als User-Block, <c>tool_call</c> als Assistant-<c>tool_use</c>-Block
/// gemappt. Zustandslos und damit als DI-Singleton sicher (Spec §11).
/// </summary>
public sealed class AnthropicMessagesAdapter : IProviderAdapter
{
    public string Provider => "anthropic";

    public RenderResult Render(RenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Spec §4.6: System-Prompt ist Top-Level-Feld, NICHT Teil der Message-Liste.
        // Mehrere system-Items werden zu einem System-Text zusammengefügt.
        string system = string.Join(
            "\n\n",
            model.StaticItems
                .Where(i => i.Kind != "tool_def")
                .Select(i => i.Content));

        // Spec §4.6: tool_def-Items als Anthropic-Tool-Schemas im separaten tools[]-Parameter —
        // nie Teil der Message-Liste.
        var tools = model.StaticItems
            .Where(i => i.Kind == "tool_def")
            .Select(i => ParseToolDef(i.Content))
            .ToList();

        var messages = model.Messages
            .Select(BuildMessage)
            .ToList();

        var requestFragment = new Dictionary<string, object?>
        {
            ["system"] = system,
            ["tools"] = tools,
            ["messages"] = messages,
        };

        // Spec §4.6: cache_breakpoints markieren mindestens das Ende der Static-Region. Anthropic
        // unterstützt Prompt-Caching ⇒ Breakpoint an der Static-Region-Grenze (Index = Tool-Count).
        var cacheBreakpoints = new List<CacheBreakpoint>
        {
            new(CacheBreakpointKind.StaticRegionEnd, tools.Count),
        };

        // Spec §3.4: expand_context_ref im Anthropic-Tool-Schema. Das Tool-Def steht als
        // verfügbares Builtin-Tool zur Verfügung (Akzeptanzkriterium 6).
        var builtinTools = new List<object> { BuildExpandContextRefTool() };

        return new RenderResult(requestFragment, cacheBreakpoints, builtinTools);
    }

    private static Dictionary<string, object?> BuildMessage(RenderMessage message)
    {
        // Spec §4.6: Anthropic kennt nur user/assistant. tool_result-Blöcke werden als
        // User-Content-Blöcke geführt; tool wird daher zu user gemappt.
        string role = message.Role switch
        {
            Role.Assistant => "assistant",
            _ => "user",
        };

        var content = message.Blocks
            .Select(BuildContentBlock)
            .ToList();

        return new Dictionary<string, object?>
        {
            ["role"] = role,
            ["content"] = content,
        };
    }

    private static Dictionary<string, object?> BuildContentBlock(RenderContentBlock block) => block.Kind switch
    {
        // Spec §4.6: tool_call ⇒ assistant tool_use-Block.
        RenderBlockKind.ToolCall => new Dictionary<string, object?>
        {
            ["type"] = "tool_use",
            ["id"] = block.ToolCallId,
            ["name"] = block.ToolName,
            ["input"] = ParseInput(block.Text),
        },
        // Spec §4.6: tool_result ⇒ user tool_result-Block (Anthropic-Konvention).
        RenderBlockKind.ToolResult => new Dictionary<string, object?>
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = block.ToolCallId,
            ["content"] = block.Text ?? string.Empty,
        },
        _ => new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = block.Text ?? string.Empty,
        },
    };

    private static object BuildExpandContextRefTool() => new Dictionary<string, object?>
    {
        ["name"] = "expand_context_ref",
        ["description"] = "Expand an externalized context segment by its segment_id to retrieve its full content.",
        ["input_schema"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["segment_id"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                },
            },
            ["required"] = new List<object?> { "segment_id" },
        },
    };

    // Tool-Def-Content trägt das Tool-Schema. Als JSON parsen, damit es als Objekt nistet und
    // CanonicalJson es kanonisch re-sortiert (statt als string-escaped Blob); sonst durchreichen.
    private static object ParseToolDef(string content) => ParseJsonOrString(content);

    private static object ParseInput(string? content) => ParseJsonOrString(content ?? string.Empty);

    private static object ParseJsonOrString(string content)
    {
        try
        {
            return JsonNode.Parse(content) ?? content;
        }
        catch (System.Text.Json.JsonException)
        {
            return content;
        }
    }
}
