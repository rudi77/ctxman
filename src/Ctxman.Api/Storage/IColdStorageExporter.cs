namespace Ctxman.Api.Storage;

/// <summary>
/// Exportiert die Live-Blob-Referenzen einer Session in den Cold Storage (Spec §7.1).
/// Wird aufgerufen, wenn <c>archived_session_blobs == "cold_storage"</c> konfiguriert ist.
/// </summary>
public interface IColdStorageExporter
{
    Task ExportSessionBlobsAsync(string tenantId, Ulid sessionId, IReadOnlyCollection<string> blobKeys, CancellationToken ct);
}
