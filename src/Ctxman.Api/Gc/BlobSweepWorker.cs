using System.Collections.Concurrent;
using Ctxman.Core;
using Ctxman.Core.Persistence;
using Ctxman.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Gc;

/// <summary>
/// Hosted Service für den Blob-Mark-and-Sweep (Spec §7.1: „Hosted Service, Default täglich, pro
/// Tenant, Advisory-Lock-geschützt"). Läuft auf einem Intervall aus
/// <see cref="Ctxman.Core.Domain.RetentionConfig.SweepIntervalSpan"/> (Default täglich) und ruft
/// pro Tenant den <see cref="BlobSweeper"/> auf.
///
/// Serialisierung: ein <see cref="SemaphoreSlim"/> je Tenant (SQLite-Test-Äquivalent zu
/// <c>pg_advisory_lock</c>; der echte Postgres-Advisory-Lock folgt in WP7).
///
/// Tenant-Scope: der Worker läuft OHNE HTTP-Request, also ohne von der Middleware gesetzten
/// <see cref="ITenantContext"/>. Pro Tenant wird ein eigener DI-Scope gebaut und der
/// <see cref="TenantContext"/> dort gesetzt, BEVOR der <see cref="CtxmanDbContext"/> aufgelöst
/// wird — sonst greift der Global Query Filter (§10) leer (gleiches Muster wie
/// <see cref="MinorGcWorker"/>).
/// </summary>
public sealed class BlobSweepWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BlobSweeper _sweeper;
    private readonly TimeProvider _clock;
    private readonly ILogger<BlobSweepWorker> _logger;
    private readonly Ctxman.Core.Domain.RetentionConfig _retention;

    // Spec §7.1: pro Tenant ein Semaphor (SQLite-Äquivalent zu pg_advisory_lock).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tenantLocks = new();

    public BlobSweepWorker(
        IServiceScopeFactory scopeFactory,
        BlobSweeper sweeper,
        TimeProvider clock,
        ILogger<BlobSweepWorker> logger,
        Ctxman.Core.Domain.RetentionConfig? retention = null)
    {
        _scopeFactory = scopeFactory;
        _sweeper = sweeper;
        _clock = clock;
        _logger = logger;
        // Spec §7.1: sweep-Intervall aus injizierter RetentionConfig; Default = PolicyConfig-Default.
        _retention = retention ?? Ctxman.Core.Domain.PolicyConfig.Default().Retention;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Spec §7.1: Default täglich; der Wert kommt aus der injizierten Retention-Konfiguration.
        var interval = _retention.SweepIntervalSpan;
        using var timer = new PeriodicTimer(interval, _clock);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAllTenantsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Sauberes Herunterfahren — kein Logging als Fehler.
            }
            catch (Exception ex)
            {
                // Sweep ist Best-Effort und darf den Worker-Loop nicht killen (Spec §7.1).
                _logger.LogError(ex, "Blob sweep run failed.");
            }
        }
    }

    private async Task SweepAllTenantsAsync(CancellationToken ct)
    {
        var tenantIds = await EnumerateTenantsAsync(ct);

        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();

            var tenantLock = _tenantLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
            await tenantLock.WaitAsync(ct);
            try
            {
                await SweepTenantScopedAsync(tenantId, ct);
            }
            catch (Exception ex)
            {
                // Ein fehlschlagender Tenant darf die übrigen nicht blockieren (Spec §7.1).
                _logger.LogError(ex, "Blob sweep for tenant {TenantId} failed.", tenantId);
            }
            finally
            {
                tenantLock.Release();
            }
        }
    }

    private async Task<IReadOnlyList<string>> EnumerateTenantsAsync(CancellationToken ct)
    {
        // Cross-Tenant-Enumeration: kein Tenants-Table existiert ⇒ die Tenant-Menge wird aus den
        // vorhandenen Daten abgeleitet. Der Global Query Filter (§10) wäre hier kontraproduktiv,
        // daher IgnoreQueryFilters() für diese Admin-artige Aggregation.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CtxmanDbContext>();

        return await db.Sessions
            .IgnoreQueryFilters()
            .Select(s => s.TenantId)
            .Distinct()
            .ToListAsync(ct);
    }

    private async Task SweepTenantScopedAsync(string tenantId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        // Tenant SETZEN, bevor der DbContext aufgelöst wird (Worker läuft außerhalb eines Requests).
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.TenantId = tenantId;

        var db = scope.ServiceProvider.GetRequiredService<CtxmanDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();

        var result = await _sweeper.SweepTenantAsync(tenantId, db, blobStore, ct);

        if (result.Unattributed.Count > 0)
        {
            // Verwaiste Blobs ohne Session-Link werden gelöscht (Spec §7.1) und als
            // tenant-scoped blob_swept-Event mit session_id = null in die Outbox emittiert.
            _logger.LogInformation(
                "Blob sweep for tenant {TenantId} deleted {Count} blob(s) without an attributable "
                + "session; emitted as tenant-scoped (session_id = null) blob_swept event(s).",
                tenantId, result.Unattributed.Count);
        }
    }
}
