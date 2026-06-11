using System.Security.Claims;
using Ctxman.Core;
using Ctxman.Core.Auth;

namespace Ctxman.Api.Auth;

/// <summary>
/// Default-<see cref="ICtxmanAuthorizationHandler"/> (Spec §4.1): erlaubt jede Aktion innerhalb des
/// bereits aufgelösten Tenants. Die Tenant-Isolation selbst stellen Tenant-Resolution (§4.1) und
/// Query-Filter (§10) sicher; dieser Handler trifft darüber hinaus keine weiteren Entscheidungen.
/// Per DI durch eine eigene Implementierung (z. B. externer Policy Decision Point) ersetzbar.
/// </summary>
public sealed class AllowAllWithinTenantAuthorizationHandler : ICtxmanAuthorizationHandler
{
    public ValueTask<AuthzDecision> AuthorizeAsync(TenantContext tenant,
        ClaimsPrincipal? caller, ResourceAction action, CancellationToken ct)
        => ValueTask.FromResult(AuthzDecision.Allow);
}
