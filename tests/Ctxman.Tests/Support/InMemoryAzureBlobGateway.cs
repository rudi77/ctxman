using System.Runtime.CompilerServices;
using Ctxman.Api.Storage;

namespace Ctxman.Tests.Support;

/// <summary>
/// In-Memory-Fake für <see cref="IAzureBlobGateway"/> — ermöglicht Unit-Tests von
/// <see cref="Ctxman.Api.Storage.AzureBlobStore"/> ohne Azure/Azurite-Verbindung.
/// </summary>
public sealed class InMemoryAzureBlobGateway : IAzureBlobGateway
{
    private readonly Dictionary<string, (byte[] Content, string ContentType, DateTimeOffset LastModified)> _blobs
        = new(StringComparer.Ordinal);

    // Deterministischer Zeitstempel: UnixEpoch + inkrementeller Counter statt UtcNow.
    private long _uploadCounter;

    /// <summary>Anzahl der tatsächlich ausgeführten Upload-Operationen (für Dedup-Assertions).</summary>
    public int UploadCount { get; private set; }

    public ValueTask<bool> ExistsAsync(string blobName, CancellationToken ct)
    {
        return ValueTask.FromResult(_blobs.ContainsKey(blobName));
    }

    public ValueTask UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        var ms = new MemoryStream();
        content.CopyTo(ms);
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref _uploadCounter));
        _blobs[blobName] = (ms.ToArray(), contentType, timestamp);
        UploadCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Stream> DownloadAsync(string blobName, CancellationToken ct)
    {
        if (!_blobs.TryGetValue(blobName, out var entry))
        {
            throw new KeyNotFoundException($"Blob '{blobName}' not found.");
        }

        Stream stream = new MemoryStream(entry.Content, writable: false);
        return ValueTask.FromResult(stream);
    }

    public ValueTask DeleteIfExistsAsync(string blobName, CancellationToken ct)
    {
        _blobs.Remove(blobName);
        return ValueTask.CompletedTask;
    }

#pragma warning disable CS1998 // Dictionary-Enumeration ist synchron; async-Signatur für IAsyncEnumerable.
    public async IAsyncEnumerable<AzureBlobItem> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (name, entry) in _blobs)
        {
            ct.ThrowIfCancellationRequested();

            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return new AzureBlobItem(name, entry.Content.LongLength, entry.LastModified);
            }
        }
    }
#pragma warning restore CS1998
}
