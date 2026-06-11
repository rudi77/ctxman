using System.Text.Json;
using Ctxman.Tests.Support;

namespace Ctxman.Tests.Api;

/// <summary>
/// Tenant-Pipeline (Spec §4.1 / Akzeptanzkriterien 9+10): die Middleware löst jeden Request vor
/// den Handlern zu einem Tenant auf. Beobachtet über die test-only Probe <c>GET /test/whoami</c>,
/// die den aufgelösten <c>ITenantContext.TenantId</c> als snake_case-Feld <c>tenant_id</c> liefert.
/// </summary>
public sealed class TenantResolutionTests
{
    private static async Task<string?> ResolvedTenantAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("tenant_id").GetString();
    }

    [Fact] // Spec §4.1: Header X-Tenant-Id bestimmt den Tenant.
    public async Task Header_DeterminesTenant()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = factory.WithProbe().CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Add("X-Tenant-Id", "t1");
        var response = await client.SendAsync(request);

        Assert.Equal("t1", await ResolvedTenantAsync(response));
    }

    [Fact] // Spec §4.1: ohne Header gilt der Default-Tenant "default".
    public async Task NoHeader_FallsBackToDefaultTenant()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = factory.WithProbe().CreateClient();

        var response = await client.GetAsync("/test/whoami");

        Assert.Equal("default", await ResolvedTenantAsync(response));
    }

    [Fact] // Spec §4.1: konfigurierbarer Header-Name (auth:tenant_header) wird respektiert.
    public async Task ConfiguredHeaderName_IsHonored()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:tenant_header", "X-Org");
        var client = factory.WithProbe().CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Add("X-Org", "org-7");
        // Der Default-Header darf jetzt nicht mehr greifen.
        request.Headers.Add("X-Tenant-Id", "ignored");
        var response = await client.SendAsync(request);

        Assert.Equal("org-7", await ResolvedTenantAsync(response));
    }
}
