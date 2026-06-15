using System.Net;
using Ctxman.Tests.Support;

namespace Ctxman.Tests.Api;

/// <summary>
/// Spec §4.1: Infrastruktur-Endpunkte (/healthz, /metrics, /openapi, /scalar) sind tenant- und
/// auth-frei. In den Modi <c>api_key</c>/<c>jwt</c> würden k8s-Liveness/Readiness-Probes und der
/// Prometheus-Scraper keinen Tenant-Key liefern können — die Tenant-Resolution darf sie daher
/// nicht mit 401 blockieren. Reguläre Endpunkte bleiben geschützt (siehe ApiKeyAuthTests/JwtAuthTests).
/// </summary>
public sealed class InfraAuthBypassTests
{
    [Fact] // Spec §4.1: /healthz ist auch ohne Key im api_key-Modus erreichbar.
    public async Task Healthz_InApiKeyMode_WithoutKey_Returns200()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "api_key")
            .WithSetting("auth:api_keys:some-key", "tenant-x");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact] // Spec §6 / §4.1: /metrics ist auch ohne Key im api_key-Modus scrapebar.
    public async Task Metrics_InApiKeyMode_WithoutKey_Returns200()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "api_key")
            .WithSetting("auth:api_keys:some-key", "tenant-x");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact] // Spec §4.1: ein regulärer, tenant-gescopter Endpunkt bleibt ohne Key 401.
    public async Task BusinessEndpoint_InApiKeyMode_WithoutKey_Returns401()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "api_key")
            .WithSetting("auth:api_keys:some-key", "tenant-x");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
