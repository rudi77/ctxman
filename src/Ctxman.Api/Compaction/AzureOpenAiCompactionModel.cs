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
        var systemPrompt = ResolveSystemPrompt(request.PromptTemplateId);
        var userContent = BuildUserContent(request.Window);

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

        var summary = result?.Choices?.FirstOrDefault()?.Message?.Content
            ?? string.Empty;

        return new CompactionResult(summary);
    }

    private static string ResolveSystemPrompt(string templateId) => templateId switch
    {
        // Minimale Built-in-Templates — Spec §8 verlangt keine spezifische Template-Registry.
        "fact-extraction-v1" =>
            "Extract the key facts and decisions from the conversation below as a concise bulleted list.",
        _ => // "default-v1" und alle anderen
            "Summarize the following conversation segments concisely, preserving all essential context.",
    };

    private static string BuildUserContent(IReadOnlyList<WindowItem> window)
    {
        var parts = window.Select(item =>
            item.Kind is not null
                ? $"[{item.Kind}]\n{item.Content}"
                : item.Content);

        return string.Join("\n\n---\n\n", parts);
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
        ChatMessage? Message);
}
