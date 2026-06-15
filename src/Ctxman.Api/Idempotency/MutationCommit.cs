using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ctxman.Core.Persistence;

namespace Ctxman.Api.Idempotency;

/// <summary>
/// Gemeinsame Commit-Logik der mutierenden Endpunkte (Spec §4.4). Kapselt die zwei
/// Nebenläufigkeits-Fälle, die beim finalen <c>SaveChanges</c>+<c>Commit</c> auftreten können:
///
/// <list type="number">
/// <item><b>Optimistic-Concurrency-Konflikt</b> (Spec §4.4): zwei Worker lasen dieselbe
/// <c>context_version</c> und schreiben beide. Das Concurrency-Token auf <c>context_version</c>
/// lässt das UPDATE des zweiten 0 Zeilen treffen ⇒ <see cref="DbUpdateConcurrencyException"/>.
/// Antwort: <c>409</c> (retrybar), kein Lost-Update.</item>
/// <item><b>Idempotency-Key-Race</b> (Spec §4.4): zwei gleichzeitige Requests mit demselben Key
/// verfehlen beide den Replay-Lookup und fügen beide den PK <c>(tenant_id, key)</c> ein. Der
/// Verlierer bekommt eine <see cref="DbUpdateException"/> (Unique-Verletzung). Statt 500 wird die
/// inzwischen committete Antwort des Gewinners erneut geliefert (identische Antwort, Spec §4.4).</item>
/// </list>
/// </summary>
internal static class MutationCommit
{
    /// <summary>
    /// Committet <paramref name="tx"/>. Liefert <c>null</c> bei Erfolg; sonst ein
    /// <see cref="IResult"/> (409 bzw. Replay-Antwort), das der Endpunkt direkt zurückgibt.
    /// </summary>
    public static async Task<IResult?> TryCommitAsync(
        CtxmanDbContext db,
        IDbContextTransaction tx,
        IdempotencyService idempotency,
        string? idempotencyKey,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Spec §4.4: konkurrierende Mutation hat die Version weitergedreht — retrybarer Konflikt.
            await SafeRollbackAsync(tx, ct);
            return Results.Json(
                new { error = "context_version_conflict", retryable = true },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (DbUpdateException) when (idempotencyKey is not null)
        {
            // Spec §4.4: vermutlich der Idempotency-Key-PK-Race. Der Gewinner ist committet —
            // den ChangeTracker leeren, Replay erneut versuchen und dessen Antwort liefern.
            await SafeRollbackAsync(tx, ct);
            db.ChangeTracker.Clear();

            var replay = await idempotency.TryReplayAsync(idempotencyKey, ct);
            if (replay is not null)
            {
                return replay;
            }

            // War es doch kein Idempotency-Konflikt (kein Snapshot vorhanden) ⇒ unverändert
            // durchreichen statt eine Fehlersituation zu verschlucken.
            throw;
        }
    }

    private static async Task SafeRollbackAsync(IDbContextTransaction tx, CancellationToken ct)
    {
        try
        {
            await tx.RollbackAsync(ct);
        }
        catch
        {
            // Die Transaktion ist nach dem fehlgeschlagenen Commit ohnehin nicht mehr nutzbar;
            // ein Rollback-Fehler darf den eigentlichen (übersetzten) Fehler nicht überdecken.
        }
    }
}
