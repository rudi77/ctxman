namespace Ctxman.Api.Storage;

/// <summary>
/// Konfiguration des Filesystem-Blob-Adapters (Spec §7, Dev-Adapter).
/// </summary>
public sealed class BlobStoreOptions
{
    /// <summary>Wurzelverzeichnis, unter dem Blobs tenant-präfixiert abgelegt werden.</summary>
    public string Root { get; set; } = string.Empty;
}
