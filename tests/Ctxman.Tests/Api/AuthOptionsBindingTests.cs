using Ctxman.Api.Auth;
using Ctxman.Core.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ctxman.Tests.Api;

/// <summary>
/// Bindung der Auth-Konfiguration (Spec §4.1): der snake_case-Wert <c>auth:mode = none</c> bindet
/// case-insensitiv auf <see cref="AuthMode.None"/> über den Standard-Konfigurationsbinder
/// (CLAUDE.md: Standard-.NET-Konfigurationskette).
/// </summary>
public sealed class AuthOptionsBindingTests
{
    [Fact] // Spec §4.1: auth:mode=none -> AuthMode.None.
    public void Bind_ModeNone_YieldsAuthModeNone()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["auth:mode"] = "none",
                ["auth:tenant_header"] = "X-Tenant-Id",
                ["auth:default_tenant"] = "default",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<AuthOptions>(config.GetSection(AuthOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AuthOptions>>().Value;

        Assert.Equal(AuthMode.None, options.Mode);
        Assert.Equal("X-Tenant-Id", options.TenantHeader);
        Assert.Equal("default", options.DefaultTenant);
    }

    [Fact] // Spec §4.1: api_key-Modus — ApiKeys-Dictionary und JWT-Optionen binden korrekt.
    // Hinweis: Der Standard-.NET-Binder liest Enum-Werte per Enum-Member-NAME (case-insensitive),
    // nicht per snake_case-Wire-String ("api_key"). snake_case-Parsing erfolgt über EnumWire
    // (nur im Middleware-Pfad). Hier wird "ApiKey" (Enum-Name) verwendet, damit der Binder greift.
    public void Bind_ModeApiKey_ApiKeysAndJwtBound()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["auth:mode"] = "ApiKey",
                ["auth:api_keys:key-alpha"] = "tenant-a",
                ["auth:api_keys:key-beta"] = "tenant-b",
                ["auth:jwt:authority"] = "https://auth.example.com",
                ["auth:jwt:issuer"] = "https://issuer.example.com",
                ["auth:jwt:audience"] = "ctxman-api",
                ["auth:jwt:tenant_claim"] = "x-tenant",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<AuthOptions>(config.GetSection(AuthOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AuthOptions>>().Value;

        // Modus
        Assert.Equal(AuthMode.ApiKey, options.Mode);

        // ApiKeys-Dictionary (Spec §4.1: key → tenant_id)
        Assert.Equal(2, options.ApiKeys.Count);
        Assert.Equal("tenant-a", options.ApiKeys["key-alpha"]);
        Assert.Equal("tenant-b", options.ApiKeys["key-beta"]);

        // JWT-Optionen (Spec §4.1)
        Assert.Equal("https://auth.example.com", options.Jwt.Authority);
        Assert.Equal("https://issuer.example.com", options.Jwt.Issuer);
        Assert.Equal("ctxman-api", options.Jwt.Audience);
        Assert.Equal("x-tenant", options.Jwt.TenantClaim);
    }

    [Fact] // Spec §4.1: TenantClaim hat Default "tenant_id", wenn nicht in Konfiguration gesetzt.
    public void Bind_JwtOptions_DefaultTenantClaim()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["auth:mode"] = "Jwt",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<AuthOptions>(config.GetSection(AuthOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AuthOptions>>().Value;

        Assert.Equal("tenant_id", options.Jwt.TenantClaim);
        Assert.Null(options.Jwt.Authority);
        Assert.Null(options.Jwt.Issuer);
        Assert.Null(options.Jwt.Audience);
        Assert.Empty(options.ApiKeys);
    }
}
