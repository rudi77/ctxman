using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ctxman.Api.Gc;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Core.Storage;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Gc;

/// <summary>
/// AC15 (Spec §7.1 / §6): Ein verwaister Blob (Key ohne Segment-Zeile, jenseits der Grace-Period)
/// wird gelöscht UND als <c>blob_swept</c>-Outbox-Event mit <c>session_id = null</c> emittiert.
/// Solche Events sind tenant-gescopt, aber strukturell vom <c>after_seq</c>-Cursor einer Session
/// isoliert: <c>GET /v1/sessions/{sid}/events</c> filtert <c>SessionId == sessionId</c> und liefert
/// die null-Session-Zeile nicht.
/// </summary>
public sealed class BlobSweeperOrphanTests
{
    private const string Tenant = "t1";

    [Fact]
    public async Task OrphanSweep_EmitsBlobSweptEvent_WithNullSession_AndExcludedFromSessionStream()
    {
        await using var factory = new CtxmanWebAppFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);

        var sessionId = Ulid.NewUlid();
        var orphanKey = new string('a', 64); // 64-stelliger lowercase-hex (kein Segment referenziert ihn)
        const long orphanSize = 4096;

        // Session anlegen, damit GET /v1/sessions/{sid}/events sie findet (kein 404).
        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(new Session(
                id: sessionId,
                tenantId: Tenant,
                agentTemplateId: "tmpl-1",
                policy: PolicyConfig.Default(),
                contextVersion: 1,
                staticEpoch: 0,
                currentTurn: 0,
                status: SessionStatus.Active,
                createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            await db.SaveChangesAsync();
        }

        // now jenseits der Grace-Period (72h) gegenüber dem Blob-LastModified ⇒ Sweep löscht.
        var lastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = lastModified.AddHours(100);
        var clock = new FixedTimeProvider(now);

        var blobStore = new FakeBlobStore();
        blobStore.Add(Tenant, new BlobInfo(orphanKey, orphanSize, lastModified));

        var sweeper = new BlobSweeper(clock);

        BlobSweepResult result;
        using (var db = factory.CreateDbContext(Tenant))
        {
            result = await sweeper.SweepTenantAsync(Tenant, db, blobStore, CancellationToken.None);
        }

        // Blob wurde als orphan gelöscht.
        var deleted = Assert.Single(result.Deleted);
        Assert.Equal(orphanKey, deleted.Key);
        Assert.Equal("orphan", deleted.Reason);
        Assert.Null(deleted.SessionId);

        // Genau eine blob_swept-Outbox-Zeile mit session_id = null und korrektem Payload.
        using (var db = factory.CreateDbContext(Tenant))
        {
            var sweptEvents = await db.Events.Where(ev => ev.Type == "blob_swept").ToListAsync();
            var swept = Assert.Single(sweptEvents);
            Assert.Null(swept.SessionId);

            using var payload = JsonDocument.Parse(swept.Payload);
            var root = payload.RootElement;
            Assert.Equal(orphanKey, root.GetProperty("key").GetString());
            Assert.Equal(orphanSize, root.GetProperty("size_bytes").GetInt64());
            Assert.Equal("orphan", root.GetProperty("reason").GetString());
        }

        // Strukturelle Exklusion: der Session-Event-Stream liefert die null-Session-Zeile nicht.
        var response = await client.GetAsync($"/v1/sessions/{sessionId}/events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var events = doc.RootElement.GetProperty("events");
        Assert.Equal(0, events.GetArrayLength());
    }

    /// <summary>Test-only <see cref="TimeProvider"/> mit fixierter „now" (Grace-Boundary kontrolliert).</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Test-only In-Memory-<see cref="IBlobStore"/>: erlaubt das Setzen von <c>LastModified</c> der
    /// gelisteten Blobs (anders als der Filesystem-Adapter, der die Datei-Mtime nutzt), um die
    /// Grace-Boundary deterministisch zu kreuzen. Nur <c>List</c>/<c>Delete</c> werden vom Sweep benutzt.
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
                foreach (var info in blobs.Values)
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
