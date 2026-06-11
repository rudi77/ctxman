using System.Text.Json.Nodes;

namespace Ctxman.Core.Rendering;

/// <summary>
/// Baut das provider-agnostische Static-Prefix-Objekt für <c>cache_prefix_hash</c> (Spec §6, I4).
/// Spiegelt die Static-Region (System-Prompt + Tool-Defs) unabhängig vom Adapter.
/// </summary>
public static class RenderStaticPrefix
{
    public static object BuildHashInput(IReadOnlyList<RenderStaticItem> items)
    {
        string system = string.Join(
            "\n\n",
            items
                .Where(i => i.Kind != "tool_def")
                .Select(i => i.Content));

        var tools = items
            .Where(i => i.Kind == "tool_def")
            .Select(i => ParseJsonOrString(i.Content))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["system"] = system,
            ["tools"] = tools,
        };
    }

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
