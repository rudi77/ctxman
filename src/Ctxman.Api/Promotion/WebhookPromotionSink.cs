using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Core.Promotion;

namespace Ctxman.Api.Promotion;

/// <summary>
/// Webhook-Implementierung von <see cref="IPromotionSink"/>: POST kanonisches JSON an die
/// per-Session konfigurierte <c>promotion.sink.url</c> (Spec §3.3 / §5).
///
/// Der Empfänger erhält: <c>{ fact, source_session, source_turn, kind }</c> (snake_case,
/// identisch mit dem Wire-Format aller anderen Endpunkte).
///
/// Die Senke ist write-only (Non-Goal N2): HTTP-Fehler werden als Exception geworfen —
/// Retry-Logik obliegt dem aufrufenden Worker (WP5-Subtask 6), nicht diesem Adapter.
/// </summary>
public sealed class WebhookPromotionSink : IPromotionSink
{
    // Spec §6: Event-Payload ist kanonisches snake_case-JSON — gleiche Optionen wie die Endpunkte.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;

    public WebhookPromotionSink(HttpClient http)
    {
        _http = http;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(PromotedFact fact, string sinkUrl, CancellationToken ct)
    {
        // Spec §3.3: POST { fact, source_session, source_turn, kind } an die Sink-URL.
        // snake_case-Serialisierung über WireOptions — konsistent mit allen anderen Payloads.
        using var response = await _http.PostAsJsonAsync(sinkUrl, fact, WireOptions, ct);
        response.EnsureSuccessStatusCode();
    }
}
