using Ctxman.Core;
using Ctxman.Core.Domain;
using Microsoft.Extensions.Options;

namespace Ctxman.Api.Auth;

/// <summary>
/// Löst pro Request den Tenant auf und schreibt ihn in den scoped <see cref="TenantContext"/> —
/// <em>bevor</em> die Handler laufen (Spec §4.1 / §8: Kette TenantResolution → Authentication →
/// Authorization). Der so gesetzte <see cref="TenantContext"/> ist dieselbe scoped-Instanz, die der
/// <c>CtxmanDbContext</c> für seinen Global Query Filter liest (§10). <c>tenant_id</c> kommt damit
/// ausschließlich aus dieser Auflösung, nie aus dem Request-Body (Spec §4 / §10).
///
/// Modus <see cref="AuthMode.None"/>: konfigurierter Header (<see cref="AuthOptions.TenantHeader"/>),
/// sonst <see cref="AuthOptions.DefaultTenant"/>. Die Modi <see cref="AuthMode.ApiKey"/> und
/// <see cref="AuthMode.Jwt"/> sind in diesem Workpaket nicht implementiert und werfen
/// <see cref="NotSupportedException"/> (kein stilles Zurückfallen auf <c>none</c>); ihre Aktivierung
/// ist eine reine Konfigurations-/Registrierungs-Erweiterung.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthOptions _options;

    public TenantResolutionMiddleware(RequestDelegate next, IOptions<AuthOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        // Spec §4.1: Tenant-Auflösung ist modusabhängig; der restliche Code ist in allen Modi identisch.
        var tenantId = _options.Mode switch
        {
            AuthMode.None => ResolveFromHeaderOrDefault(context),
            // M5: api_key/jwt noch nicht implementiert — bewusst kein Fallback auf none (Spec §4.1).
            AuthMode.ApiKey => throw new NotSupportedException(
                "Auth mode 'api_key' is not implemented in this build."),
            AuthMode.Jwt => throw new NotSupportedException(
                "Auth mode 'jwt' is not implemented in this build."),
            _ => throw new NotSupportedException($"Unknown auth mode '{_options.Mode}'."),
        };

        // Spec §10: dieselbe scoped-Instanz, die der CtxmanDbContext für seinen Query Filter liest.
        ((TenantContext)tenantContext).TenantId = tenantId;

        await _next(context);
    }

    private string ResolveFromHeaderOrDefault(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(_options.TenantHeader, out var values))
        {
            var header = values.ToString();
            if (!string.IsNullOrWhiteSpace(header))
            {
                return header;
            }
        }

        return _options.DefaultTenant;
    }
}
