using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Ctxman.Api.Storage;

/// <summary>
/// Dünner Azure SDK-Wrapper über <see cref="BlobContainerClient"/> (Spec §7).
/// Dieser Layer wird in Unit-Tests NICHT verwendet — stattdessen kommt <see cref="IAzureBlobGateway"/>
/// mit einem Fake. <see cref="AzureBlobStore"/> enthält die Business-Logik.
/// </summary>
public sealed class AzureBlobContainerGateway : IAzureBlobGateway
{
    private readonly BlobContainerClient _containerClient;
    private int _initialized; // 0 = nein, 1 = ja — lazy CreateIfNotExists, blockiert nicht den Start.

    public AzureBlobContainerGateway(BlobContainerClient containerClient)
    {
        _containerClient = containerClient;
    }

    public async ValueTask<bool> ExistsAsync(string blobName, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        var resp = await _containerClient.GetBlobClient(blobName).ExistsAsync(ct);
        return resp.Value;
    }

    public async ValueTask UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        var headers = new BlobHttpHeaders { ContentType = contentType };
        await _containerClient.GetBlobClient(blobName).UploadAsync(content, headers, cancellationToken: ct);
    }

    public async ValueTask<Stream> DownloadAsync(string blobName, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        var resp = await _containerClient.GetBlobClient(blobName).DownloadStreamingAsync(cancellationToken: ct);
        return resp.Value.Content;
    }

    public async ValueTask DeleteIfExistsAsync(string blobName, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        await _containerClient.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async IAsyncEnumerable<AzureBlobItem> ListAsync(string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        await foreach (var b in _containerClient.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
        {
            yield return new AzureBlobItem(
                b.Name,
                b.Properties.ContentLength ?? 0,
                b.Properties.LastModified ?? default);
        }
    }

    /// <summary>
    /// Lazy-Init des Containers — nicht beim App-Start, damit eine nicht erreichbare Azure-Instanz
    /// den Start nicht blockiert.
    /// </summary>
    private async ValueTask EnsureContainerAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            try
            {
                await _containerClient.CreateIfNotExistsAsync(cancellationToken: ct);
            }
            catch (Exception)
            {
                // Lazy-Init-Fehler nicht beim Start anzeigen — Fehler werden beim konkreten
                // Blob-Zugriff propagiert. Reset damit der nächste Aufruf erneut versucht.
                Interlocked.Exchange(ref _initialized, 0);
                throw;
            }
        }
    }
}
