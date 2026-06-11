using System.Security.Claims;

namespace Ctxman.Core.Auth;

/// <summary>
/// Eine Aktion auf einer Ressource, über die autorisiert wird (Spec §4.1). Die Tenant-Zugehörigkeit
/// ist bereits durch die Tenant-Resolution geklärt; dieser Typ trägt nur das *was* (Ressourcentyp,
/// konkrete Ressource, Operation) für einen optionalen externen Policy Decision Point.
/// </summary>
/// <param name="ResourceType">Ressourcen-Art, z. B. <c>session</c>, <c>segment</c>, <c>frame</c>.</param>
/// <param name="ResourceId">Konkrete Ressourcen-Id, sofern bekannt (null bei Create/List).</param>
/// <param name="Operation">Operation, z. B. <c>read</c>, <c>write</c>, <c>delete</c>.</param>
public sealed record ResourceAction(
    string ResourceType,
    string? ResourceId,
    string Operation);

/// <summary>
/// Ergebnis einer Autorisierungs-Entscheidung (Spec §4.1). <see cref="Allowed"/> trägt die binäre
/// Entscheidung; <see cref="Reason"/> ist eine optionale, menschenlesbare Begründung (z. B. für 403).
/// </summary>
/// <param name="Allowed">true = erlaubt, false = abgelehnt.</param>
/// <param name="Reason">Optionale Begründung, insbesondere bei Ablehnung.</param>
public sealed record AuthzDecision(
    bool Allowed,
    string? Reason = null)
{
    /// <summary>Erlaubende Entscheidung ohne weitere Begründung.</summary>
    public static AuthzDecision Allow { get; } = new(true);

    /// <summary>Ablehnende Entscheidung mit optionaler Begründung.</summary>
    public static AuthzDecision Deny(string? reason = null) => new(false, reason);
}

/// <summary>
/// Autorisierungs-Erweiterungspunkt (Spec §4.1). Über "authentifizierter Aufrufer gehört zum Tenant"
/// hinausgehende Entscheidungen sind kein Bestandteil des Kerns: Ein externer Policy Decision Point
/// kann per DI eingehängt werden, ohne dass ctxman dessen Protokoll kennt. Die Default-Implementierung
/// erlaubt alles innerhalb des aufgelösten Tenants.
///
/// Reines Core-Interface — nur BCL (<see cref="ClaimsPrincipal"/> aus System.Security.Claims),
/// keine ASP.NET-Abhängigkeit. Signatur wörtlich aus Spec §4.1.
/// </summary>
public interface ICtxmanAuthorizationHandler
{
    /// <summary>
    /// Entscheidet, ob der (optional authentifizierte) Aufrufer die angegebene Aktion im aufgelösten
    /// Tenant ausführen darf (Spec §4.1).
    /// </summary>
    ValueTask<AuthzDecision> AuthorizeAsync(TenantContext tenant,
        ClaimsPrincipal? caller, ResourceAction action, CancellationToken ct);
}
