using System.Collections.Concurrent;
using Ctxman.Core.Promotion;

namespace Ctxman.Tests.Support;

/// <summary>
/// Write-only <see cref="IPromotionSink"/> für Tests (AC7). Speichert jeden
/// <see cref="WriteAsync"/>-Aufruf in einer thread-sicheren Liste, sodass Tests prüfen können,
/// ob und mit welchen Daten der Sink beschrieben wurde. Kein Netzwerk, kein I/O (AC2).
/// </summary>
public sealed class RecordingPromotionSink : IPromotionSink
{
    private readonly ConcurrentBag<(PromotedFact Fact, string SinkUrl)> _writes = new();

    /// <summary>Alle bisher geschriebenen Fakten mit zugehöriger Sink-URL.</summary>
    public IReadOnlyCollection<(PromotedFact Fact, string SinkUrl)> Writes => _writes;

    public Task WriteAsync(PromotedFact fact, string sinkUrl, CancellationToken ct)
    {
        _writes.Add((fact, sinkUrl));
        return Task.CompletedTask;
    }

    /// <summary>Setzt die aufgezeichneten Schreibzugriffe zurück (für isolierte Tests).</summary>
    public void Reset() => _writes.Clear();
}
