namespace Ctxman.Api.Compaction;

/// <summary>
/// Konfiguration des ctxman-eigenen Compaction-LLM-Backends (Spec §8).
/// Credentials kommen ausschließlich aus der Standard-.NET-Konfigurationskette
/// (Umgebungsvariablen / appsettings / Secret-Provider) — nie hart codiert (Non-Goal N5).
/// Bindung aus Sektion <c>Compaction</c>.
/// </summary>
public sealed class CompactionOptions
{
    /// <summary>Konfigurations-Sektion (<c>Compaction</c>).</summary>
    public const string SectionName = "Compaction";

    /// <summary>Konfiguration für den Anthropic-Adapter.</summary>
    public AnthropicOptions Anthropic { get; set; } = new();

    /// <summary>Konfiguration für den Azure-OpenAI-Adapter.</summary>
    public AzureOpenAiOptions AzureOpenAi { get; set; } = new();
}

/// <summary>
/// Anthropic-spezifische Einstellungen (API-Key, Base-URL, API-Version, Max-Tokens).
/// Credentials kommen ausschließlich aus der Konfigurationskette (Non-Goal N5).
/// </summary>
public sealed class AnthropicOptions
{
    /// <summary>Anthropic API-Key. Muss via Konfiguration gesetzt werden.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Basis-URL der Anthropic Messages API. Default: <c>https://api.anthropic.com</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>Anthropic-API-Versionssstring im <c>anthropic-version</c>-Header. Default: <c>2023-06-01</c>.</summary>
    public string ApiVersion { get; set; } = "2023-06-01";

    /// <summary>Maximale Anzahl Output-Tokens pro Compaction-Aufruf. Default: 1024.</summary>
    public int MaxTokens { get; set; } = 1024;
}

/// <summary>
/// Azure-OpenAI-spezifische Einstellungen (Endpoint, API-Key, Deployment, API-Version).
/// Credentials kommen ausschließlich aus der Konfigurationskette (Non-Goal N5).
/// </summary>
public sealed class AzureOpenAiOptions
{
    /// <summary>Azure-OpenAI-Ressourcen-Endpunkt, z. B. <c>https://my-resource.openai.azure.com</c>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Azure-OpenAI API-Key. Muss via Konfiguration gesetzt werden.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Deployment-Name, z. B. <c>gpt-4o-mini</c>.</summary>
    public string Deployment { get; set; } = string.Empty;

    /// <summary>Azure-OpenAI API-Version, z. B. <c>2024-02-01</c>.</summary>
    public string ApiVersion { get; set; } = "2024-02-01";
}
