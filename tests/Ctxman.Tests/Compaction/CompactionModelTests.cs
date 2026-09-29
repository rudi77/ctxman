using System.Net;
using System.Text;
using System.Text.Json;
using Ctxman.Api.Compaction;
using Ctxman.Core.Compaction;
using Microsoft.Extensions.Options;

namespace Ctxman.Tests.Compaction;

/// <summary>
/// Die Compaction-Backends (Spec §8) gegen eine gefälschte HTTP-Gegenstelle — kein Netzwerk.
/// Geprüft wird, was beim Provider ankommt (Prompt, Fenster) und wie eine unvollständige
/// Antwort behandelt wird. Beides sind Lehren aus agentkits eigener Kompaktierung
/// (Commit 5d7a8b3), übertragen auf ctxman.
/// </summary>
public sealed class CompactionModelTests
{
    /// <summary>Antwortet mit festem JSON und merkt sich den zuletzt gesendeten Body.</summary>
    private sealed class FakeHandler(string responseJson) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static AnthropicCompactionModel Anthropic(FakeHandler handler) =>
        new(new HttpClient(handler), Options.Create(new CompactionOptions
        {
            Anthropic = new AnthropicOptions { ApiKey = "test", MaxTokens = 1024 },
        }));

    private static AzureOpenAiCompactionModel Azure(FakeHandler handler) =>
        new(new HttpClient(handler), Options.Create(new CompactionOptions
        {
            AzureOpenAi = new AzureOpenAiOptions
            {
                Endpoint = "https://example.openai.azure.com",
                ApiKey = "test",
                Deployment = "d",
            },
        }));

    private static CompactionRequest Request(string templateId = "default-v1") => new(
        [
            new WindowItem("""{"cmd":"cargo test"}""", "tool_call", "run_shell"),
            new WindowItem("12 passed", "tool_result"),
        ],
        templateId,
        "model");

    // ---- Abgeschnittene Antworten ----

    // Ein bei max_tokens abgeschnittenes Summary ersetzte die Quellen durch einen Text ohne Ende.
    // Spec §3.1: lieber ein expliziter, retrybarer Fehler als ein lossy Notabwurf.
    [Fact]
    public async Task Anthropic_TruncatedResponse_IsAnError_NotASummary()
    {
        var handler = new FakeHandler("""
            {"content":[{"type":"text","text":"GOAL: halb"}],"stop_reason":"max_tokens"}
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Anthropic(handler).SummarizeAsync(Request(), CancellationToken.None));
        Assert.Contains("max_tokens", ex.Message);
    }

    [Fact]
    public async Task Anthropic_CompleteResponse_ReturnsTheSummary()
    {
        var handler = new FakeHandler("""
            {"content":[{"type":"text","text":"GOAL: fertig"}],"stop_reason":"end_turn"}
            """);

        var result = await Anthropic(handler).SummarizeAsync(Request(), CancellationToken.None);

        Assert.Equal("GOAL: fertig", result.Summary);
    }

    [Fact]
    public async Task AzureOpenAi_TruncatedResponse_IsAnError_NotASummary()
    {
        var handler = new FakeHandler("""
            {"choices":[{"message":{"role":"assistant","content":"GOAL: halb"},"finish_reason":"length"}]}
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Azure(handler).SummarizeAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task AzureOpenAi_CompleteResponse_ReturnsTheSummary()
    {
        var handler = new FakeHandler("""
            {"choices":[{"message":{"role":"assistant","content":"GOAL: fertig"},"finish_reason":"stop"}]}
            """);

        var result = await Azure(handler).SummarizeAsync(Request(), CancellationToken.None);

        Assert.Equal("GOAL: fertig", result.Summary);
    }

    // ---- Was beim Modell ankommt ----

    // Vorher sah das Compaction-Modell von einem Tool-Aufruf nur die Argumente — die
    // Zusammenfassung erfuhr nie, WELCHES Werkzeug lief.
    [Fact]
    public async Task ToolName_ReachesTheCompactionModel()
    {
        var handler = new FakeHandler("""{"content":[{"type":"text","text":"x"}],"stop_reason":"end_turn"}""");

        await Anthropic(handler).SummarizeAsync(Request(), CancellationToken.None);

        Assert.Contains("[tool_call: run_shell]", UserText(handler.LastRequestBody!));
    }

    // Ein älteres compaction_summary landet beim nächsten Major GC im Fenster. Ohne die
    // Anweisung, es vollständig mitzunehmen, verdünnt jede weitere Compaction die frühere.
    [Fact]
    public async Task SummarizePrompt_CarriesAnEarlierSummaryOver()
    {
        var handler = new FakeHandler("""{"content":[{"type":"text","text":"x"}],"stop_reason":"end_turn"}""");

        await Anthropic(handler).SummarizeAsync(Request(), CancellationToken.None);

        var system = SystemText(handler.LastRequestBody!);
        Assert.Contains("compaction_summary", system);
        Assert.Contains("carry its content over completely", system);
    }

    // Spec §3.3: „leeres Summary = keine dauerhaften Fakten". Ohne die ausdrückliche Erlaubnis
    // antwortet ein Modell immer mit irgendetwas, und der Zweig wird nie erreicht.
    [Fact]
    public async Task FactExtractionPrompt_PermitsAnEmptyAnswer()
    {
        var handler = new FakeHandler("""{"content":[{"type":"text","text":""}],"stop_reason":"end_turn"}""");

        var result = await Anthropic(handler).SummarizeAsync(
            Request("fact-extraction-v1"), CancellationToken.None);

        Assert.Contains("reply with an empty response", SystemText(handler.LastRequestBody!));
        Assert.Equal(string.Empty, result.Summary);
    }

    private static string SystemText(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("system").GetString()!;

    private static string UserText(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
}
