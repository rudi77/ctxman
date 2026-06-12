using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Ctxman.Tests.Support;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Ctxman.Tests.Api;

/// <summary>
/// Auth-Modus <c>jwt</c> (Spec §4.1 / WP7 Subtask 4):
/// JwtBearer validiert den Bearer-Token (Issuer, Audience, Signatur). Der Tenant wird aus
/// einem konfigurierbaren Claim (Default <c>tenant_id</c>) im validierten Token gelesen.
/// Fehlender / abgelaufener / falsch-signierter Token ⇒ 401.
/// </summary>
public sealed class JwtAuthTests
{
    // Symmetrischer Test-Schlüssel (HS256). 32+ Bytes erforderlich für HS256 (HMAC-SHA256).
    private static readonly byte[] KeyBytes =
        System.Text.Encoding.UTF8.GetBytes("ctxman-test-signing-key-which-is-long-enough-32+bytes");

    private static readonly SymmetricSecurityKey SigningKey = new(KeyBytes);

    private const string TestIssuer   = "https://test-issuer";
    private const string TestAudience = "ctxman-test-aud";

    // -----------------------------------------------------------------------
    // Token-Minthelfer

    /// <summary>
    /// Erstellt einen signierten JWT. Für den Abgelaufener-Fall: <paramref name="expiresAt"/> in der
    /// Vergangenheit setzen. Für den Falsch-signiert-Fall: <paramref name="useWrongKey"/> = true.
    /// </summary>
    private static string MintToken(
        string tenantId,
        string issuer        = TestIssuer,
        string audience      = TestAudience,
        DateTime? expiresAt  = null,
        bool useWrongKey     = false)
    {
        var now     = DateTime.UtcNow;
        var expires = expiresAt ?? now.AddHours(1);

        // Für „abgelaufen": notBefore weit genug in der Vergangenheit, damit der Token
        // auch formal ausgestellt wurde (einige Validator-Implementierungen lehnen
        // notBefore > now ab).
        var notBefore = expires < now ? now.AddHours(-2) : now;

        var key = useWrongKey
            ? new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("wrong-key-entirely-different-32bytes!!"))
            : SigningKey;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer             = issuer,
            Audience           = audience,
            NotBefore          = notBefore,
            Expires            = expires,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Subject            = new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenantId),
            }),
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.CreateEncodedJwt(descriptor);
    }

    // -----------------------------------------------------------------------
    // Antwort-Hilfe

    private static async Task<string?> ResolvedTenantAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("tenant_id").GetString();
    }

    // -----------------------------------------------------------------------
    // Hilfsmethode: baut eine WebApplicationFactory im jwt-Modus MIT injiziertem
    // Test-Schlüssel (kein OIDC-Discovery, kein Netz).

    private static HttpClient BuildJwtClient(CtxmanWebAppFactory factory)
    {
        // WithProbe() registriert die WhoAmI-Probe. WithWebHostBuilder erzeugt einen
        // abgeleiteten Host, in dem wir per PostConfigure die JwtBearerOptions überschreiben,
        // damit keine OIDC-Discovery stattfindet.
        return factory
            .WithWebHostBuilder(b =>
                b.ConfigureTestServices(s =>
                    s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                    {
                        o.RequireHttpsMetadata = false;
                        o.Authority            = null;
                        o.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer           = true,
                            ValidIssuer              = TestIssuer,
                            ValidateAudience         = true,
                            ValidAudience            = TestAudience,
                            ValidateIssuerSigningKey  = true,
                            IssuerSigningKey          = SigningKey,
                            ValidateLifetime          = true,
                            ClockSkew                 = TimeSpan.Zero,
                        };
                    })))
            .CreateClient();
    }

    // -----------------------------------------------------------------------
    // Tests

    /// <summary>
    /// Spec §4.1: valider Token (korrekte Issuer/Audience/Signatur, zukünftige Expiry,
    /// Claim tenant_id=acme) → 200, aufgelöster Tenant ist "acme".
    /// </summary>
    [Fact]
    public async Task ValidToken_Returns200AndResolvedTenant()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "jwt")
            .WithSetting("auth:jwt:tenant_claim", "tenant_id"); // Default explizit üben

        var client = BuildJwtClient(factory);
        var token  = MintToken(tenantId: "acme");

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal("acme", await ResolvedTenantAsync(response));
    }

    /// <summary>
    /// Spec §4.1: abgelaufener Token (Expiry in der Vergangenheit) → 401.
    /// </summary>
    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "jwt");

        var client = BuildJwtClient(factory);
        // Expiry und NotBefore so setzen, dass der Token zwar ausgestellt, aber abgelaufen ist.
        var token = MintToken(tenantId: "acme", expiresAt: DateTime.UtcNow.AddHours(-1));

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Spec §4.1: Token mit falscher Audience → 401.
    /// </summary>
    [Fact]
    public async Task WrongAudience_Returns401()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "jwt");

        var client = BuildJwtClient(factory);
        var token  = MintToken(tenantId: "acme", audience: "completely-wrong-audience");

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Spec §4.1: kein Authorization-Header → 401 (nicht authentifizierter Zugriff).
    /// </summary>
    [Fact]
    public async Task MissingToken_Returns401()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "jwt");

        var client = BuildJwtClient(factory);

        // Kein Authorization-Header.
        var response = await client.GetAsync("/test/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Spec §4.1: Token mit falschem Signing-Key → 401 (Signatur ungültig).
    /// </summary>
    [Fact]
    public async Task WrongSigningKey_Returns401()
    {
        using var factory = new CtxmanWebAppFactory()
            .WithSetting("auth:mode", "jwt");

        var client = BuildJwtClient(factory);
        var token  = MintToken(tenantId: "acme", useWrongKey: true);

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
