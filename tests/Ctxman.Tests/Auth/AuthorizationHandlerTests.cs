using System.Security.Claims;
using Ctxman.Api.Auth;
using Ctxman.Core;
using Ctxman.Core.Auth;
using Ctxman.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ctxman.Tests.Auth;

/// <summary>Test-Fake: lehnt jede Aktion ab (Spec §4.1: externer Policy Decision Point per DI).</summary>
internal sealed class DenyAllAuthorizationHandler : ICtxmanAuthorizationHandler
{
    public ValueTask<AuthzDecision> AuthorizeAsync(TenantContext tenant,
        ClaimsPrincipal? caller, ResourceAction action, CancellationToken ct)
        => ValueTask.FromResult(AuthzDecision.Deny("test-deny"));
}

/// <summary>
/// <see cref="ICtxmanAuthorizationHandler"/> (Spec §4.1 / Akzeptanzkriterium 12): die
/// Default-Implementierung erlaubt alles innerhalb des Tenants und ist per DI ersetzbar.
/// </summary>
public sealed class AuthorizationHandlerTests
{
    private static readonly ResourceAction Action = new("session", null, "write");

    [Fact] // Spec §4.1: Default-Handler erlaubt jede Aktion innerhalb des Tenants.
    public async Task DefaultHandler_Allows()
    {
        var handler = new AllowAllWithinTenantAuthorizationHandler();

        var decision = await handler.AuthorizeAsync(
            new TenantContext { TenantId = "t1" },
            new ClaimsPrincipal(),
            Action,
            CancellationToken.None);

        Assert.True(decision.Allowed);
    }

    [Fact] // Spec §4.1: im Host ist der Default-Handler registriert.
    public void Host_ResolvesDefaultHandler()
    {
        using var factory = new CtxmanWebAppFactory();

        var handler = factory.Services.GetRequiredService<ICtxmanAuthorizationHandler>();

        Assert.IsType<AllowAllWithinTenantAuthorizationHandler>(handler);
    }

    [Fact] // Spec §4.1: eine per DI registrierte Implementierung ersetzt den Default (Override gewinnt).
    public async Task DiOverride_ReplacesDefaultHandler()
    {
        using var baseFactory = new CtxmanWebAppFactory();
        using var overridden = baseFactory.WithAuthorizationHandler<DenyAllAuthorizationHandler>();

        var handler = overridden.Services.GetRequiredService<ICtxmanAuthorizationHandler>();
        Assert.IsType<DenyAllAuthorizationHandler>(handler);

        var decision = await handler.AuthorizeAsync(
            new TenantContext { TenantId = "t1" },
            null,
            Action,
            CancellationToken.None);

        Assert.False(decision.Allowed);
    }
}
