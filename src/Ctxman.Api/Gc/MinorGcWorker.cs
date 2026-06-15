using System.Text.Json;
using Ctxman.Core;
using Ctxman.Core.Domain;
using Ctxman.Core.Gc;
using Ctxman.Core.Persistence;
using Ctxman.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Gc;

/// <summary>
/// Hintergrund-Worker für GC-Jobs (Spec §3.2 / §8). Liest <see cref="MinorGcJob"/>s aus dem
/// <see cref="ChannelGcJobQueue"/> und arbeitet sie außerhalb des Request-Pfads ab.
///
/// Nebenläufigkeit (Spec §8): mehrere Consumer-Loops verarbeiten Jobs PARALLEL, damit ein
/// langsamer Major-Job (LLM-I/O, bis zu Sekunden) nicht die GC aller anderen Sessions blockiert
/// (Head-of-Line-Blocking). Die Serialisierung „keine zwei parallelen Collections auf derselben
/// Session" bleibt erhalten — sie wird vom <see cref="ISessionGcLock"/> pro session_id erzwungen
/// (prozesslokal in Tests, <c>pg_advisory_lock(session_id)</c> über Replikas hinweg in Produktion).
///
/// Tenant-Scope: der Worker läuft OHNE HTTP-Request, also ohne von der Middleware gesetzten
/// <see cref="ITenantContext"/>. Pro Job wird ein eigener DI-Scope gebaut und der
/// <see cref="TenantContext"/> dort auf den Tenant des Jobs gesetzt, BEVOR der
/// <see cref="CtxmanDbContext"/> aufgelöst wird — sonst greift der Global Query Filter (§10) leer.
/// </summary>
public sealed class MinorGcWorker : BackgroundService
{
    // Spec §8: Grad der Job-Parallelität. Pro-Session-Serialisierung übernimmt der ISessionGcLock —
    // diese Schranke verhindert nur, dass beliebig viele LLM-Major-Jobs gleichzeitig laufen.
    private const int MaxConcurrency = 4;

    private readonly ChannelGcJobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISessionGcLock _sessionLock;
    private readonly MajorCollection _majorCollection;
    private readonly ILogger<MinorGcWorker> _logger;

