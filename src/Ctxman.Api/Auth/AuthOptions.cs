using Ctxman.Core.Domain;

namespace Ctxman.Api.Auth;

/// <summary>
/// Typisierte Bindung der Auth-Konfiguration (Spec §4.1):
/// <code>
/// auth:
///   mode: none | api_key | jwt      # Default: none
///   tenant_header: "X-Tenant-Id"    # nur Modus none, optional
///   default_tenant: "default"
/// </code>
/// Konfigurationsschlüssel sind snake_case (<c>auth:mode</c>, <c>auth:tenant_header</c>,
/// <c>auth:default_tenant</c>).
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Konfigurations-Sektion (<c>auth</c>).</summary>
    public const string SectionName = "auth";

    /// <summary>Auth-Modus der Tenant-Auflösung (Spec §4.1). Default: <see cref="AuthMode.None"/>.</summary>
    // Spec §4.1: Der .NET-Konfigurationsbinder bindet AuthMode über den Enum-Member-NAMEN
    // (case-insensitive), d. h. "none" -> AuthMode.None. Die snake_case-Wire-Werte "api_key"/"jwt"
    // haben noch kein eigenes Config-Mapping; diese Modi sind bis M5 (WP7) out of scope.
    public AuthMode Mode { get; set; } = AuthMode.None;

    /// <summary>
    /// Header, aus dem im Modus <see cref="AuthMode.None"/> der Tenant gelesen wird (Spec §4.1).
    /// Default: <c>X-Tenant-Id</c>.
    /// </summary>
    public string TenantHeader { get; set; } = "X-Tenant-Id";

    /// <summary>
    /// Fallback-Tenant, wenn im Modus <see cref="AuthMode.None"/> kein Header gesetzt ist (Spec §4.1).
    /// Default: <c>default</c>.
    /// </summary>
    public string DefaultTenant { get; set; } = "default";
}
