namespace Ctxman.Core;

/// <summary>
/// Trägt den für den aktuellen Request aufgelösten Tenant (Spec §4.1 / §10). Die Auflösung
/// passiert in der API-Middleware <em>bevor</em> Handler laufen; <see cref="TenantId"/> kommt
/// ausschließlich aus dieser Auflösung, nie aus dem Request-Body. Der <c>CtxmanDbContext</c>
/// liest daraus seinen Global Query Filter (§10: jede Query tenant-gefiltert).
///
/// Reines Core-Interface — keine ASP.NET-Abhängigkeit.
/// </summary>
public interface ITenantContext
{
    /// <summary>Der aufgelöste Tenant für den aktuellen Scope (Spec §4.1 / §10).</summary>
    string TenantId { get; }
}
