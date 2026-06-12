using Ctxman.Core.Domain;

namespace Ctxman.Core.Storage;

/// <summary>
/// Adapter-Interface auf den Blob Store (Spec §7). Content-addressed (key = sha256) und immutable.
/// Tenant-Isolation erfolgt über die physische Ablage; der <see cref="BlobRef.Key"/> ist der nackte
/// sha256-Hash für Dedup (Spec §7.1). v1-Adapter: Azure Blob Storage und Filesystem (Dev).
/// </summary>
public interface IBlobStore
{
    /// <summary>
    /// Speichert <paramref name="content"/> content-adressiert und liefert einen <see cref="BlobRef"/>.
    /// Gleicher Inhalt ⇒ gleicher Key ⇒ kein erneutes Schreiben (Dedup).
    /// </summary>
    ValueTask<BlobRef> Put(string tenantId, Stream content, string contentType, CancellationToken ct);

    /// <summary>Öffnet den Inhalt zum Lesen für den gegebenen <paramref name="key"/> (sha256).</summary>
    ValueTask<Stream> Get(string tenantId, string key, CancellationToken ct);

    /// <summary>Prüft, ob für <paramref name="key"/> ein Inhalt existiert.</summary>
    ValueTask<bool> Exists(string tenantId, string key, CancellationToken ct);

    /// <summary>
    /// Enumeriert alle für den Tenant gespeicherten Blobs. Fehlende Tenant-Ablage ⇒ leere Sequenz.
    /// </summary>
    IAsyncEnumerable<BlobInfo> List(string tenantId, CancellationToken ct);

    /// <summary>
    /// Löscht den Blob mit <paramref name="key"/> idempotent (kein Fehler, wenn nicht vorhanden).
    /// </summary>
    ValueTask Delete(string tenantId, string key, CancellationToken ct);
}
