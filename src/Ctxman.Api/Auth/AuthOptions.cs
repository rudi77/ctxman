using Ctxman.Core.Domain;
using Microsoft.Extensions.Configuration;

namespace Ctxman.Api.Auth;

/// <summary>
/// Typisierte Bindung der Auth-Konfiguration (Spec §4.1):
/// <code>
/// auth:
///   mode: none | api_key | jwt      # Default: none
///   tenant_header: "X-Tenant-Id"    # nur Modus none, optional
///   default_tenant: "default"
///   api_keys:
///     &lt;key&gt;: &lt;tenant_id&gt;            # Modus api_key: Key → Tenant-Mapping
///   jwt:
///     authority: "https://..."      # OIDC-Authority (optional, wenn issuer direkt gesetzt)
///     issuer: "https://..."         # Token-Issuer (optional)
///     audience: "ctxman"            # Token-Audience (optional)
///     tenant_claim: "tenant_id"     # Claim-Name für den Tenant (Default: tenant_id)
/// </code>
/// Konfigurationsschlüssel sind snake_case (<c>auth:mode</c>, <c>auth:tenant_header</c>,
/// <c>auth:default_tenant</c>, <c>auth:api_keys:*</c>, <c>auth:jwt:*</c>).
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Konfigurations-Sektion (<c>auth</c>).</summary>
    public const string SectionName = "auth";

    /// <summary>Auth-Modus der Tenant-Auflösung (Spec §4.1). Default: <see cref="AuthMode.None"/>.</summary>
    // Spec §4.1: Der .NET-Konfigurationsbinder bindet AuthMode über den Enum-Member-NAMEN
    // (case-insensitive), d. h. "none" -> AuthMode.None.
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

    /// <summary>
    /// API-Key → Tenant-ID-Mapping für Modus <see cref="AuthMode.ApiKey"/> (Spec §4.1).
    /// Konfiguration: <c>auth:api_keys:&lt;key&gt; = &lt;tenant_id&gt;</c>.
    /// </summary>
    // Spec §4.1: [ConfigurationKeyName] sorgt dafür, dass der Standard-.NET-Konfigurationsbinder
    // den Abschnitt "auth:api_keys:*" (snake_case) auf diese PascalCase-Eigenschaft mappt.
    [ConfigurationKeyName("api_keys")]
    public Dictionary<string, string> ApiKeys { get; set; } = new();

    /// <summary>
    /// JWT-spezifische Konfiguration für Modus <see cref="AuthMode.Jwt"/> (Spec §4.1).
    /// Konfiguration: <c>auth:jwt:*</c>.
    /// </summary>
    // Spec §4.1: [ConfigurationKeyName] mappt "auth:jwt:*" (snake_case) auf diese Eigenschaft.
    [ConfigurationKeyName("jwt")]
    public JwtOptions Jwt { get; set; } = new();
}

/// <summary>
/// JWT-Konfiguration für den Modus <see cref="AuthMode.Jwt"/> (Spec §4.1).
/// </summary>
public sealed class JwtOptions
{
    /// <summary>
    /// OIDC-Authority-URL (Spec §4.1). Kann <c>null</c> sein, wenn <see cref="Issuer"/> direkt gesetzt wird.
    /// Konfiguration: <c>auth:jwt:authority</c>.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Token-Issuer (Spec §4.1). Optional, wenn <see cref="Authority"/> gesetzt ist.
    /// Konfiguration: <c>auth:jwt:issuer</c>.
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Erwartete Token-Audience (Spec §4.1). Optional.
    /// Konfiguration: <c>auth:jwt:audience</c>.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Claim-Name, aus dem der Tenant gelesen wird (Spec §4.1). Default: <c>tenant_id</c>.
    /// Konfiguration: <c>auth:jwt:tenant_claim</c>.
    /// </summary>
    // Spec §4.1: Default-Wert "tenant_id" gemäß Spec. [ConfigurationKeyName] mappt snake_case-Schlüssel.
    [ConfigurationKeyName("tenant_claim")]
    public string TenantClaim { get; set; } = "tenant_id";
}
