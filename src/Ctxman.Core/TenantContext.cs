namespace Ctxman.Core;

/// <summary>
/// Schlichte, mutable <see cref="ITenantContext"/>-Implementierung. Die API-Middleware setzt
/// <see cref="TenantId"/> pro Request (scoped); Tests können den Tenant direkt setzen, ohne
/// einen DI-Scope aufzubauen.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    /// <summary>Der aufgelöste Tenant (Spec §4.1 / §10); von der Middleware bzw. im Test gesetzt.</summary>
    public string TenantId { get; set; } = string.Empty;
}
