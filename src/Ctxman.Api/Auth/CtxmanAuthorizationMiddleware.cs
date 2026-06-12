using Ctxman.Core;
using Ctxman.Core.Auth;

namespace Ctxman.Api.Auth;

/// <summary>
/// Authorization-Stufe der Middleware-Kette (Spec §4.1 / §8):
/// TenantResolution → Authentication → Authorization.
///
/// Liest die <see cref="ResourceAction"/>-Metadaten vom gematchten Endpoint.
/// Fehlt die Metadaten (z. B. /healthz, /test/whoami), wird die Anfrage ohne
/// Authorization-Prüfung durchgelassen. Andernfalls entscheidet der registrierte
/// <see cref="ICtxmanAuthorizationHandler"/>; Deny ⇒ 403.
/// </summary>
public sealed class CtxmanAuthorizationMiddleware // Spec §4.1, Spec §8
{
    private readonly RequestDelegate _next;

    public CtxmanAuthorizationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ICtxmanAuthorizationHandler handler,
        ITenantContext tenant)
    {
        var endpoint = context.GetEndpoint();
        var action = endpoint?.Metadata.GetMetadata<ResourceAction>(); // Spec §4.1

        if (action is null)
        {
            // No ResourceAction declared ⇒ no authorization check needed (e.g. /healthz, probes).
            await _next(context);
            return;
        }

        // Spec §4.1: tenant is already resolved by TenantResolutionMiddleware (pipeline order).
        var decision = await handler.AuthorizeAsync(
            (TenantContext)tenant,
            context.User,
            action,
            context.RequestAborted); // Spec §8

        if (!decision.Allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // Spec §4.1
            if (!string.IsNullOrWhiteSpace(decision.Reason))
            {
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync(decision.Reason, context.RequestAborted);
            }

            return;
        }

        await _next(context);
    }
}
