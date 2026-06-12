using System.Collections.Concurrent;

namespace Ctxman.Api.Gc;

/// <summary>
/// Singleton-Dienst, der pro Session einen <see cref="SemaphoreSlim"/> verwaltet und damit
/// sicherstellt, dass niemals zwei parallele GC-Collections (Minor ODER Major) auf derselben
/// Session laufen (Spec §8: "no two parallel collections on the same session").
///
/// Entspricht dem SQLite-Test-Äquivalent zu <c>pg_advisory_lock(session_id)</c> — der echte
/// Postgres-Advisory-Lock folgt in WP7.
/// </summary>
// Spec §8: pg_advisory_lock(session_id) — Minor und Major teilen sich DENSELBEN Lock.
public sealed class SessionGcLocks
{
    private readonly ConcurrentDictionary<Ulid, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Gibt den <see cref="SemaphoreSlim"/> für die angegebene Session zurück (neu angelegt,
    /// falls noch nicht vorhanden). Callers rufen <c>WaitAsync</c> / <c>Release</c> selbst auf.
    /// </summary>
    public SemaphoreSlim GetLock(Ulid sessionId) =>
        _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
}
