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
/// sonst <see cref="AuthOptions.DefaultTenant"/>. Modus <see cref="AuthMode.ApiKey"/>: liest den
/// Key aus <c>X-Api-Key</c> (Vorrang) oder <c>Authorization: Bearer &lt;key&gt;</c> und schlägt ihn
/// im konfigurierten <see cref="AuthOptions.ApiKeys"/>-Dictionary nach — fehlt der Key oder ist er
/// unbekannt, antwortet die Middleware direkt mit 401 (kein Weiterleiten an <c>_next</c>). Modus
/// <see cref="AuthMode.Jwt"/>: setzt voraus, dass <c>UseAuthentication</c> den Token bereits
/// validiert und <c>context.User</c> gesetzt hat — ist der User nicht authentifiziert oder fehlt
/// der Tenant-Claim (<see cref="AuthOptions.JwtOptions.TenantClaim"/>), antwortet die Middleware
/// direkt mit 401. Bei allen drei Modi gibt es kein stilles Zurückfallen auf einen anderen Modus.
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

    // Infrastruktur-Endpunkte ohne Tenant-Bezug: Liveness/Readiness (/healthz), Prometheus
    // (/metrics) und die API-Referenz (/openapi, /scalar). Spec §4.1: in api_key/jwt würde die
    // Tenant-Auflösung diese sonst mit 401 blockieren — k8s-Probes und Prometheus können aber
    // keinen Tenant-Key liefern. Diese Pfade lesen nie tenant-gescopte Daten und werden daher
    // vor der modusabhängigen Auflösung durchgelassen (in allen Modi identisch).
    private static readonly string[] InfraPaths = { "/healthz", "/metrics", "/openapi", "/scalar" };

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        // Spec §4.1: Infrastruktur-Endpunkte sind tenant-frei und auth-frei (Health/Metrics/Docs).
        if (IsInfraPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // Spec §4.1: Tenant-Auflösung ist modusabhängig.
        switch (_options.Mode)
        {
            case AuthMode.None:
            {
                // Spec §10: dieselbe scoped-Instanz, die der CtxmanDbContext für seinen Query Filter liest.
                ((TenantContext)tenantContext).TenantId = ResolveFromHeaderOrDefault(context);
                await _next(context);
                break;
            }

            case AuthMode.ApiKey:
            {
                // Spec §4.1 / §10: Key→Tenant-Mapping; Tenant kommt ausschließlich aus der Konfiguration.
                var key = ExtractApiKey(context);
                if (key is null || !_options.ApiKeys.TryGetValue(key, out var mappedTenant))
                {
                    // Fehlender oder unbekannter Key → 401, kein Weiterleiten an _next. Spec §4.1.
                    context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"ctxman\"";
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                ((TenantContext)tenantContext).TenantId = mappedTenant;
                await _next(context);
                break;
            }

            case AuthMode.Jwt:
            {
                // Spec §4.1: JwtBearer (UseAuthentication) läuft vor dieser Middleware und setzt
                // context.User wenn der Token valide ist. Fehlender/ungültiger Token → kein
                // IsAuthenticated → 401 short-circuit (kein Weiterleiten an _next). Spec §10.
                if (context.User?.Identity?.IsAuthenticated != true)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                // Spec §4.1: Tenant-Claim-Name ist konfigurierbar (Default "tenant_id").
                var tenant = context.User.FindFirst(_options.Jwt.TenantClaim)?.Value;
                if (string.IsNullOrEmpty(tenant))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                ((TenantContext)tenantContext).TenantId = tenant;
                await _next(context);
                break;
            }

            default:
                throw new NotSupportedException($"Unknown auth mode '{_options.Mode}'.");
        }
    }

    /// <summary>
    /// Extrahiert den API-Key aus dem Request. Spec §4.1: X-Api-Key hat Vorrang vor
    /// Authorization: Bearer (deterministisch, damit Tests vorhersehbar sind).
    /// </summary>
    private static string? ExtractApiKey(HttpContext context)
    {
        // Spec §4.1: X-Api-Key zuerst, dann Authorization: Bearer <key>.
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var xApiKey))
        {
            var v = xApiKey.ToString();
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }

        if (context.Request.Headers.TryGetValue("Authorization", out var auth))
        {
            var v = auth.ToString();
            if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return v["Bearer ".Length..].Trim();
        }

        return null;
    }

    private static bool IsInfraPath(PathString path)
    {
        foreach (var infra in InfraPaths)
        {
            if (path.StartsWithSegments(infra, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
