using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Ctxman.Core.Domain;
using Ctxman.Core.Storage;

namespace Ctxman.Api.Storage;

/// <summary>
/// Content-adressierter Azure Blob Storage-Adapter (Spec §7, §7.1).
/// Key = sha256 (lowercase hex) des Inhalts; physische Ablage tenant-präfixiert als
/// <c>{tenantId}/{key}</c> im konfigurierten Container. Der <see cref="BlobRef.Key"/>
/// bleibt der nackte sha256 für Dedup (§7.1).
/// </summary>
public sealed class AzureBlobStore : IBlobStore
{
    private const string StoreName = "azure-blob";

    private readonly IAzureBlobGateway _gateway;

    public AzureBlobStore(IAzureBlobGateway gateway)
    {
        _gateway = gateway;
    }

    public async ValueTask<BlobRef> Put(string tenantId, Stream content, string contentType, CancellationToken ct)
    {
        // Single-pass: Inhalt puffern und dabei sha256 + Größe ermitteln (Spec §7.1, content-addressed).
        var buffer = new MemoryStream();
        long sizeBytes;
        byte[] hash;

        using (var sha = SHA256.Create())
        await using (var hashing = new CryptoStream(buffer, sha, CryptoStreamMode.Write, leaveOpen: true))
        {
            await content.CopyToAsync(hashing, ct);
            await hashing.FlushFinalBlockAsync(ct);
            sizeBytes = buffer.Position;
            hash = sha.Hash!;
        }

        // Spec §7.1: Key ist der lowercase-hex sha256 — Tenant-Isolation über den Pfad-Präfix.
        var key = Convert.ToHexString(hash).ToLowerInvariant();

        // Spec §7.1: Tenant-Präfix sichert Tenant-Isolation im gemeinsamen Container.
        var blobName = $"{tenantId}/{key}";

        // Dedup: gleicher Inhalt ⇒ gleicher Key — vorhandenes Blob nicht erneut hochladen (§7.1, immutable).
        if (!await _gateway.ExistsAsync(blobName, ct))
        {
            buffer.Position = 0;
            await _gateway.UploadAsync(blobName, buffer, contentType, ct);
        }

        return new BlobRef(StoreName, key, sizeBytes, contentType);
    }

    public async ValueTask<Stream> Get(string tenantId, string key, CancellationToken ct)
    {
        return await _gateway.DownloadAsync($"{tenantId}/{key}", ct);
    }

    public ValueTask<bool> Exists(string tenantId, string key, CancellationToken ct)
    {
        return _gateway.ExistsAsync($"{tenantId}/{key}", ct);
    }

    public async IAsyncEnumerable<BlobInfo> List(
        string tenantId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var prefix = $"{tenantId}/";

        await foreach (var item in _gateway.ListAsync(prefix, ct))
        {
            // Tenant-Präfix abstreifen, um den nackten Key zu erhalten.
            var key = item.Name.StartsWith(prefix, StringComparison.Ordinal)
                ? item.Name[prefix.Length..]
                : item.Name;

            // Nur valide Blob-Keys weitergeben — Spec §7.1: Key ist immer 64 lowercase-hex-Zeichen.
            if (!IsBlobKey(key))
            {
                continue;
            }

            yield return new BlobInfo(key, item.SizeBytes, item.LastModified);
        }
    }

    public ValueTask Delete(string tenantId, string key, CancellationToken ct)
    {
        // Idempotent: DeleteIfExists wirft keinen Fehler, wenn das Blob nicht existiert.
        return _gateway.DeleteIfExistsAsync($"{tenantId}/{key}", ct);
    }

    private static bool IsBlobKey(string name)
    {
        if (name.Length != 64)
        {
            return false;
        }

        foreach (var c in name)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
