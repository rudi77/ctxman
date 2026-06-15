using Ctxman.Api.Gc;

namespace Ctxman.Tests.Gc;

/// <summary>
/// Spec §8: der prozesslokale Per-Session-GC-Lock serialisiert Läufe DERSELBEN Session, lässt
/// verschiedene Sessions aber parallel laufen (kein Head-of-Line-Blocking, N2) und leakt keine
/// Semaphoren mehr (N3: Referenzzählung statt „ein Semaphore pro je gesehener Session").
/// </summary>
public sealed class InProcessSessionGcLockTests
{
    [Fact]
    public async Task SameSession_SecondAcquire_BlocksUntilFirstReleased()
    {
        var lockSvc = new InProcessSessionGcLock();
        var sid = Ulid.NewUlid();

        var first = await lockSvc.AcquireAsync("t", sid, CancellationToken.None);

        // Zweiter Acquire derselben Session darf nicht laufen, solange der erste gehalten wird.
        var secondTask = lockSvc.AcquireAsync("t", sid, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();

        // Jetzt geht der zweite durch.
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        await second.DisposeAsync();

        // N3: kein Leak — nach Freigabe aller Handles ist kein Lock-Eintrag mehr vorhanden.
        Assert.Equal(0, lockSvc.ActiveLockCount);
    }

    [Fact]
    public async Task DifferentSessions_DoNotBlockEachOther()
    {
        var lockSvc = new InProcessSessionGcLock();

        var a = await lockSvc.AcquireAsync("t", Ulid.NewUlid(), CancellationToken.None);
        // Andere Session blockiert nicht — Acquire schließt prompt ab.
        var b = await lockSvc
            .AcquireAsync("t", Ulid.NewUlid(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, lockSvc.ActiveLockCount);

        await a.DisposeAsync();
        await b.DisposeAsync();
        Assert.Equal(0, lockSvc.ActiveLockCount);
    }

    [Fact]
    public async Task RepeatedAcquireRelease_DoesNotLeak()
    {
        var lockSvc = new InProcessSessionGcLock();
        var sid = Ulid.NewUlid();

        for (var i = 0; i < 50; i++)
        {
            var handle = await lockSvc.AcquireAsync("t", sid, CancellationToken.None);
            await handle.DisposeAsync();
        }

        Assert.Equal(0, lockSvc.ActiveLockCount);
    }
}
