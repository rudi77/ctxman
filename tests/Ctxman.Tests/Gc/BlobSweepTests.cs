using System.Runtime.CompilerServices;
using System.Text.Json;
using Ctxman.Api.Gc;
using Ctxman.Core.Domain;
using Ctxman.Core.Storage;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Gc;

/// <summary>
/// AC14 (Spec §7.1 / §6): Mark-and-Sweep über den Blob Store. Ein Blob ohne Live-Referenz und
/// jenseits der Grace-Period (72h) wird gelöscht und emittiert
/// <c>blob_swept { key, size_bytes, reason: "unreferenced" }</c>; ein noch referenzierter
/// (externalisiertes Segment) oder ein noch in der Grace-Period liegender Blob wird NICHT gelöscht.
/// Die Grace-Grenze wird über einen fixierten <see cref="TimeProvider"/> deterministisch gekreuzt.
/// </summary>
public sealed class BlobSweepTests
{
    private const string Tenant = "tenant-sweep";
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string Key(char c) => new(c, 64); // 64-stelliger lowercase-hex (gültiger Blob-Key)

    private static Segment SegmentForBlob(Ulid sessionId, BlobRef blobRef, SegmentState state) => new(
        id: Ulid.NewUlid(),
        sessionId: sessionId,
        tenantId: Tenant,
        region: Region.Working,
        kind: "tool_result",
        source: null,
        role: Role.Tool,
        content: null,
        blobRef: blobRef,
        refetchable: false,
        origin: null,
        summary: "summary",
        toolCallId: null,
        frameId: null,
        pinned: false,
        createdTurn: 0,
        lastReferencedTurn: 0,
        tokens: 10,
        seq: Wp4Fixtures.NextSeq(),
        state: state);

    // AC14: unreferenzierter Blob jenseits Grace ⇒ gelöscht + blob_swept { reason: "unreferenced" };
    // referenzierter (externalisiert) und in-Grace-Blob bleiben erhalten.
    [Fact]
    public async Task Sweep_DeletesUnreferencedPastGrace_KeepsReferencedAndInGrace()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));

        // unreferenced: Segment existiert, ist aber evicted (keine Live-Referenz mehr); alt genug.
        const long unreferencedSize = 4096;
        var unreferencedRef = new BlobRef("fs", Key('a'), unreferencedSize, "text/plain; charset=utf-8");
        var unreferencedSeg = SegmentForBlob(session.Id, unreferencedRef, SegmentState.Evicted);

        // referenced: Segment ist externalisiert ⇒ Live-Referenz ⇒ nie löschen (auch wenn alt).
        var referencedRef = new BlobRef("fs", Key('b'), 2048, "text/plain; charset=utf-8");
        var referencedSeg = SegmentForBlob(session.Id, referencedRef, SegmentState.Externalized);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.AddRange(unreferencedSeg, referencedSeg);
            await db.SaveChangesAsync();
        }

        // now = Base + 100h ⇒ Grace (72h) ist für die alten Blobs überschritten, nicht für den jungen.
        var now = Base.AddHours(100);
        var clock = new FakeTimeProvider(now);

        var blobStore = new FakeBlobStore();
        blobStore.Add(Tenant, new BlobInfo(unreferencedRef.Key, unreferencedSize, Base));        // alt, unreferenced
        blobStore.Add(Tenant, new BlobInfo(referencedRef.Key, 2048, Base));                       // alt, referenced
        var inGraceRef = new BlobRef("fs", Key('c'), 1024, "text/plain; charset=utf-8");
        blobStore.Add(Tenant, new BlobInfo(inGraceRef.Key, 1024, now.AddHours(-1)));               // jung, in Grace

        var sweeper = new BlobSweeper(clock);
        BlobSweepResult result;
        using (var db = factory.CreateDbContext(Tenant))
        {
            result = await sweeper.SweepTenantAsync(Tenant, db, blobStore, CancellationToken.None);
        }

        // Nur der unreferenzierte, alte Blob wurde gelöscht.
        var deleted = Assert.Single(result.Deleted);
        Assert.Equal(unreferencedRef.Key, deleted.Key);
        Assert.Equal("unreferenced", deleted.Reason);
        Assert.Equal(unreferencedSize, deleted.SizeBytes);

        // Referenzierter und junger Blob existieren noch.
        Assert.True(await blobStore.Exists(Tenant, referencedRef.Key, CancellationToken.None));
        Assert.True(await blobStore.Exists(Tenant, inGraceRef.Key, CancellationToken.None));
        Assert.False(await blobStore.Exists(Tenant, unreferencedRef.Key, CancellationToken.None));

        // Genau ein blob_swept-Event mit dem dokumentierten snake_case-Payload.
        using var verify = factory.CreateDbContext(Tenant);
        var sweptEvents = await verify.Events.AsNoTracking()
            .Where(ev => ev.Type == "blob_swept").ToListAsync();
        var swept = Assert.Single(sweptEvents);
        using var payload = JsonDocument.Parse(swept.Payload);
        var root = payload.RootElement;
        Assert.Equal(unreferencedRef.Key, root.GetProperty("key").GetString());
        Assert.Equal(unreferencedSize, root.GetProperty("size_bytes").GetInt64());
        Assert.Equal("unreferenced", root.GetProperty("reason").GetString());
    }

    /// <summary>
    /// Test-only In-Memory-<see cref="IBlobStore"/> mit setzbarem <c>LastModified</c> (anders als der
    /// Filesystem-Adapter mit Datei-Mtime), um die Grace-Boundary deterministisch zu kreuzen.
    /// </summary>
    private sealed class FakeBlobStore : IBlobStore
    {
        private readonly Dictionary<string, Dictionary<string, BlobInfo>> _byTenant = new();

        public void Add(string tenantId, BlobInfo info)
        {
            if (!_byTenant.TryGetValue(tenantId, out var blobs))
            {
                blobs = new Dictionary<string, BlobInfo>(StringComparer.Ordinal);
                _byTenant[tenantId] = blobs;
            }
            blobs[info.Key] = info;
        }

#pragma warning disable CS1998
        public async IAsyncEnumerable<BlobInfo> List(
            string tenantId, [EnumeratorCancellation] CancellationToken ct)
        {
            if (_byTenant.TryGetValue(tenantId, out var blobs))
            {
                foreach (var info in blobs.Values.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    yield return info;
                }
            }
        }
#pragma warning restore CS1998

        public ValueTask Delete(string tenantId, string key, CancellationToken ct)
        {
            if (_byTenant.TryGetValue(tenantId, out var blobs))
            {
                blobs.Remove(key);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<BlobRef> Put(string tenantId, Stream content, string contentType, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<Stream> Get(string tenantId, string key, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> Exists(string tenantId, string key, CancellationToken ct)
            => ValueTask.FromResult(_byTenant.TryGetValue(tenantId, out var b) && b.ContainsKey(key));
    }
}
