using System.Security.Cryptography;
using System.Text;

namespace Ctxman.Api.Gc;

/// <summary>
/// Serialisiert GC-Läufe pro Session (Spec §8: „keine zwei parallelen Collections auf derselben
/// Session"). Abstraktion über den konkreten Lock-Mechanismus, damit derselbe Worker-Code
/// prozesslokal (Dev/Test, SQLite) wie verteilt (Produktion, Postgres
/// <c>pg_advisory_lock(session_id)</c>) funktioniert.
///
/// <see cref="AcquireAsync"/> blockiert bis der Lock frei ist und gibt ein Handle zurück; dessen
/// Dispose gibt den Lock frei (<c>await using</c>). Der Lock muss den gesamten Job überspannen
/// (inkl. der DB-Transaktion), daher ein <see cref="IAsyncDisposable"/> statt eines using-Blocks
/// um nur die Transaktion.
/// </summary>
public interface ISessionGcLock
{
    /// <summary>
    /// Erwirbt den Lock für <paramref name="sessionId"/> (wartend). Das zurückgegebene Handle gibt
    /// den Lock bei Dispose frei.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string tenantId, Ulid sessionId, CancellationToken ct);
}

/// <summary>
/// Stabiler 64-Bit-Schlüssel aus (tenant_id, session_id) — für <c>pg_advisory_lock(bigint)</c> und
/// als Dictionary-Key der prozesslokalen Variante. Deterministisch über SHA-256 (erste 8 Bytes),
/// damit alle Replikas für dieselbe Session denselben Advisory-Lock-Schlüssel berechnen.
/// </summary>
public static class SessionLockKey
{
    public static long Compute(string tenantId, Ulid sessionId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{tenantId}:{sessionId}");
        var hash = SHA256.HashData(bytes);
        return BitConverter.ToInt64(hash, 0);
    }
}
