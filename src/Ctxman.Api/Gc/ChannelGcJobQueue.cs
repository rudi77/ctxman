using System.Threading.Channels;
using Ctxman.Core.Gc;

namespace Ctxman.Api.Gc;

/// <summary>
/// <see cref="IGcJobQueue"/> auf Basis eines unbounded <see cref="Channel{T}"/> (Spec §8). Als
/// Singleton registriert; der <c>MinorGcWorker</c> (BackgroundService) liest über
/// <see cref="Reader"/>. Ein Pending-Counter trägt den deterministischen Test-Hook
/// (<see cref="WaitForIdleAsync"/>): er zählt enqueued-aber-noch-nicht-abgearbeitete Jobs und
/// signalisiert Idle, sobald er auf 0 fällt.
/// </summary>
public sealed class ChannelGcJobQueue : IGcJobQueue
{
    private readonly Channel<MinorGcJob> _channel =
        Channel.CreateUnbounded<MinorGcJob>(new UnboundedChannelOptions
        {
            // Genau ein Consumer (der Worker); mehrere Producer (Request-Handler).
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly object _gate = new();
    private int _pending;
    private TaskCompletionSource _idle =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChannelGcJobQueue()
    {
        // Start im Idle-Zustand: kein Job pending ⇒ WaitForIdleAsync completed sofort.
        _idle.TrySetResult();
    }

    /// <summary>Reader-Seite für den Worker.</summary>
    public ChannelReader<MinorGcJob> Reader => _channel.Reader;

    public void Enqueue(MinorGcJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock (_gate)
        {
            if (_pending == 0)
            {
                // Übergang idle → busy: einen frischen, noch nicht abgeschlossenen Idle-Task anlegen.
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _pending++;
        }

        // Unbounded ⇒ TryWrite schlägt nur nach Complete() fehl (im Betrieb nie).
        _channel.Writer.TryWrite(job);
    }

    /// <summary>
    /// Vom Worker nach Abschluss EINES Jobs aufzurufen (auch bei Fehler/No-op). Fällt der
    /// Pending-Counter auf 0, wird der aktuelle Idle-Task signalisiert.
    /// </summary>
    internal void MarkProcessed()
    {
        lock (_gate)
        {
            if (_pending == 0)
            {
                return;
            }

            _pending--;
            if (_pending == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    public Task WaitForIdleAsync(CancellationToken ct = default)
    {
        Task idle;
        lock (_gate)
        {
            idle = _idle.Task;
        }

        return ct.CanBeCanceled ? idle.WaitAsync(ct) : idle;
    }
}
