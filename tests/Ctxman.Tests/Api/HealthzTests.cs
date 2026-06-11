using System.Net;
using System.Text.Json;
using Ctxman.Tests.Support;

namespace Ctxman.Tests.Api;

/// <summary>
/// <c>/healthz</c> exponiert den Auth-Modus in den Metadaten (Spec §4.1 / Akzeptanzkriterium 11).
/// Das Wire-Format ist snake_case (CLAUDE.md): Feld <c>auth_mode</c>, Wert <c>none</c>.
/// </summary>
public sealed class HealthzTests
{
    [Fact] // Spec §4.1: /healthz liefert auth_mode "none" (snake_case Feld + Wert).
    public async Task Healthz_ExposesAuthModeNone_InSnakeCase()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("auth_mode", out var authMode),
            "Response muss das snake_case-Feld 'auth_mode' tragen.");
        Assert.Equal("none", authMode.GetString());
    }
}
