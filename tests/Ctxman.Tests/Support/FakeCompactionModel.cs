using System.Collections.Concurrent;
using Ctxman.Core.Compaction;

namespace Ctxman.Tests.Support;

/// <summary>
/// Deterministischer, netzwerkfreier <see cref="ICompactionModel"/> für Tests (AC2).
/// Gibt für den Fact-Extraction-Template ("fact-extraction-v1") einen anderen, NON-EMPTY
/// Text zurück als für alle anderen Templates — so können Tests prüfen, welcher Aufruf
/// welche Rolle hatte, ohne echte LLM-Calls. Thread-sicher (als Singleton verwendet).
///
/// Erweiterungen für WP5-Gap-Tests:
/// - <see cref="OnSummarize"/>: optionaler Hook, der auf dem COMPACTION-Aufruf (nicht dem
///   Promotion-Aufruf) ausgelöst wird. Damit kann ein Test eine nebenläufige DB-Schreiboperation
///   NACH dem Fenstereinfrieren, aber VOR der Transaktion, simulieren (Spec §3.3 step 3).
/// - <see cref="EmptyPromotionResult"/>: wenn true, gibt der Promotion-Aufruf ("fact-extraction-v1")
///   einen leeren String zurück — testet den Empty-Extraction-Branch (Gap 2 / AC4).
/// Alle Defaults bewahren das bisherige Verhalten, sodass bestehende Tests grün bleiben.
/// </summary>
public sealed class FakeCompactionModel : ICompactionModel
{
    private readonly ConcurrentBag<CompactionRequest> _received = new();

    /// <summary>Alle bisher empfangenen Anfragen (thread-sicher lesbar).</summary>
    public IReadOnlyCollection<CompactionRequest> ReceivedRequests => _received;

    /// <summary>
    /// Optionaler Hook, der NUR beim COMPACTION-Aufruf (PromptTemplateId != "fact-extraction-v1")
    /// ausgelöst wird — asynchron, BEVOR das Ergebnis zurückgegeben wird.
    /// Nützlich, um während des eingefrorenen Fensters nebenläufige DB-Operationen zu simulieren
    /// (Spec §3.3 step 3 Version-Isolation). Default: null (kein Hook).
    /// Thread-safe: volatile read/write; der Hook selbst muss thread-safe sein.
    /// </summary>
    public Func<CompactionRequest, Task>? OnSummarize;

    /// <summary>
    /// Wenn true, gibt der Promotion-Aufruf ("fact-extraction-v1") einen LEEREN String zurück,
    /// um den Empty-Extraction-Branch (Spec §3.3 / AC4) zu testen. Default: false.
    /// </summary>
    public bool EmptyPromotionResult { get; set; }

    public async Task<CompactionResult> SummarizeAsync(CompactionRequest request, CancellationToken ct)
    {
        _received.Add(request);

        var isPromotion = request.PromptTemplateId == "fact-extraction-v1";

        if (isPromotion)
        {
            // Promotion call: honour EmptyPromotionResult flag.
            var summary = EmptyPromotionResult
                ? string.Empty
                : $"FACT: extracted from {request.Window.Count} segments";
            return new CompactionResult(summary);
        }

        // Compaction call: fire optional hook BEFORE returning, so the test's concurrent
        // DB write happens while the worker is mid-LLM-call (after window freeze).
        var hook = OnSummarize;
        if (hook is not null)
        {
            await hook(request);
        }

        return new CompactionResult(
            $"SUMMARY: compacted {request.Window.Count} segments via {request.PromptTemplateId}");
    }

    /// <summary>
    /// Gibt alle empfangenen Requests mit dem gegebenen PromptTemplateId zurück.
    /// </summary>
    public IEnumerable<CompactionRequest> RequestsForTemplate(string promptTemplateId) =>
        _received.Where(r => r.PromptTemplateId == promptTemplateId);

    /// <summary>Setzt die aufgezeichneten Anfragen zurück (für isolierte Tests).</summary>
    public void Reset() => _received.Clear();
}
