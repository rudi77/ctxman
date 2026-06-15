namespace Ctxman.Api.Gc;

/// <summary>
/// Prozesslokale <see cref="ISessionGcLock"/>-Implementierung (Dev/Test, Single-Replica). Ersetzt
/// den früheren <c>SessionGcLocks</c>: hält pro Session einen <see cref="SemaphoreSlim"/>, gibt ihn
/// aber per Referenzzählung wieder frei, sobald kein Job mehr Interesse hat — der frühere Ansatz
/// behielt einen Semaphore pro je gesehener Session und wuchs damit unbegrenzt.
///
/// Hinweis: serialisiert nur INNERHALB eines Prozesses. Über mehrere Replikas hinweg (Spec §8)
/// braucht es den <see cref="PostgresSessionGcLock"/> mit echtem <c>pg_advisory_lock</c>.
/// </summary>
public sealed class InProcessSessionGcLock : ISessionGcLock
{
    private sealed class Entry
    {
        public required SemaphoreSlim Semaphore { get; init; }
        public int RefCount;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _locks = new();

    /// <summary>Anzahl aktuell gehaltener/erwarteter Session-Locks — für Diagnose/Tests (Leak-Check).</summary>
    public int ActiveLockCount
    {
        get { lock (_gate) { return _locks.Count; } }
    }

    public async Task<IAsyncDisposable> AcquireAsync(string tenantId, Ulid sessionId, CancellationToken ct)
    {
        var key = SessionLockKey.Compute(tenantId, sessionId);

        Entry entry;
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out entry!))
            {
                entry = new Entry { Semaphore = new SemaphoreSlim(1, 1) };
                _locks[key] = entry;
            }

            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            // Beim Abbruch des Wartens das Interesse zurücknehmen (Semaphore wurde NICHT erworben).
            ReleaseInterest(key, entry, acquired: false);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void ReleaseInterest(long key, Entry entry, bool acquired)
    {
        if (acquired)
        {
            entry.Semaphore.Release();
        }

        lock (_gate)
        {
            entry.RefCount--;
            if (entry.RefCount == 0)
            {
                _locks.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private readonly InProcessSessionGcLock _owner;
        private readonly long _key;
        private readonly Entry _entry;
        private int _disposed;

        public Releaser(InProcessSessionGcLock owner, long key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public ValueTask DisposeAsync()
        {
            // Genau einmal freigeben, auch bei doppeltem Dispose.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.ReleaseInterest(_key, _entry, acquired: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
