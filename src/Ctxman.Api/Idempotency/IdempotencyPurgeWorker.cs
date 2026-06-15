using Ctxman.Core;
using Ctxman.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Idempotency;

/// <summary>
/// Räumt abgelaufene <c>idempotency_keys</c> auf (Spec §4.4: Aufbewahrung 24 h). Ohne diesen Job
/// wächst die Tabelle unbegrenzt — der Replay-Lookup ist zwar lazy (abgelaufene Einträge gelten als
/// nicht vorhanden), aber nie wiederverwendete Keys (der Normalfall: jeder Append/Render/Frame trägt
/// einen Unique-Key) würden sonst dauerhaft liegen bleiben.
///
/// Läuft auf einem festen Intervall (Default stündlich) und löscht <c>expires_at &lt; now</c> über
/// ALLE Tenants (<c>IgnoreQueryFilters</c> — eine wartungsartige Aggregation, kein Tenant-Scope).
/// <see cref="TimeProvider"/> macht das Intervall und die Grenze in Tests deterministisch;
/// <see cref="PurgeExpiredAsync"/> ist für Tests auch direkt aufrufbar.
/// </summary>
public sealed class IdempotencyPurgeWorker : BackgroundService
{
    // Spec §4.4: 24 h Retention; stündliches Aufräumen hält die Tabelle klein, ohne den Hot Path
    // zu belasten (Lazy-Cleanup deckt die Korrektheit ohnehin ab).
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<IdempotencyPurgeWorker> _logger;

    public IdempotencyPurgeWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        ILogger<IdempotencyPurgeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PurgeInterval, _clock);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CtxmanDbContext>();
                var deleted = await PurgeExpiredAsync(db, _clock.GetUtcNow(), stoppingToken);
                if (deleted > 0)
                {
                    _logger.LogInformation("Purged {Count} expired idempotency key(s).", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Sauberes Herunterfahren — kein Logging als Fehler.
            }
            catch (Exception ex)
            {
                // Best-Effort-Wartung; ein Fehler darf den Worker-Loop nicht killen.
                _logger.LogError(ex, "Idempotency key purge run failed.");
            }
        }
    }

    /// <summary>
    /// Löscht alle Keys mit <c>expires_at &lt; <paramref name="now"/></c> über alle Tenants und
    /// liefert die Anzahl gelöschter Zeilen (Spec §4.4).
    /// </summary>
    public static async Task<int> PurgeExpiredAsync(CtxmanDbContext db, DateTimeOffset now, CancellationToken ct) =>
        await db.IdempotencyKeys
            .IgnoreQueryFilters() // wartungsartige Cross-Tenant-Aggregation (kein Tenant-Scope).
            .Where(k => k.ExpiresAt < now)
            .ExecuteDeleteAsync(ct);
}
