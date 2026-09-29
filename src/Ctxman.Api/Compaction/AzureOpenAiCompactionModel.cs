using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ctxman.Core.Compaction;
using Microsoft.Extensions.Options;

namespace Ctxman.Api.Compaction;

/// <summary>
/// Compaction-LLM-Adapter für die Azure OpenAI Chat Completions API (Spec §8).
/// Ruft NIEMALS das LLM des Agents auf (Non-Goal N1) — dies ist das ctxman-eigene,
/// günstige Compaction-Backend. Zustandslos und damit als DI-Singleton sicher.
/// Credentials kommen ausschließlich aus der Konfigurationskette (Non-Goal N5).
/// </summary>
public sealed class AzureOpenAiCompactionModel : ICompactionModel
{
    // Spec §8: Anthropic und OpenAI verwenden snake_case-Feldnamen im Wire-Format.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AzureOpenAiOptions _options;

    public AzureOpenAiCompactionModel(HttpClient http, IOptions<CompactionOptions> options)
    {
        _http = http;
        _options = options.Value.AzureOpenAi;
    }

    /// <inheritdoc/>
    public async Task<CompactionResult> SummarizeAsync(CompactionRequest request, CancellationToken ct)
    {
        var systemPrompt = CompactionPrompts.SystemPrompt(request.PromptTemplateId);
        var userContent = CompactionPrompts.UserContent(request.Window);

        // Spec §8: Azure OpenAI Chat Completions endpoint —
        // {endpoint}/openai/deployments/{deployment}/chat/completions?api-version=...
        var url = $"{_options.Endpoint.TrimEnd('/')}/openai/deployments/" +
                  $"{_options.Deployment}/chat/completions?api-version={_options.ApiVersion}";

        var body = new AzureOpenAiRequest(
            Messages:
            [
                new ChatMessage(Role: "system", Content: systemPrompt),
                new ChatMessage(Role: "user", Content: userContent),
            ]);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);

        // Spec §8: Auth via api-key header (Non-Goal N5 — aus Konfiguration).
        httpRequest.Headers.Add("api-key", _options.ApiKey);
        httpRequest.Content = JsonContent.Create(body, options: WireOptions);

        using var response = await _http.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AzureOpenAiResponse>(WireOptions, ct);
        var choice = result?.Choices?.FirstOrDefault();

        // Abgeschnitten ist kein Summary (siehe AnthropicCompactionModel) — Spec §3.1: lieber ein
        // expliziter, retrybarer Fehler als ein lossy Notabwurf.
        if (choice?.FinishReason == "length")
        {
            throw new InvalidOperationException(
                $"Compaction-Antwort abgeschnitten (finish_reason=length, Template " +
                $"{request.PromptTemplateId}) — das Kontextfenster des Deployments reicht nicht.");
        }

        var summary = choice?.Message?.Content
            ?? string.Empty;

        return new CompactionResult(summary);
    }

    // --- Wire-Typen (Azure OpenAI Chat Completions API) ---

    private sealed record AzureOpenAiRequest(
        IReadOnlyList<ChatMessage> Messages);

    private sealed record ChatMessage(
        string Role,
        string Content);

    private sealed record AzureOpenAiResponse(
        IReadOnlyList<AzureOpenAiChoice>? Choices);

    private sealed record AzureOpenAiChoice(
        ChatMessage? Message,
        string? FinishReason = null);
}
