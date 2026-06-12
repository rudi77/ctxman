using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ctxman.Tests.Support;

namespace Ctxman.Tests.Api;

/// <summary>
/// Auth-Modus <c>api_key</c> (Spec §4.1 / WP7 Subtask 3 / Akzeptanzkriterien §api_key):
/// Statischer Key via <c>Authorization: Bearer</c> oder <c>X-Api-Key</c> wird zum konfigurierten
/// Tenant aufgelöst. Fehlender/unbekannter Key ⇒ 401. Tenant kommt ausschließlich aus dem
/// Key→Tenant-Mapping, nie aus Body/Header.
/// </summary>
public sealed class ApiKeyAuthTests
{
    private static async Task<string?> ResolvedTenantAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("tenant_id").GetString();
    }

    [Fact] // Spec §4.1: gültiger Key via Authorization: Bearer → 200, korrekter Tenant.
    public async Task BearerToken_ValidKey_Returns200AndResolvedTenant()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "api_key")
            .WithSetting("auth:api_keys:secret-key-1", "tenant-a");
        var client = factory.WithProbe().CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key-1");
        var response = await client.SendAsync(request);

        Assert.Equal("tenant-a", await ResolvedTenantAsync(response));
    }

    [Fact] // Spec §4.1: gültiger Key via X-Api-Key-Header → 200, korrekter Tenant.
    public async Task XApiKeyHeader_ValidKey_Returns200AndResolvedTenant()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "api_key")
            .WithSetting("auth:api_keys:secret-key-2", "tenant-b");
        var client = factory.WithProbe().CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Add("X-Api-Key", "secret-key-2");
        var response = await client.SendAsync(request);

        Assert.Equal("tenant-b", await ResolvedTenantAsync(response));
    }

    [Fact] // Spec §4.1: kein Authorization- und kein X-Api-Key-Header → 401.
    public async Task MissingKey_Returns401()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "api_key")
            .WithSetting("auth:api_keys:some-key", "tenant-c");
        var client = factory.WithProbe().CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        // Kein Authorization-Header, kein X-Api-Key.
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact] // Spec §4.1: unbekannter/falscher Key → 401.
    public async Task UnknownKey_Returns401()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "api_key")
            .WithSetting("auth:api_keys:correct-key", "tenant-d");
        var client = factory.WithProbe().CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact] // Spec §4.1: zwei Keys → zwei Tenants; Tenant-Isolation: jeder Request erhält seinen eigenen Tenant.
    public async Task TwoKeys_MappedToTwoDifferentTenants_IsolationIsPreserved()
    {
        // Beide Keys auf einer Factory, damit beide auf demselben Host laufen.
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "api_key")
            .WithSetting("auth:api_keys:key-for-alpha", "alpha")
            .WithSetting("auth:api_keys:key-for-beta", "beta");
        var client = factory.WithProbe().CreateClient();

        var requestAlpha = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        requestAlpha.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "key-for-alpha");
        var responseAlpha = await client.SendAsync(requestAlpha);
        var tenantAlpha = await ResolvedTenantAsync(responseAlpha);

        var requestBeta = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        requestBeta.Headers.Add("X-Api-Key", "key-for-beta");
        var responseBeta = await client.SendAsync(requestBeta);
        var tenantBeta = await ResolvedTenantAsync(responseBeta);

        Assert.Equal("alpha", tenantAlpha);
        Assert.Equal("beta", tenantBeta);
        Assert.NotEqual(tenantAlpha, tenantBeta);
    }
}
