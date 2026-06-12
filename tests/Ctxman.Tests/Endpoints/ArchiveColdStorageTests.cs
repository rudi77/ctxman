using System.Net;
using Ctxman.Api.Storage;
using Ctxman.Core.Domain;
using Ctxman.Tests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// Akzeptanztests für WP7 Subtask 8 (Spec §7.1 / AC10):
/// Auf <c>POST /v1/sessions/{sid}/archive</c> exportiert der Endpoint bei
/// <c>archived_session_blobs == "cold_storage"</c> die Live-Blob-Referenzen der Session
/// via <see cref="IColdStorageExporter"/>.  Bei "delete" darf der Exporter NICHT gerufen werden.
///
/// Diese Tests sind bewusst ROT (Compile-Fehler), bis der Developer:
/// 1. <c>Ctxman.Api.Storage.IColdStorageExporter</c> anlegt.
/// 2. Den Archive-Endpoint so erweitert, dass er <c>RetentionConfig.ArchivedSessionBlobs</c>
///    auswertet und bei "cold_storage" <c>IColdStorageExporter.ExportSessionBlobsAsync</c> ruft.
/// </summary>
public sealed class ArchiveColdStorageTests
{
    // 64-stelliger Lowercase-Hex-Blob-Key (gültiges Format laut BlobSweepTests).
    private static string BlobKey(char c) => new(c, 64);

    // ---------------------------------------------------------------------------
    // Hilfsmethode: Segment mit BlobRef + Externalized-Zustand anlegen.
    // Orientiert sich exakt am SegmentForBlob-Muster aus BlobSweepTests.cs.
    // ---------------------------------------------------------------------------
    private static Segment SegmentWithExternalizedBlob(
        Ulid sessionId,
        string tenantId,
        BlobRef blobRef) => new(
            id: Ulid.NewUlid(),
            sessionId: sessionId,
            tenantId: tenantId,
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
            state: SegmentState.Externalized);

