using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Core.Rendering;

namespace Ctxman.Tests.Rendering;

/// <summary>
/// Unit-Tests für die Provider-Adapter (Spec §4.3, §4.6; WP3-Akzeptanz 2, 6, 12).
/// Konstruiert <see cref="RenderModel"/> direkt — kein Session/DB nötig.
/// </summary>
public sealed class ProviderAdapterTests
{
    private static readonly Ulid SessionId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV");

    private static RenderModel SampleModel(bool withExternalized = false)
    {
        var staticItems = new List<RenderStaticItem>
        {
            new("core", "system_prompt", "You are helpful.", CanonicalJson.ContentHash("You are helpful."), 5),
            new("core", "tool_def", """{"name":"search","description":"Search the web","input_schema":{"type":"object","properties":{"q":{"type":"string"}}}}""",
                CanonicalJson.ContentHash("""{"name":"search","description":"Search the web","input_schema":{"type":"object","properties":{"q":{"type":"string"}}}}"""), 10),
        };

        var messages = new List<RenderMessage>
        {
            new(Role.User, new[] { new RenderContentBlock(RenderBlockKind.Text, "hello", null, null) }),
            new(Role.Assistant, new[]
            {
                new RenderContentBlock(RenderBlockKind.ToolCall, """{"q":"ctxman"}""", "call-1", "search"),
                new RenderContentBlock(RenderBlockKind.Text, "thinking…", null, null),
            }),
            new(Role.Tool, new[] { new RenderContentBlock(RenderBlockKind.ToolResult, "result body", "call-1", null) }),
        };

        return new RenderModel(staticItems, messages, withExternalized, TokensTotal: 42);
    }

    [Fact]
    public void Anthropic_Provider_ReturnsAnthropic()
    {
        Assert.Equal("anthropic", new AnthropicMessagesAdapter().Provider);
    }

    [Fact]
    public void OpenAi_Provider_ReturnsOpenai()
    {
        Assert.Equal("openai", new OpenAiChatAdapter().Provider);
    }

    [Fact] // Akzeptanz 2: Anthropic request_fragment hat system, tools, messages.
    public void Anthropic_RequestFragment_HasSystemToolsMessages_NotInMessageList()
    {
        var result = new AnthropicMessagesAdapter().Render(SampleModel());
        using var doc = JsonDocument.Parse(CanonicalJson.Serialize(result.RequestFragment));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("system", out _));
        Assert.True(root.TryGetProperty("tools", out var tools));
        Assert.True(root.TryGetProperty("messages", out var messages));
        Assert.Equal(JsonValueKind.Array, tools.ValueKind);
        Assert.Equal(JsonValueKind.Array, messages.ValueKind);

        var allMessageText = messages.GetRawText();
        Assert.DoesNotContain("Search the web", allMessageText);
    }

    [Fact] // Akzeptanz 2: OpenAI hat tools + messages, kein top-level system; system ist erste Message.
    public void OpenAi_RequestFragment_HasToolsAndMessages_NoTopLevelSystem()
    {
        var result = new OpenAiChatAdapter().Render(SampleModel());
        using var doc = JsonDocument.Parse(CanonicalJson.Serialize(result.RequestFragment));
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("system", out _));
        Assert.True(root.TryGetProperty("tools", out var tools));
        Assert.True(root.TryGetProperty("messages", out var messages));
        Assert.Equal(JsonValueKind.Array, tools.ValueKind);

        var first = messages[0];
        Assert.Equal("system", first.GetProperty("role").GetString());
        Assert.Contains("You are helpful.", first.GetProperty("content").GetString());
    }

    [Fact] // Akzeptanz 12: Anthropic cache_breakpoints non-empty at static region end.
    public void Anthropic_CacheBreakpoints_NonEmpty()
    {
        var result = new AnthropicMessagesAdapter().Render(SampleModel());
        Assert.NotEmpty(result.CacheBreakpoints);
        Assert.Contains(result.CacheBreakpoints, bp => bp.Kind == CacheBreakpointKind.StaticRegionEnd);
    }

    [Fact] // Akzeptanz 12: OpenAI cache_breakpoints empty.
    public void OpenAi_CacheBreakpoints_Empty()
    {
        var result = new OpenAiChatAdapter().Render(SampleModel());
        Assert.Empty(result.CacheBreakpoints);
    }

    [Fact] // Akzeptanz 6: builtin_tools enthält expand_context_ref mit segment_id.
    public void Anthropic_BuiltinTools_ContainExpandContextRefWithSegmentId()
    {
        var result = new AnthropicMessagesAdapter().Render(SampleModel(withExternalized: true));
        var json = CanonicalJson.Serialize(result.BuiltinTools);
        Assert.Contains("expand_context_ref", json);
        Assert.Contains("segment_id", json);
        Assert.Contains("input_schema", json);
    }

    [Fact] // Akzeptanz 6: OpenAI builtin_tools als function mit segment_id.
    public void OpenAi_BuiltinTools_ContainExpandContextRefFunction()
    {
        var result = new OpenAiChatAdapter().Render(SampleModel(withExternalized: true));
        var json = CanonicalJson.Serialize(result.BuiltinTools);
        Assert.Contains("expand_context_ref", json);
        Assert.Contains("segment_id", json);
        Assert.Contains("function", json);
    }

    [Fact] // Coalesced multi-block message bleibt erhalten (Anthropic).
    public void Anthropic_CoalescedMessage_PreservesMultipleBlocks()
    {
        var result = new AnthropicMessagesAdapter().Render(SampleModel());
        using var doc = JsonDocument.Parse(CanonicalJson.Serialize(result.RequestFragment));
        var messages = doc.RootElement.GetProperty("messages");

        var assistant = messages.EnumerateArray().First(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal(2, assistant.GetProperty("content").GetArrayLength());
    }
}
