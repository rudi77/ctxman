using System.Runtime.CompilerServices;

namespace Ctxman.Api.Storage;

/// <summary>
/// Test-Seam zwischen <see cref="AzureBlobStore"/> und dem Azure SDK (Spec §7).
/// Ermöglicht Unit-Tests ohne echte Azure/Azurite-Verbindung.
/// </summary>
public interface IAzureBlobGateway
{
    ValueTask<bool> ExistsAsync(string blobName, CancellationToken ct);
    ValueTask UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct);
    ValueTask<Stream> DownloadAsync(string blobName, CancellationToken ct);
    ValueTask DeleteIfExistsAsync(string blobName, CancellationToken ct);
    IAsyncEnumerable<AzureBlobItem> ListAsync(string prefix, CancellationToken ct);
}

/// <summary>Metadaten eines Azure Blob-Eintrags (Spec §7).</summary>
public sealed record AzureBlobItem(string Name, long SizeBytes, DateTimeOffset LastModified);
