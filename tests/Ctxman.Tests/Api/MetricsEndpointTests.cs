using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Tests.Support;

namespace Ctxman.Tests.Api;

/// <summary>
/// Acceptance tests for the Prometheus scrape endpoint <c>GET /metrics</c> (Spec §6; WP7 AC9).
/// Verifies that all required metric families are registered at startup (visible even at 0)
/// and that counters/histograms change after the corresponding operations.
/// </summary>
public sealed class MetricsEndpointTests
{
    private const string Tenant = "tenant-metrics";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    /// <summary>
    /// Scrape <c>/metrics</c> and return the full response body as a string.
    /// The endpoint must return 200 in text/plain format (unauthenticated in auth:mode=none).
    /// </summary>
    private static async Task<string> ScrapeMetricsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Parse a numeric sample value from a Prometheus text-format scrape body.
    /// Looks for lines whose first whitespace-separated token equals <paramref name="seriesName"/>.
    /// The second token (after labels stripped by Prometheus on sample lines) is the double value.
    /// Returns 0 if the series is not found (metric not yet populated but will appear after
    /// developer implements registration).
    ///
    /// Sample lines examples:
    ///   ctxman_epoch_bumps_total 3
    ///   ctxman_render_duration_seconds_count 5
    ///   ctxman_render_duration_seconds_bucket{le="0.005"} 0
    /// The helper matches the exact series name (no labels), so it correctly skips _bucket lines
    /// when asked for _count.
    /// </summary>
    private static double ParseMetricValue(string scrapeText, string seriesName)
    {
        foreach (var rawLine in scrapeText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || line.Length == 0)
                continue;

            // A sample line may be "name value" or "name{labels} value".
            // Extract the metric name (everything before '{' or whitespace).
            var nameEnd = line.IndexOfAny(['{', ' ', '\t']);
            if (nameEnd < 0)
                continue;

            var name = line[..nameEnd];
            if (name != seriesName)
                continue;

            // Find the value token: skip optional label block, then read the trailing number.
            var valueStart = line.LastIndexOfAny(['}', ' ', '\t']);
            if (valueStart < 0)
                continue;

            var valueToken = line[(valueStart + 1)..].Trim();
            if (double.TryParse(valueToken, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
        }

        return 0.0;
    }

    // -----------------------------------------------------------------------------------------
    // Test 1 — Endpoint exists and all required metric family names are present (Spec §6; AC9)
    // -----------------------------------------------------------------------------------------

    [Fact] // Spec §6: GET /metrics must return 200 and expose all ctxman metric families.
    public async Task Metrics_Endpoint_Returns200_AndContainsAllRequiredMetricFamilies()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        // All seven metric families must appear in the scrape output (even at value 0 because
        // the developer registers them at startup). Assert on family name presence — not on
        // a specific value — so the test passes regardless of concurrent test ordering.
        Assert.Contains("ctxman_render_duration_seconds", body);
        Assert.Contains("ctxman_render_tokens", body);
        Assert.Contains("ctxman_render_watermark_total", body);
        Assert.Contains("ctxman_compaction_ratio", body);
        Assert.Contains("ctxman_page_faults_total", body);
        Assert.Contains("ctxman_eviction_after_expansion_total", body);
        Assert.Contains("ctxman_epoch_bumps_total", body);
    }

    // -----------------------------------------------------------------------------------------
    // Test 2 — A successful render increments ctxman_render_duration_seconds_count (Spec §6)
    // -----------------------------------------------------------------------------------------

    [Fact] // Spec §6: render latency histogram _count must increase after a successful render.
    public async Task Metrics_AfterRender_RenderDurationCount_Increases()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        // Baseline scrape — treat missing as 0 (metric may not be present before first render).
        var before = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var beforeText = await before.Content.ReadAsStringAsync();
        var countBefore = ParseMetricValue(beforeText, "ctxman_render_duration_seconds_count");

        // Create a session with at least one static segment so tokens_total > 0.
        var sessionBody = new
        {
            static_segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "metrics test system prompt", source = "core" },
            },
        };
        var createResponse = await client.PostAsJsonAsync("/v1/sessions", sessionBody);
        createResponse.EnsureSuccessStatusCode();
        var createDoc = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sid = createDoc.GetProperty("session_id").GetString()!;

        // Perform one successful render (turn_advance=true requires Idempotency-Key per Spec §4.3).
        var renderReq = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/render")
        {
            Content = JsonContent.Create(new { provider = "anthropic", turn_advance = true }),
        };
        renderReq.Headers.Add("Idempotency-Key", "metrics-render-idem-1");

        var renderResponse = await client.SendAsync(renderReq);
        Assert.Equal(HttpStatusCode.OK, renderResponse.StatusCode);

        // Post-render scrape — _count must be strictly greater than before.
        var after = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var afterText = await after.Content.ReadAsStringAsync();
        var countAfter = ParseMetricValue(afterText, "ctxman_render_duration_seconds_count");

        Assert.True(
            countAfter > countBefore,
            $"ctxman_render_duration_seconds_count did not increase after render: before={countBefore}, after={countAfter}");
    }

    // -----------------------------------------------------------------------------------------
    // Test 3 — A static-segments PUT (epoch bump) increments ctxman_epoch_bumps_total (Spec §6)
    // -----------------------------------------------------------------------------------------

    [Fact] // Spec §6: epoch-bump counter must increase after PUT /v1/sessions/{sid}/static-segments.
    public async Task Metrics_AfterEpochBump_EpochBumpsTotal_Increases()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        // Baseline scrape.
        var before = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var beforeText = await before.Content.ReadAsStringAsync();
        var bumpsBefore = ParseMetricValue(beforeText, "ctxman_epoch_bumps_total");

        // Create a session with static segments so we have a valid context_version to PUT against.
        var createResponse = await client.PostAsJsonAsync("/v1/sessions", new
        {
            static_segments = new[]
            {
                new { kind = "system_prompt", role = "system", content = "epoch bump test", source = "core" },
                new { kind = "tool_def", role = (string?)null, content = """{"name":"git","description":"Git tool"}""", source = "core" },
            },
        });
        createResponse.EnsureSuccessStatusCode();
        var createDoc = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sid = createDoc.GetProperty("session_id").GetString()!;
        var contextVersion = createDoc.GetProperty("context_version").GetInt64();

        // PUT /v1/sessions/{sid}/static-segments to trigger one epoch bump (Spec §4.2).
        var putReq = new HttpRequestMessage(HttpMethod.Put, $"/v1/sessions/{sid}/static-segments")
        {
            Content = JsonContent.Create(new
            {
                segments = new[]
                {
                    new { kind = "system_prompt", role = "system", content = "epoch bump test updated", source = "core" },
                },
            }),
        };
        putReq.Headers.Add("Idempotency-Key", "metrics-epoch-bump-idem-1");
        putReq.Headers.TryAddWithoutValidation("If-Match", contextVersion.ToString());

        var putResponse = await client.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        // Post-bump scrape — counter must be strictly greater than before.
        var after = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var afterText = await after.Content.ReadAsStringAsync();
        var bumpsAfter = ParseMetricValue(afterText, "ctxman_epoch_bumps_total");

        Assert.True(
            bumpsAfter > bumpsBefore,
            $"ctxman_epoch_bumps_total did not increase after epoch bump: before={bumpsBefore}, after={bumpsAfter}");
    }
}
