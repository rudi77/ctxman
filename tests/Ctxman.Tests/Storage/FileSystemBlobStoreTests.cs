using System.Text;
using Ctxman.Api.Storage;
using Microsoft.Extensions.Options;

namespace Ctxman.Tests.Storage;

/// <summary>
/// Content-Addressing & Dedup des Filesystem-Adapters (Spec §7.1): gleicher Inhalt ⇒ gleicher Key,
/// kein erneutes Schreiben; Exists nach Put true.
/// </summary>
public sealed class FileSystemBlobStoreTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemBlobStore _store;

    public FileSystemBlobStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ctxman-blobtests-" + Guid.NewGuid().ToString("N"));
        _store = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = _root }));
    }

    [Fact]
    public async Task Put_SameContent_YieldsSameKeyAndSize()
    {
        var bytes = Encoding.UTF8.GetBytes("hello content-addressed world");

        var first = await _store.Put("tenant-a", new MemoryStream(bytes), "text/plain", CancellationToken.None);
        var second = await _store.Put("tenant-a", new MemoryStream(bytes), "text/plain", CancellationToken.None);

        Assert.Equal(first.Key, second.Key);
        Assert.Equal("fs", first.Store);
        Assert.Equal(bytes.LongLength, first.SizeBytes);
        Assert.Equal("text/plain", first.ContentType);
    }

    [Fact]
    public async Task Put_IsContentAddressed_KeyIsLowercaseSha256()
    {
        var bytes = Encoding.UTF8.GetBytes("deterministic");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var blob = await _store.Put("tenant-a", new MemoryStream(bytes), "application/octet-stream", CancellationToken.None);

        Assert.Equal(expected, blob.Key);
    }

    [Fact]
    public async Task Put_SameContent_DoesNotRewriteFile()
    {
        var bytes = Encoding.UTF8.GetBytes("write once");

        var blob = await _store.Put("tenant-a", new MemoryStream(bytes), "text/plain", CancellationToken.None);
        var path = Path.Combine(_root, "tenant-a", blob.Key);
        var firstWrite = File.GetLastWriteTimeUtc(path);

        await _store.Put("tenant-a", new MemoryStream(bytes), "text/plain", CancellationToken.None);
        var secondWrite = File.GetLastWriteTimeUtc(path);

        Assert.Equal(firstWrite, secondWrite);
    }

    [Fact]
    public async Task Exists_AfterPut_IsTrue()
    {
        var bytes = Encoding.UTF8.GetBytes("present");

        var blob = await _store.Put("tenant-a", new MemoryStream(bytes), "text/plain", CancellationToken.None);

        Assert.True(await _store.Exists("tenant-a", blob.Key, CancellationToken.None));
        Assert.False(await _store.Exists("tenant-a", "0000000000000000000000000000000000000000000000000000000000000000", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
