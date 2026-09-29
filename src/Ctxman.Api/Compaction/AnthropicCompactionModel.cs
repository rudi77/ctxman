using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ctxman.Core.Compaction;
using Microsoft.Extensions.Options;

namespace Ctxman.Api.Compaction;

/// <summary>
/// Compaction-LLM-Adapter für die Anthropic Messages API (Spec §8).
/// Ruft NIEMALS das LLM des Agents auf (Non-Goal N1) — dies ist das ctxman-eigene,
/// günstige Compaction-Backend. Zustandslos und damit als DI-Singleton sicher.
/// Credentials kommen ausschließlich aus der Konfigurationskette (Non-Goal N5).
/// </summary>
public sealed class AnthropicCompactionModel : ICompactionModel
{
    // Spec §8: Anthropic und OpenAI verwenden snake_case-Feldnamen im Wire-Format.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AnthropicOptions _options;

    public AnthropicCompactionModel(HttpClient http, IOptions<CompactionOptions> options)
    {
        _http = http;
        _options = options.Value.Anthropic;
    }

    /// <inheritdoc/>
    public async Task<CompactionResult> SummarizeAsync(CompactionRequest request, CancellationToken ct)
    {
        var systemPrompt = CompactionPrompts.SystemPrompt(request.PromptTemplateId);
        var userContent = CompactionPrompts.UserContent(request.Window);

        var body = new AnthropicRequest(
            Model: request.Model,
            MaxTokens: _options.MaxTokens,
            System: systemPrompt,
            Messages:
            [
                new AnthropicMessage(Role: "user", Content: userContent),
            ]);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(_options.BaseUrl), "/v1/messages"));

        // Spec §8: Auth via x-api-key + anthropic-version (Non-Goal N5 — aus Konfiguration).
        httpRequest.Headers.Add("x-api-key", _options.ApiKey);
        httpRequest.Headers.Add("anthropic-version", _options.ApiVersion);
        httpRequest.Content = JsonContent.Create(body, options: WireOptions);

        using var response = await _http.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>(WireOptions, ct);

        // Ein bei max_tokens abgeschnittenes Summary ist kein Summary: die Compaction ersetzte
        // die Quellen durch einen Text ohne Ende, eine Fact-Extraction verlöre die hinteren
        // Fakten. Spec §3.1: lieber ein expliziter, retrybarer Fehler als ein lossy Notabwurf.
        if (result?.StopReason == "max_tokens")
        {
            throw new InvalidOperationException(
                $"Compaction-Antwort bei max_tokens={_options.MaxTokens} abgeschnitten " +
                $"(Template {request.PromptTemplateId}) — Compaction:Anthropic:MaxTokens erhöhen.");
        }

        var summary = result?.Content?.FirstOrDefault(b => b.Type == "text")?.Text
            ?? string.Empty;

        return new CompactionResult(summary);
    }

    // --- Wire-Typen (Anthropic Messages API) ---

    private sealed record AnthropicRequest(
        string Model,
        int MaxTokens,
        string System,
        IReadOnlyList<AnthropicMessage> Messages);

    private sealed record AnthropicMessage(
        string Role,
        string Content);

    private sealed record AnthropicResponse(
        IReadOnlyList<AnthropicContentBlock>? Content,
        string? StopReason = null);

    private sealed record AnthropicContentBlock(
        string Type,
        string? Text);
}
