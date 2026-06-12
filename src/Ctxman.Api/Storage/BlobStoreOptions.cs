using Microsoft.Extensions.Configuration;

namespace Ctxman.Api.Storage;

/// <summary>
/// Konfiguration des Blob-Adapters (Spec §7).
/// Konfigurations-Sektion: <c>blobstore</c>.
/// <list type="bullet">
///   <item><c>blobstore:provider</c> — <c>fs</c> (Default) oder <c>azure-blob</c>.</item>
///   <item><c>blobstore:root</c> — Wurzelverzeichnis für den <c>fs</c>-Adapter.</item>
///   <item><c>blobstore:connection_string</c> — Azure Storage Connection String für <c>azure-blob</c>.</item>
///   <item><c>blobstore:container_name</c> — Azure Blob Container-Name für <c>azure-blob</c>.</item>
/// </list>
/// </summary>
public sealed class BlobStoreOptions
{
    /// <summary>Blob-Provider: <c>fs</c> (Dev-Default) oder <c>azure-blob</c> (Spec §7).</summary>
    public string Provider { get; set; } = "fs";

    /// <summary>Wurzelverzeichnis, unter dem Blobs tenant-präfixiert abgelegt werden (<c>fs</c>-Adapter).</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// Azure Storage Connection String (<c>azure-blob</c>-Adapter, Spec §7; N5: Credentials via Config-Kette).
    /// Konfigurations-Schlüssel: <c>blobstore:connection_string</c>.
    /// </summary>
    [ConfigurationKeyName("connection_string")]
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Name des Azure Blob Containers (<c>azure-blob</c>-Adapter, Spec §7).
    /// Konfigurations-Schlüssel: <c>blobstore:container_name</c>.
    /// </summary>
    [ConfigurationKeyName("container_name")]
    public string ContainerName { get; set; } = "ctxman-blobs";
}
