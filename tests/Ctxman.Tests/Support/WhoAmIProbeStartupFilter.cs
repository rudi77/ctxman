using Ctxman.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ctxman.Tests.Support;

/// <summary>
/// Hängt die test-only Probe <c>GET /test/whoami</c> in die Pipeline ein — NACH der in
/// <c>Program.cs</c> registrierten <c>TenantResolutionMiddleware</c> (§4.1), sodass die Probe
/// den bereits aufgelösten <see cref="ITenantContext.TenantId"/> beobachtet. Existiert nur im
/// Test-Host; die Produktion kennt diesen Endpoint nicht.
/// </summary>
internal sealed class WhoAmIProbeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Zuerst die reguläre App-Pipeline (inkl. Tenant-Resolution-Middleware) aufbauen …
            next(app);

            // … dann die Probe-Route ergänzen. Routing ist durch das minimal-hosting bereits aktiv.
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/test/whoami", (ITenantContext tenant) =>
                    Results.Ok(new WhoAmIResponse(tenant.TenantId)));
            });
        };
    }

    private sealed record WhoAmIResponse(string TenantId);
}
