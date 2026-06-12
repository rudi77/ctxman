using Ctxman.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ctxman.Api.Storage;

/// <summary>
/// Kopiert Blobs aus dem Hot-Store in ein Cold-Storage-Verzeichnis (Spec §7.1).
/// Layout: <c>{coldRoot}/{tenantId}/{key}</c> — spiegelt <see cref="FileSystemBlobStore"/>.
/// Content-addressed + dedup: existierende Dateien werden übersprungen (immutable).
/// </summary>
public sealed class ColdStorageExporter : IColdStorageExporter
{
    private readonly IBlobStore _blobStore;
    private readonly string _coldRoot;
    private readonly ILogger<ColdStorageExporter> _logger;

    public ColdStorageExporter(
        IBlobStore blobStore,
        IOptions<ColdStorageOptions> options,
        ILogger<ColdStorageExporter> logger)
    {
        _blobStore = blobStore;
        _coldRoot = options.Value.Root;
        _logger = logger;
    }

    /// <summary>
    /// Exportiert alle angegebenen Blob-Keys vom Hot-Store in den Cold Storage (Spec §7.1).
    /// Fehler pro Blob werden geloggt und übersprungen — ein einzelner Fehler bricht nicht den Rest ab.
    /// </summary>
    public async Task ExportSessionBlobsAsync(
        string tenantId,
        Ulid sessionId,
        IReadOnlyCollection<string> blobKeys,
        CancellationToken ct)
    {
        // Spec §7.1: Tenant-präfixiertes Zielverzeichnis im Cold Root.
        var tenantColdDir = Path.Combine(_coldRoot, tenantId);
        Directory.CreateDirectory(tenantColdDir);

        foreach (var key in blobKeys)
        {
            try
            {
                var targetPath = Path.Combine(tenantColdDir, key);

                // Spec §7.1: Dedup — immutable content-addressed, bereits vorhandene Dateien überspringen.
                if (File.Exists(targetPath))
                {
                    continue;
                }

                // Temp-Datei im selben Verzeichnis für atomares Move.
                var tempPath = Path.Combine(tenantColdDir, Path.GetRandomFileName());
                try
                {
                    await using var src = await _blobStore.Get(tenantId, key, ct);
                    await using (var dest = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await src.CopyToAsync(dest, ct);
                    }

                    // Nochmals prüfen (race): wenn inzwischen angelegt, Temp verwerfen.
                    if (File.Exists(targetPath))
                    {
                        File.Delete(tempPath);
                    }
                    else
                    {
                        File.Move(tempPath, targetPath);
                    }
                }
                catch
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                    throw;
                }
            }
            catch (Exception ex)
            {
                // Spec §7.1: Best-effort — ein Blob-Fehler bricht nicht den gesamten Export ab.
                _logger.LogWarning(ex,
                    "Cold-storage export failed for blob {Key} (session {SessionId}, tenant {TenantId}).",
                    key, sessionId, tenantId);
            }
        }
    }
}
