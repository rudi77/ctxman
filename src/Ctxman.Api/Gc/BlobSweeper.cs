using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Gc;

/// <summary>
/// Mark-and-Sweep über den Blob Store (Spec §7.1). Source of Truth ist ausschließlich die
/// <c>segments</c>-Tabelle, daher adapter-agnostisch (Azure Blob und Filesystem identisch).
///
/// Diese Klasse kapselt die Kern-Sweep-Logik für GENAU EINEN Tenant, deterministisch über die
/// injizierte „now" (Spec §7.1: <c>now − LastModified &gt; blob_grace</c>). Der umgebende
/// <see cref="BlobSweepWorker"/> ruft sie pro Tenant auf einer Schleife; Tests rufen sie direkt
/// für AC14/AC15 ohne den Timer auf.
///
/// Tenant-Scope: Aufrufer MÜSSEN den <see cref="Ctxman.Core.TenantContext"/> des übergebenen
/// DI-Scopes bereits auf <paramref name="tenantId"/> gesetzt haben (analog
/// <see cref="MinorGcWorker"/>), bevor sie hier den <see cref="CtxmanDbContext"/> übergeben.
/// </summary>
public sealed class BlobSweeper
{
    private readonly TimeProvider _clock;

    // Spec §6: Event-Payload ist kanonisches snake_case-JSON (gleiche Optionen wie die Endpunkte).
    private static readonly JsonSerializerOptions EventPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public BlobSweeper(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Führt einen vollständigen Mark-and-Sweep für <paramref name="tenantId"/> aus: löscht
    /// nicht-referenzierte und verwaiste Blobs jenseits der Grace-Period und emittiert für JEDE
    /// Löschung ein <c>blob_swept</c>-Event — session-zugeordnete mit ihrer Session, verwaiste
    /// (un-attributierbare) als tenant-gescoptes Event mit <c>session_id = null</c> (Spec §7.1).
    /// </summary>
    public async Task<BlobSweepResult> SweepTenantAsync(
        string tenantId, CtxmanDbContext db, IBlobStore blobStore, CancellationToken ct)
    {
        // Mark + Session-Zuordnung in EINEM Durchgang über die Segmente des Tenants.
        // BlobRef ist als JSON-Spalte (Value Converter) abgelegt ⇒ nicht serverseitig filterbar;
        // der Schlüssel-Abgleich läuft daher in-memory. Der QueryFilter ist bereits tenant-gescopt.
        var segmentBlobs = await db.Segments
            .Where(s => s.BlobRef != null)
            .Select(s => new { s.SessionId, s.BlobRef, s.State })
            .ToListAsync(ct);

        // Spec §7.1 Schritt 1 (Mark): Keys mit ≥ 1 Live-Referenz = Segmente im Zustand Externalized.
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);

        // Beliebige (auch nicht-live) Segment-Zeile je Key ⇒ Session-Link für das blob_swept-Event.
        // I3 (Spec §2.2): externalisierte Segmente behalten ihren BlobRef auch nach Eviction, daher
        // existiert für einen "unreferenced"-Key oft noch eine Zeile mit nutzbarer SessionId.
        var keyToSession = new Dictionary<string, Ulid>(StringComparer.Ordinal);

        foreach (var row in segmentBlobs)
        {
            var key = row.BlobRef!.Key;
            if (row.State == SegmentState.Externalized)
            {
                liveKeys.Add(key);
            }

            keyToSession.TryAdd(key, row.SessionId);
        }

        var now = _clock.GetUtcNow();
        var grace = PolicyConfig.Default().Retention.BlobGrace;

        var deletions = new List<BlobSweepDeletion>();

        await foreach (var blob in blobStore.List(tenantId, ct))
        {
            // Spec §7.1 Schritt 2/3: Grace-Period schützt das Race "Put committed, Segment-Update
            // noch nicht" und lässt ein Forensik-Fenster offen.
            if (now - blob.LastModified <= grace)
            {
                continue;
            }

            if (liveKeys.Contains(blob.Key))
            {
                continue; // Live-Referenz ⇒ niemals löschen.
            }

            // reason (Spec §7.1 Schritt 4): existiert IRGENDEINE Segment-Zeile mit diesem Key, ist
            // der Blob "unreferenced" (Referenz endete); existiert keine, ist er "orphan"
            // (Crash zwischen Put und Append).
            var hasSession = keyToSession.TryGetValue(blob.Key, out var sessionId);
            var reason = hasSession ? "unreferenced" : "orphan";

            await blobStore.Delete(tenantId, blob.Key, ct);

            deletions.Add(new BlobSweepDeletion(
                blob.Key, blob.SizeBytes, reason, hasSession ? sessionId : null));
        }

        // Spec §6: blob_swept-Events für ALLE Löschungen emittieren — auch verwaiste. Verwaiste
        // (un-attributierbare) Löschungen werden als tenant-gescoptes Lifecycle-Event mit
        // session_id = null persistiert (Spec §7.1) und sind so vom after_seq-Cursor einer Session
        // strukturell isoliert.
        if (deletions.Count > 0)
        {
            await EmitEventsAsync(db, tenantId, deletions, now, ct);
        }

        return new BlobSweepResult(
            Deleted: deletions,
            Unattributed: deletions.Where(d => d.SessionId is null).ToList());
    }

