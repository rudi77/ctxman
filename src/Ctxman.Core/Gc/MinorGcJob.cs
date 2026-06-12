using Ctxman.Core.Domain;

namespace Ctxman.Core.Gc;

/// <summary>
/// Auftrag für einen GC-Lauf einer Session (Spec §3.2 / §3.3). Tenant-Scope kommt
/// ausschließlich aus dem Aufruf-Kontext (CLAUDE.md), nie aus einem Request-Body.
/// </summary>
public sealed record MinorGcJob(
    string TenantId,
    Ulid SessionId,
    Ulid JobId,
    GcLevel Level);