    // ---------------------------------------------------------------------------
    // Test 1: cold_storage ⇒ Exporter wird genau einmal gerufen (Spec §7.1 / AC10)
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task Archive_ColdStorage_ExportsSessionBlobsExactlyOnce()
    {
        const string Tenant = "t-cold";
        var blobKey = BlobKey('a');

        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        // Session mit einem Externalized-Segment + BlobRef anlegen.
        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        var blobRef = new BlobRef("fs", blobKey, 4096, "text/plain");
        var segment = SegmentWithExternalizedBlob(session.Id, Tenant, blobRef);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        // DI-Overrides: cold_storage-RetentionConfig + RecordingColdStorageExporter.
        var recording = new RecordingColdStorageExporter();
        var coldRetention = new RetentionConfig(
            BlobGraceHours: 72,
            EvictedBlobRetentionDays: 0,
            ArchivedSessionBlobs: "cold_storage",
            SweepInterval: "24h");

        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IColdStorageExporter>();
            s.AddSingleton<IColdStorageExporter>(recording);
            s.RemoveAll<RetentionConfig>();
            s.AddSingleton(coldRetention);
        }));
        var client = host.CreateClient();

        // Archive-Request: Idempotency-Key + X-Tenant-Id erforderlich (Spec §4.4).
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{session.Id}/archive");
        request.Headers.Add("Idempotency-Key", "idem-cold-1");
        request.Headers.Add("X-Tenant-Id", Tenant);

        var response = await client.SendAsync(request);

        // Spec §4.3: 204 No Content bei erfolgreichem Archive.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Exporter wurde genau einmal gerufen (AC10).
        Assert.Equal(1, recording.InvocationCount);

        // Exporter erhielt den richtigen Tenant.
        Assert.Equal(Tenant, recording.CapturedTenantId);

        // Exporter erhielt die korrekte Session-ID.
        Assert.Equal(session.Id, recording.CapturedSessionId);

        // Exporter erhielt den Blob-Key des Externalized-Segments.
        Assert.NotNull(recording.CapturedKeys);
        Assert.Contains(blobKey, recording.CapturedKeys);
    }

    // ---------------------------------------------------------------------------
    // Test 2: delete ⇒ Exporter wird NICHT gerufen (Spec §7.1 / AC10)
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task Archive_Delete_DoesNotCallExporter()
    {
        const string Tenant = "t-delete";
        var blobKey = BlobKey('b');

        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        var blobRef = new BlobRef("fs", blobKey, 2048, "text/plain");
        var segment = SegmentWithExternalizedBlob(session.Id, Tenant, blobRef);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        // DI-Overrides: delete-RetentionConfig + RecordingColdStorageExporter.
        var recording = new RecordingColdStorageExporter();
        var deleteRetention = new RetentionConfig(
            BlobGraceHours: 72,
            EvictedBlobRetentionDays: 0,
            ArchivedSessionBlobs: "delete",
            SweepInterval: "24h");

        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IColdStorageExporter>();
            s.AddSingleton<IColdStorageExporter>(recording);
            s.RemoveAll<RetentionConfig>();
            s.AddSingleton(deleteRetention);
        }));
        var client = host.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{session.Id}/archive");
        request.Headers.Add("Idempotency-Key", "idem-delete-1");
        request.Headers.Add("X-Tenant-Id", Tenant);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Bei "delete" darf der Exporter NICHT gerufen werden (AC10: Sweep übernimmt Löschung).
        Assert.Equal(0, recording.InvocationCount);
    }

    // ---------------------------------------------------------------------------
    // Test 3: Idempotenter Replay exportiert NICHT ein zweites Mal (Spec §4.4)
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task Archive_ColdStorage_IdempotentReplay_DoesNotDoubleExport()
    {
        const string Tenant = "t-cold-idem";
        var blobKey = BlobKey('c');

        await using var factory = new CtxmanWebAppFactory();
        Wp4Fixtures.EnsureHost(factory);

        var session = Wp4Fixtures.NewSession(Tenant, Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1_000_000));
        var blobRef = new BlobRef("fs", blobKey, 1024, "text/plain");
        var segment = SegmentWithExternalizedBlob(session.Id, Tenant, blobRef);

        using (var db = factory.CreateDbContext(Tenant))
        {
            db.Sessions.Add(session);
            db.Segments.Add(segment);
            await db.SaveChangesAsync();
        }

        var recording = new RecordingColdStorageExporter();
        var coldRetention = new RetentionConfig(
            BlobGraceHours: 72,
            EvictedBlobRetentionDays: 0,
            ArchivedSessionBlobs: "cold_storage",
            SweepInterval: "24h");

        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IColdStorageExporter>();
            s.AddSingleton<IColdStorageExporter>(recording);
            s.RemoveAll<RetentionConfig>();
            s.AddSingleton(coldRetention);
        }));
        var client = host.CreateClient();

        const string idempotencyKey = "idem-cold-replay-1";

        // Erster Request — führt das Archive durch.
        var firstRequest = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{session.Id}/archive");
        firstRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        firstRequest.Headers.Add("X-Tenant-Id", Tenant);
        var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

        // Zweiter Request mit DEMSELBEN Idempotency-Key — Replay-Pfad, kein zweites Archive.
        var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{session.Id}/archive");
        replayRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        replayRequest.Headers.Add("X-Tenant-Id", Tenant);
        var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.NoContent, replayResponse.StatusCode);

        // Export darf insgesamt nur EINMAL stattgefunden haben (Spec §4.4: Replay berührt Storage nicht).
        Assert.Equal(1, recording.InvocationCount);
    }

    // ---------------------------------------------------------------------------
    // Recording-Exporter: fängt den einzigen Aufruf auf und stellt ihn für Assertions bereit.
    // Implementiert IColdStorageExporter — COMPILE-FEHLER bis der Developer die Schnittstelle anlegt.
    // ---------------------------------------------------------------------------
    private sealed class RecordingColdStorageExporter : IColdStorageExporter
    {
        public int InvocationCount { get; private set; }
        public string? CapturedTenantId { get; private set; }
        public Ulid CapturedSessionId { get; private set; }
        public List<string>? CapturedKeys { get; private set; }

        public Task ExportSessionBlobsAsync(
            string tenantId,
            Ulid sessionId,
            IReadOnlyCollection<string> blobKeys,
            CancellationToken ct)
        {
            InvocationCount++;
            CapturedTenantId = tenantId;
            CapturedSessionId = sessionId;
            CapturedKeys = [.. blobKeys];
            return Task.CompletedTask;
        }
    }
}
