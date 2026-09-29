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

    // ---- Port-Rückfluss V5: Tool-Paarung im Adapter ----

    /// <summary>
    /// Die Lage, in der ein Ergebnis NACHGEREICHT wird: Aufruf, dazwischen anderer Verkehr, dann
    /// erst die Antwort — typisch für das synthetische Error-tool_result, mit dem das SDK laut I5
    /// offene Units schließt. Nach seq sortiert steht die Antwort hinter dem Zwischenverkehr.
    /// </summary>
    private static RenderModel LateResultModel() => new(
        StaticItems: [],
        Messages:
        [
            new(Role.User, [new RenderContentBlock(RenderBlockKind.Text, "los", null, null)]),
            new(Role.Assistant, [new RenderContentBlock(RenderBlockKind.ToolCall, "{}", "c1", "run_shell")]),
            new(Role.User, [new RenderContentBlock(RenderBlockKind.Text, "und weiter", null, null)]),
            new(Role.Tool, [new RenderContentBlock(RenderBlockKind.ToolResult, "(tool no longer available)", "c1", null)]),
        ],
        EmitExpandContextRef: false,
        TokensTotal: 0);

    private static JsonElement Messages(RenderResult result)
    {
        using var doc = JsonDocument.Parse(CanonicalJson.Serialize(result.RequestFragment));
        return doc.RootElement.GetProperty("messages").Clone();
    }

    [Fact] // OpenAI: auf tool_calls folgt UNMITTELBAR die role:tool-Antwort.
    public void OpenAi_LateToolResult_DirectlyFollowsItsCall()
    {
        var messages = Messages(new OpenAiChatAdapter().Render(LateResultModel()));

        var callIndex = Enumerable.Range(0, messages.GetArrayLength())
            .Single(i => messages[i].TryGetProperty("tool_calls", out _));
        var next = messages[callIndex + 1];

        Assert.Equal("tool", next.GetProperty("role").GetString());
        Assert.Equal("c1", next.GetProperty("tool_call_id").GetString());
        // Verlustfrei: nichts fällt weg, der Zwischenverkehr steht danach.
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("und weiter", messages[callIndex + 2].GetProperty("content").GetString());
    }

    [Fact] // Anthropic: tool_result in der UNMITTELBAR folgenden Nachricht.
    public void Anthropic_LateToolResult_IsInTheNextMessage()
    {
        var messages = Messages(new AnthropicMessagesAdapter().Render(LateResultModel()));

        var callIndex = Enumerable.Range(0, messages.GetArrayLength())
            .Single(i => messages[i].GetProperty("content").EnumerateArray()
                .Any(b => b.GetProperty("type").GetString() == "tool_use"));
        var next = messages[callIndex + 1];

        Assert.Equal("user", next.GetProperty("role").GetString());
        var first = next.GetProperty("content")[0];
        Assert.Equal("tool_result", first.GetProperty("type").GetString());
        Assert.Equal("c1", first.GetProperty("tool_use_id").GetString());
        Assert.Equal(4, messages.GetArrayLength());
    }

    [Fact] // Normalfall: folgt die Antwort ohnehin direkt, ist die Ausgabe byte-gleich.
    public void Pairing_LeavesAlreadyPairedOutputUnchanged()
    {
        // SampleModel hat Aufruf und Antwort bereits direkt hintereinander.
        var openAi = CanonicalJson.Serialize(new OpenAiChatAdapter().Render(SampleModel()).RequestFragment);
        var anthropic = CanonicalJson.Serialize(new AnthropicMessagesAdapter().Render(SampleModel()).RequestFragment);

        var openAiMessages = JsonDocument.Parse(openAi).RootElement.GetProperty("messages");
        Assert.Equal(new[] { "system", "user", "assistant", "tool" },
            openAiMessages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));

        var anthropicMessages = JsonDocument.Parse(anthropic).RootElement.GetProperty("messages");
        Assert.Equal(new[] { "user", "assistant", "user" },
            anthropicMessages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
    }

    [Fact] // Verlustfrei: eine Antwort ohne zugehörigen Aufruf bleibt stehen, statt zu verschwinden.
    public void OpenAi_ToolResultWithoutCall_StaysInPlace()
    {
        var model = new RenderModel(
            StaticItems: [],
            Messages:
            [
                new(Role.User, [new RenderContentBlock(RenderBlockKind.Text, "hi", null, null)]),
                new(Role.Tool, [new RenderContentBlock(RenderBlockKind.ToolResult, "alt", "weg", null)]),
            ],
            EmitExpandContextRef: false,
            TokensTotal: 0);

        var messages = Messages(new OpenAiChatAdapter().Render(model));

        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("weg", messages[1].GetProperty("tool_call_id").GetString());
    }
}
