using System.Runtime.CompilerServices;
using System.Text.Json;
using Ctxman.Api.Gc;
using Ctxman.Core.Domain;
using Ctxman.Core.Storage;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Gc;

/// <summary>
/// Akzeptanztests für WP7 Subtask 7: Retention-Konfiguration wirkt im Blob-Sweep, und
/// Blobs von archivierten Sessions erhalten reason "session_deleted" (Spec §7.1).
///
/// Diese Tests sind bewusst ROT, bis der Developer:
/// 1. <c>new BlobSweeper(clock, retention)</c> (optionaler 2. Parameter) implementiert.
/// 2. Die <c>session_deleted</c>-Logik im Sweep einbaut.
/// 3. Die <c>evicted_blob_retention_days</c>-Fensterprüfung implementiert.
/// </summary>
public sealed class BlobSweepRetentionTests
{
    private const string Tenant = "tenant-sweep-retention";
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string Key(char c) => new(c, 64); // 64-stelliger lowercase-hex (gültiger Blob-Key)

    /// <summary>
    /// Hilfsmethode: erzeugt eine Session mit wählbarem Status direkt in der DB.
    /// </summary>
    private static Session NewSession(string tenantId, SessionStatus status)
    {
        return new Session(
            id: Ulid.NewUlid(),
            tenantId: tenantId,
            agentTemplateId: "tmpl-wp7-retention",
            policy: Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000),
            contextVersion: 1,
            staticEpoch: 0,
            currentTurn: 0,
            status: status,
            createdAt: Base,
            updatedAt: Base);
    }

    /// <summary>
    /// Hilfsmethode: erzeugt ein Segment mit BlobRef in gegebenem Zustand.
    /// </summary>
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

    // ---------------------------------------------------------------------------
    // Test 1 — session_deleted reason für Blobs von ARCHIVIERTEN Sessions
    // Spec §7.1: reason = "session_deleted" wenn Session.Status == Archived.
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task Sweep_BlobFromArchivedSession_ReasonIsSessionDeleted()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        // ARCHIVED session mit evicted segment (Blob hat keine Live-Referenz mehr).
        var session = NewSession(Tenant, SessionStatus.Archived);
        const long blobSize = 4096;
        var blobRef = new BlobRef("fs", Key('a'), blobSize, "text/plain; charset=utf-8");
        var segment = SegmentForBlob(session.Id, blobRef, SegmentState.Evicted);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        // now = Base + 100h ⇒ gut jenseits der 72h-Grace.
        var now = Base.AddHours(100);
        var clock = new FakeTimeProvider(now);

        var blobStore = new FakeBlobStore();
        blobStore.Add(Tenant, new BlobInfo(blobRef.Key, blobSize, Base)); // alt genug

        // BlobSweeper mit Default-Retention (2-arg-Ctor, retention = null).
        // DIESE ZEILE kompiliert erst nach der Developer-Implementierung (erwarteter Compile-Fehler).
        var sweeper = new BlobSweeper(clock, retention: null);
        BlobSweepResult result;
        using (var db = factory.CreateDbContext(Tenant))
        {
            result = await sweeper.SweepTenantAsync(Tenant, db, blobStore, CancellationToken.None);
        }

        // Blob wurde gelöscht.
        Assert.False(await blobStore.Exists(Tenant, blobRef.Key, CancellationToken.None));

        // Genau eine Löschung mit reason "session_deleted" (Spec §7.1).
        var deleted = Assert.Single(result.Deleted);
        Assert.Equal(blobRef.Key, deleted.Key);
        Assert.Equal(blobSize, deleted.SizeBytes);
        Assert.Equal("session_deleted", deleted.Reason); // ASSERTION FAILURE bis Developer-Impl

        // blob_swept-Event hat ebenfalls reason "session_deleted".
        using var verify = factory.CreateDbContext(Tenant);
        var sweptEvents = await verify.Events.AsNoTracking()
            .Where(ev => ev.Type == "blob_swept").ToListAsync();
        var swept = Assert.Single(sweptEvents);
        using var payload = JsonDocument.Parse(swept.Payload);
        var root = payload.RootElement;
        Assert.Equal(blobRef.Key, root.GetProperty("key").GetString());
        Assert.Equal(blobSize, root.GetProperty("size_bytes").GetInt64());
        Assert.Equal("session_deleted", root.GetProperty("reason").GetString()); // ASSERTION FAILURE
    }

    // ---------------------------------------------------------------------------
    // Test 2 — Blob einer AKTIVEN Session bleibt "unreferenced" (Regression-Guard)
    // Sichert, dass Test 1 nicht über-triggert: aktive Session darf nie "session_deleted" liefern.
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task Sweep_BlobFromActiveSession_ReasonRemainsUnreferenced()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var session = NewSession(Tenant, SessionStatus.Active);
        const long blobSize = 2048;
        var blobRef = new BlobRef("fs", Key('b'), blobSize, "text/plain; charset=utf-8");
        var segment = SegmentForBlob(session.Id, blobRef, SegmentState.Evicted);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        var now = Base.AddHours(100);
        var clock = new FakeTimeProvider(now);

        var blobStore = new FakeBlobStore();
        blobStore.Add(Tenant, new BlobInfo(blobRef.Key, blobSize, Base));

        var sweeper = new BlobSweeper(clock, retention: null);
        BlobSweepResult result;
        using (var db = factory.CreateDbContext(Tenant))
        {
            result = await sweeper.SweepTenantAsync(Tenant, db, blobStore, CancellationToken.None);
        }

        var deleted = Assert.Single(result.Deleted);
        Assert.Equal(blobRef.Key, deleted.Key);
        Assert.Equal("unreferenced", deleted.Reason); // darf NICHT "session_deleted" sein
    }

    // ---------------------------------------------------------------------------
    // Test 3 — evicted_blob_retention_days: Blob wird INNERHALB des Fensters NICHT gelöscht
    // Spec §7.1: active-session-Blob mit age < evicted_blob_retention_days bleibt erhalten,
    // auch wenn er jenseits der blob_grace liegt.
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task Sweep_WithEvictedRetentionWindow_RetainsBlobWithinWindow()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var session = NewSession(Tenant, SessionStatus.Active);
        const long blobSize = 1024;
        var blobRef = new BlobRef("fs", Key('c'), blobSize, "text/plain; charset=utf-8");
        var segment = SegmentForBlob(session.Id, blobRef, SegmentState.Evicted);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        // now = Base + 48h; blob age = 2 Tage = 48h.
        // Retention: BlobGraceHours=1 (1h grace), EvictedBlobRetentionDays=7 (7-Tage-Fenster).
        // age (2d = 48h) > BlobGrace (1h) ABER age (2d) < EvictedBlobRetentionDays (7d) ⇒ BEHALTEN.
        var now = Base.AddHours(48);
        var clock = new FakeTimeProvider(now);

        var retention = new RetentionConfig(
            BlobGraceHours: 1,
            EvictedBlobRetentionDays: 7,
            ArchivedSessionBlobs: "delete",
            SweepInterval: "24h");

        var blobStore = new FakeBlobStore();
        // Blob ist 2 Tage alt (now - Base = 48h), liegt innerhalb des 7-Tage-Fensters.
        blobStore.Add(Tenant, new BlobInfo(blobRef.Key, blobSize, Base));

        var sweeper = new BlobSweeper(clock, retention); // COMPILE-FEHLER bis Developer-Impl
        BlobSweepResult result;
        using (var db = factory.CreateDbContext(Tenant))
        {
            result = await sweeper.SweepTenantAsync(Tenant, db, blobStore, CancellationToken.None);
        }

        // Blob NICHT gelöscht — liegt noch im 7-Tage-Forensik-Fenster.
        Assert.Empty(result.Deleted); // ASSERTION FAILURE bis Developer-Impl
        Assert.True(await blobStore.Exists(Tenant, blobRef.Key, CancellationToken.None));
    }

    // ---------------------------------------------------------------------------
    // Test 4 — evicted_blob_retention_days: Blob wird NACH dem Fenster gelöscht
    // Spec §7.1: active-session-Blob mit age > evicted_blob_retention_days wird swept.
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task Sweep_WithEvictedRetentionWindow_SwepsBlobPastWindow()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var session = NewSession(Tenant, SessionStatus.Active);
        const long blobSize = 1024;
        var blobRef = new BlobRef("fs", Key('d'), blobSize, "text/plain; charset=utf-8");
        var segment = SegmentForBlob(session.Id, blobRef, SegmentState.Evicted);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        // now = Base + 192h (= 8 Tage); blob age = 8 Tage > 7-Tage-Fenster ⇒ löschen.
        var now = Base.AddHours(192); // 8 * 24 = 192h
        var clock = new FakeTimeProvider(now);

        var retention = new RetentionConfig(
            BlobGraceHours: 1,
            EvictedBlobRetentionDays: 7,
            ArchivedSessionBlobs: "delete",
            SweepInterval: "24h");

        var blobStore = new FakeBlobStore();
        blobStore.Add(Tenant, new BlobInfo(blobRef.Key, blobSize, Base)); // 8 Tage alt

        var sweeper = new BlobSweeper(clock, retention);
        BlobSweepResult result;
        using (var db = factory.CreateDbContext(Tenant))
        {
            result = await sweeper.SweepTenantAsync(Tenant, db, blobStore, CancellationToken.None);
        }

        // Blob wurde gelöscht — jenseits des 7-Tage-Fensters.
        var deleted = Assert.Single(result.Deleted); // ASSERTION FAILURE bis Developer-Impl
        Assert.Equal(blobRef.Key, deleted.Key);
        Assert.Equal("unreferenced", deleted.Reason);
        Assert.False(await blobStore.Exists(Tenant, blobRef.Key, CancellationToken.None));
    }

    // ---------------------------------------------------------------------------
    // Test 5 — Konfigurierbare Grace wird aus injiziertem RetentionConfig gelesen
    // Spec §7.1: BlobGraceHours aus der übergebenen Retention, NICHT die hardcodierten 72h.
    // Zwei Sub-Cases: (a) Blob innerhalb konfigurierbarer 10h-Grace → BEHALTEN;
    //                 (b) Blob jenseits 10h-Grace → GELÖSCHT.
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task Sweep_ConfigurableGrace_HonorsInjectedRetentionNotHardcoded72h()
    {
        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var session = NewSession(Tenant, SessionStatus.Active);
        const long blobSize = 512;

        // Blob E: 5h alt — INNERHALB der 10h-Grace ⇒ behalten.
        var blobRefE = new BlobRef("fs", Key('e'), blobSize, "text/plain; charset=utf-8");
        var segmentE = SegmentForBlob(session.Id, blobRefE, SegmentState.Evicted);

        // Blob F: 20h alt — JENSEITS der 10h-Grace ⇒ löschen.
        var blobRefF = new BlobRef("fs", Key('f'), blobSize, "text/plain; charset=utf-8");
        var segmentF = SegmentForBlob(session.Id, blobRefF, SegmentState.Evicted);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.AddRange(segmentE, segmentF);
            await db.SaveChangesAsync();
        }

        // now = Base + 20h; beide Blobs wären jenseits der hardcodierten 72h NICHT weg,
        // aber mit BlobGraceHours=10 (konfiguriert) ist Blob F (age=20h) jenseits der Grace.
        var now = Base.AddHours(20);
        var clock = new FakeTimeProvider(now);

        var retention = new RetentionConfig(
            BlobGraceHours: 10,
            EvictedBlobRetentionDays: 0, // kein extra-Fenster
            ArchivedSessionBlobs: "delete",
            SweepInterval: "24h");

        var blobStore = new FakeBlobStore();
        blobStore.Add(Tenant, new BlobInfo(blobRefE.Key, blobSize, Base.AddHours(15))); // age = 5h (jünger als 10h-Grace)
        blobStore.Add(Tenant, new BlobInfo(blobRefF.Key, blobSize, Base));              // age = 20h (älter als 10h-Grace)

        var sweeper = new BlobSweeper(clock, retention); // COMPILE-FEHLER bis Developer-Impl
        BlobSweepResult result;
        using (var db = factory.CreateDbContext(Tenant))
        {
            result = await sweeper.SweepTenantAsync(Tenant, db, blobStore, CancellationToken.None);
        }

        // Blob E (age=5h < 10h-Grace) wurde NICHT gelöscht.
        Assert.True(await blobStore.Exists(Tenant, blobRefE.Key, CancellationToken.None)); // ASSERTION FAILURE bis Impl

        // Blob F (age=20h > 10h-Grace) wurde gelöscht.
        Assert.False(await blobStore.Exists(Tenant, blobRefF.Key, CancellationToken.None)); // ASSERTION FAILURE bis Impl

        var deleted = Assert.Single(result.Deleted);
        Assert.Equal(blobRefF.Key, deleted.Key);
        Assert.Equal("unreferenced", deleted.Reason);
    }

    // ---------------------------------------------------------------------------
    // Test-only In-Memory-IBlobStore (analog zu BlobSweepTests.FakeBlobStore).
    // Kopie statt Referenz — jede Test-Klasse bleibt eigenständig (keine Cross-Abhängigkeiten).
    // ---------------------------------------------------------------------------
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
