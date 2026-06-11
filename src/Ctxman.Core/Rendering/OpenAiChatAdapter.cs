using System.Text.Json.Nodes;
using Ctxman.Core.Domain;

namespace Ctxman.Core.Rendering;

/// <summary>
/// Provider-Adapter für die OpenAI Chat Completions API (Spec §4.6). Erzeugt aus dem neutralen,
/// bereits sortierten und coalesced <see cref="RenderModel"/> das OpenAI-Wire-Format
/// <c>{ tools[], messages[] }</c>: der System-Prompt ist die ERSTE Message mit <c>role: system</c>
/// (kein Top-Level-Feld), <c>tool_def</c>-Items wandern in den separaten <c>tools</c>-Parameter
/// (nie in die Message-Liste), <c>tool_call</c> wird zur Assistant-Message mit <c>tool_calls</c>,
/// <c>tool_result</c> zur Message mit <c>role: tool</c>. Zustandslos und damit als DI-Singleton
/// sicher (Spec §11).
/// </summary>
public sealed class OpenAiChatAdapter : IProviderAdapter
{
    public string Provider => "openai";

    public RenderResult Render(RenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var messages = new List<Dictionary<string, object?>>();

        // Spec §4.6: System-Prompt ist bei OpenAI die ERSTE Message (role: system), kein Top-Level-Feld.
        string system = string.Join(
            "\n\n",
            model.StaticItems
                .Where(i => i.Kind != "tool_def")
                .Select(i => i.Content));

        if (system.Length > 0)
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = system,
            });
        }

        // Spec §4.6: tool_def-Items als OpenAI-Function-Tool-Schemas im separaten tools[]-Parameter —
        // nie Teil der Message-Liste.
        var tools = model.StaticItems
            .Where(i => i.Kind == "tool_def")
            .Select(i => BuildFunctionTool(i.Content))
            .ToList();

        foreach (var message in model.Messages)
        {
            messages.AddRange(BuildMessages(message));
        }

        var requestFragment = new Dictionary<string, object?>
        {
            ["tools"] = tools,
            ["messages"] = messages,
        };

        // Spec §4.6: OpenAI Chat Completions unterstützt keine empfohlenen Cache-Breakpoints ⇒ leer.
        var cacheBreakpoints = new List<CacheBreakpoint>();

        // Spec §3.4: expand_context_ref im OpenAI-Function-Schema (Akzeptanzkriterium 6).
        var builtinTools = new List<object> { BuildExpandContextRefTool() };

        return new RenderResult(requestFragment, cacheBreakpoints, builtinTools);
    }

    private static IEnumerable<Dictionary<string, object?>> BuildMessages(RenderMessage message)
    {
        // Spec §4.6: tool_result ⇒ je eigene Message mit role: tool (kein Content-Block-Array).
        if (message.Blocks.Any(b => b.Kind == RenderBlockKind.ToolResult))
        {
            foreach (var block in message.Blocks.Where(b => b.Kind == RenderBlockKind.ToolResult))
            {
                yield return new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = block.ToolCallId,
                    ["content"] = block.Text ?? string.Empty,
                };
            }

            yield break;
        }

        // Spec §4.6: tool_call ⇒ Assistant-Message mit tool_calls[].
        var toolCalls = message.Blocks
            .Where(b => b.Kind == RenderBlockKind.ToolCall)
            .Select(BuildToolCall)
            .ToList();

        string content = string.Concat(
            message.Blocks
                .Where(b => b.Kind == RenderBlockKind.Text)
                .Select(b => b.Text ?? string.Empty));

        var msg = new Dictionary<string, object?>
        {
            ["role"] = RoleWire(message.Role),
            ["content"] = content,
        };

        if (toolCalls.Count > 0)
        {
            msg["tool_calls"] = toolCalls;
        }

        yield return msg;
    }

    private static Dictionary<string, object?> BuildToolCall(RenderContentBlock block) => new()
    {
        ["id"] = block.ToolCallId,
        ["type"] = "function",
        ["function"] = new Dictionary<string, object?>
        {
            ["name"] = block.ToolName,
            ["arguments"] = block.Text ?? string.Empty,
        },
    };

    private static string RoleWire(Role role) => role switch
    {
        Role.System => "system",
        Role.Assistant => "assistant",
        Role.Tool => "tool",
        _ => "user",
    };

    private static object BuildFunctionTool(string content) => new Dictionary<string, object?>
    {
        ["type"] = "function",
        ["function"] = ParseJsonOrString(content),
    };

    private static object BuildExpandContextRefTool() => new Dictionary<string, object?>
    {
        ["type"] = "function",
        ["function"] = new Dictionary<string, object?>
        {
            ["name"] = "expand_context_ref",
            ["description"] = "Expand an externalized context segment by its segment_id to retrieve its full content.",
            ["parameters"] = new Dictionary<string, object?>
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
        },
    };

    // Tool-Def-Content trägt das Tool-Schema. Als JSON parsen, damit es als Objekt nistet und
    // CanonicalJson es kanonisch re-sortiert (statt als string-escaped Blob); sonst durchreichen.
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