    private static async Task EmitEventsAsync(
        CtxmanDbContext db,
        string tenantId,
        IReadOnlyList<BlobSweepDeletion> deletions,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Spec §6: Event-seq ist pro Session monoton (Outbox-Cursor, §4.3 after_seq) ⇒ MAX(seq)+1
        // je betroffener Session.
        var nextSeqBySession = new Dictionary<Ulid, long>();
        foreach (var sessionId in deletions
            .Where(d => d.SessionId is not null)
            .Select(d => d.SessionId!.Value)
            .Distinct())
        {
            var maxSeq = await db.Events
                .Where(ev => ev.SessionId == sessionId)
                .Select(ev => (long?)ev.Seq)
                .MaxAsync(ct);
            nextSeqBySession[sessionId] = (maxSeq ?? -1) + 1;
        }

        // Session-lose Löschungen (verwaiste Blobs, §7.1): ein einzelner Zähler pro Tenant über die
        // null-Session-Partition. Der Query honoriert den (tenant-gescopten) Query-Filter.
        long? nextSeqOrphan = null;
        if (deletions.Any(d => d.SessionId is null))
        {
            var maxOrphanSeq = await db.Events
                .Where(ev => ev.SessionId == null)
                .Select(ev => (long?)ev.Seq)
                .MaxAsync(ct);
            nextSeqOrphan = (maxOrphanSeq ?? -1) + 1;
        }

        var events = new List<EventRecord>(deletions.Count);
        foreach (var deletion in deletions)
        {
            long seq;
            if (deletion.SessionId is { } sessionId)
            {
                seq = nextSeqBySession[sessionId]++;
            }
            else
            {
                seq = nextSeqOrphan!.Value;
                nextSeqOrphan = seq + 1;
            }

            events.Add(new EventRecord
            {
                Id = Ulid.NewUlid(),
                TenantId = tenantId,
                SessionId = deletion.SessionId,
                Type = "blob_swept",
                Payload = JsonSerializer.Serialize(new
                {
                    key = deletion.Key,
                    size_bytes = deletion.SizeBytes,
                    reason = deletion.Reason,
                }, EventPayloadOptions),
                Seq = seq,
                CreatedAt = now,
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Events.AddRange(events);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}

/// <summary>Eine einzelne durch den Sweep gelöschte Blob-Referenz (Spec §7.1).</summary>
public sealed record BlobSweepDeletion(string Key, long SizeBytes, string Reason, Ulid? SessionId);

/// <summary>
/// Ergebnis eines Tenant-Sweeps. <see cref="Unattributed"/> listet gelöschte Blobs, denen keine
/// Session zuordenbar war (verwaiste Blobs). Diese werden nicht mehr verworfen, sondern als
/// <c>blob_swept</c>-Outbox-Events mit <c>session_id = null</c> emittiert (Spec §7.1); die Liste
/// bleibt rein zu Diagnose-Zwecken erhalten.
/// </summary>
public sealed record BlobSweepResult(
    IReadOnlyList<BlobSweepDeletion> Deleted,
    IReadOnlyList<BlobSweepDeletion> Unattributed);
