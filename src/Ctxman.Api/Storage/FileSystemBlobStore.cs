using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Ctxman.Core.Domain;
using Ctxman.Core.Storage;
using Microsoft.Extensions.Options;

namespace Ctxman.Api.Storage;

/// <summary>
/// Content-adressierter Filesystem-Adapter (Spec §7, Dev). Key = sha256 (lowercase hex) des Inhalts;
/// physische Ablage tenant-präfixiert unter <c>{root}/{tenantId}/{key}</c> (Spec §2.6 — Tenant-Isolation
/// über den Pfad-Präfix). Der <see cref="BlobRef.Key"/> selbst bleibt der nackte sha256 für Dedup (§7.1).
/// </summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    private const string StoreName = "fs";

    private readonly string _root;

    public FileSystemBlobStore(IOptions<BlobStoreOptions> options)
    {
        _root = options.Value.Root;
    }

    public async ValueTask<BlobRef> Put(string tenantId, Stream content, string contentType, CancellationToken ct)
    {
        // Inhalt in eine Temp-Datei kopieren, dabei sha256 und Größe ermitteln (single pass, ohne alles im Speicher zu halten).
        var tenantDir = Path.Combine(_root, tenantId);
        Directory.CreateDirectory(tenantDir);

        var tempPath = Path.Combine(tenantDir, Path.GetRandomFileName());
        long sizeBytes;
        byte[] hash;

        try
        {
            using (var sha = SHA256.Create())
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var hashing = new CryptoStream(temp, sha, CryptoStreamMode.Write))
            {
                await content.CopyToAsync(hashing, ct);
                await hashing.FlushFinalBlockAsync(ct);
                sizeBytes = temp.Position;
                hash = sha.Hash!;
            }

            var key = Convert.ToHexString(hash).ToLowerInvariant();
            var targetPath = Path.Combine(tenantDir, key);

            // Dedup: gleicher Inhalt ⇒ gleicher Key. Vorhandenes Blob nicht erneut schreiben (§7.1, immutable).
            if (File.Exists(targetPath))
            {
                File.Delete(tempPath);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }

            return new BlobRef(StoreName, key, sizeBytes, contentType);
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

    public ValueTask<Stream> Get(string tenantId, string key, CancellationToken ct)
    {
        var path = Path.Combine(_root, tenantId, key);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ValueTask.FromResult(stream);
    }

    public ValueTask<bool> Exists(string tenantId, string key, CancellationToken ct)
    {
        var path = Path.Combine(_root, tenantId, key);
        return ValueTask.FromResult(File.Exists(path));
    }

#pragma warning disable CS1998 // Filesystem-Enumeration ist synchron; async-Signatur für IAsyncEnumerable.
    public async IAsyncEnumerable<BlobInfo> List(
        string tenantId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var tenantDir = Path.Combine(_root, tenantId);
        if (!Directory.Exists(tenantDir))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(tenantDir))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(path);
            // Temp-Dateien des Put-Pfads (Path.GetRandomFileName(), z. B. "abcd1234.efg") überspringen —
            // ein Blob-Key ist immer der 64-stellige lowercase-hex sha256 ohne Punkt.
            if (!IsBlobKey(name))
            {
                continue;
            }

            var info = new FileInfo(path);
            yield return new BlobInfo(name, info.Length, info.LastWriteTimeUtc);
        }
    }
#pragma warning restore CS1998

    public ValueTask Delete(string tenantId, string key, CancellationToken ct)
    {
        var path = Path.Combine(_root, tenantId, key);
        // Idempotent: kein Fehler, wenn die Datei (oder das Tenant-Verzeichnis) nicht existiert.
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return ValueTask.CompletedTask;
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
