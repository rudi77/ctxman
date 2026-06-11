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
}