    // Spec §6: Event-Payload ist kanonisches snake_case-JSON (gleiche Optionen wie die Endpunkte).
    private static readonly JsonSerializerOptions EventPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public MinorGcWorker(
        ChannelGcJobQueue queue,
        IServiceScopeFactory scopeFactory,
        ISessionGcLock sessionLock,
        MajorCollection majorCollection,
        ILogger<MinorGcWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _sessionLock = sessionLock;
        _majorCollection = majorCollection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Spec §8: mehrere parallele Consumer auf derselben Queue (kein Head-of-Line-Blocking).
        var consumers = new Task[MaxConcurrency];
        for (var i = 0; i < MaxConcurrency; i++)
        {
            consumers[i] = ConsumeAsync(stoppingToken);
        }

        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Sauberes Herunterfahren — kein Logging als Fehler.
            }
            catch (Exception ex)
            {
                // GC ist Best-Effort und darf den Worker-Loop nicht killen (Spec §3.2 / §8).
                _logger.LogError(ex, "Minor GC job {JobId} for session {SessionId} failed.",
                    job.JobId, job.SessionId);
            }
            finally
            {
                // Test-Hook (WaitForIdleAsync): Job gilt als abgearbeitet, auch bei Fehler/No-op.
                _queue.MarkProcessed();
            }
        }
    }

    private async Task ProcessJobAsync(MinorGcJob job, CancellationToken ct)
    {
        // Spec §8: Minor und Major teilen denselben Per-Session-Lock — niemals zwei parallele
        // Collections auf derselben Session (pg_advisory_lock(session_id) / prozesslokales Äquivalent).
        await using var handle = await _sessionLock.AcquireAsync(job.TenantId, job.SessionId, ct);

        if (job.Level == GcLevel.Major)
        {
            await _majorCollection.ExecuteAsync(job.TenantId, job.SessionId, ct);
        }
        else
        {
            await RunMinorAsync(job, ct);
        }
    }

    private async Task RunMinorAsync(MinorGcJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        // Tenant SETZEN, bevor der DbContext aufgelöst wird: der Worker läuft außerhalb eines
        // Requests, daher ist der scoped TenantContext leer. TenantContext ist die konkrete,
        // mutable Implementierung (Program.cs registriert ITenantContext darauf gemappt).
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.TenantId = job.TenantId;

        var db = scope.ServiceProvider.GetRequiredService<CtxmanDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == job.SessionId, ct);
        if (session is null)
        {
            // Session existiert nicht (mehr) oder fremder Tenant ⇒ nichts zu tun.
            return;
        }

        var segments = await db.Segments
            .Where(s => s.SessionId == job.SessionId)
            .ToListAsync(ct);

        // Spec §3.2: vollständige Minor Collection als Plan. Eviction-Phasen mutieren die Segmente
        // bereits in-memory; die Externalisierungs-Kandidaten führt der Worker I/O-behaftet aus.
        var plan = MinorCollector.PlanFull(segments, session.Policy, session.CurrentTurn);

        if (plan.CleanPageEvicted.Count == 0
            && plan.UnitEvicted.Count == 0
            && plan.ExternalizationCandidates.Count == 0)
        {
            return; // Nichts zu tun ⇒ keine Transaktion, keine Events.
        }

        var now = DateTimeOffset.UtcNow;

        // Spec §6: Event-seq ist pro Session monoton (Outbox-Cursor, §4.3 after_seq) — MAX(seq)+1.
        var maxEventSeq = await db.Events
            .Where(ev => ev.SessionId == job.SessionId)
            .Select(ev => (long?)ev.Seq)
            .MaxAsync(ct);
        var nextEventSeq = (maxEventSeq ?? -1) + 1;

        var events = new List<EventRecord>();

        // Phase 2 (Spec §3.2.2): Externalisierung mit Blob-Write ausführen, dann Segment.Externalize.
        foreach (var candidate in plan.ExternalizationCandidates)
        {
            await using var contentStream = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(candidate.Content));

            var blobRef = await blobStore.Put(
                job.TenantId, contentStream, "text/plain; charset=utf-8", ct);

            candidate.Segment.Externalize(blobRef, candidate.Summary);

            events.Add(CreateEvent(job, "segment_externalized", new
            {
                segment_id = candidate.Segment.Id.ToString(),
                blob_key = blobRef.Key,
            }, nextEventSeq++, now));
        }

        // Phase 1 (Spec §3.2.1): Clean-Page-Eviction operiert auf refetchable Einzel-Segmenten ⇒
        // ein segment_evicted je Segment. Segmente sind in-memory bereits mutiert.
        foreach (var evicted in plan.CleanPageEvicted)
        {
            events.Add(CreateEvent(job, "segment_evicted", new
            {
                segment_id = evicted.Id.ToString(),
            }, nextEventSeq++, now));
        }

        // Phase 3 (Spec §3.2.3 / §6): TTL-Eviction operiert auf UNITS ⇒ ein unit_evicted je Unit.
        // Eine gekoppelte tool_call+tool_result-Unit ergibt EIN Event über beide Segmente (§2.4).
        foreach (var unit in plan.UnitEvicted)
        {
            events.Add(CreateEvent(job, "unit_evicted", new
            {
                unit_id = unit.UnitId,
                segment_ids = unit.Segments.Select(s => s.Id.ToString()).ToArray(),
            }, nextEventSeq++, now));
        }

        // Spec §3.2 / §6: Zustandsänderungen + Events atomar in EINER Transaktion persistieren.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Events.AddRange(events);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static EventRecord CreateEvent(
        MinorGcJob job, string type, object payload, long seq, DateTimeOffset now) =>
        new()
        {
            Id = Ulid.NewUlid(),
            TenantId = job.TenantId,
            SessionId = job.SessionId,
            Type = type,
            Payload = JsonSerializer.Serialize(payload, EventPayloadOptions),
            Seq = seq,
            CreatedAt = now,
        };
}
