using Ctxman.Core.Gc;

namespace Ctxman.Api.Gc;

/// <summary>
/// Producer-/Consumer-Queue für GC-Jobs (Spec §8: Hosted Service + Channel). Mutierende Endpunkte
/// (Append/Render) enqueuen einen <see cref="MinorGcJob"/>; der <c>MinorGcWorker</c> arbeitet ihn
/// asynchron außerhalb des Request-Pfads ab. Per-Session-Serialisierung übernimmt der Worker
/// (Spec §8: pg_advisory_lock(session_id)).
/// </summary>
public interface IGcJobQueue
{
    /// <summary>Reiht einen GC-Job ein. Nicht-blockierend (unbounded Channel).</summary>
    void Enqueue(MinorGcJob job);

    /// <summary>
    /// Test-Hook (CLAUDE.md Teststrategie): completed, sobald die Queue leer ist UND kein Job mehr
    /// in Bearbeitung ist. Erlaubt deterministisches Abwarten in <c>WebApplicationFactory</c>-Tests
    /// ohne <c>Thread.Sleep</c>-Races. Im Produktivbetrieb ungenutzt.
    /// </summary>
    Task WaitForIdleAsync(CancellationToken ct = default);
}
