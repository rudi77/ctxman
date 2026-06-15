using Ctxman.Api.Idempotency;
using Ctxman.Core.Persistence;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Persistence;

/// <summary>
/// Spec §4.4: abgelaufene Idempotency-Keys (24 h Retention) werden vom
/// <see cref="IdempotencyPurgeWorker"/> gelöscht — über ALLE Tenants —, frische bleiben erhalten.
/// </summary>
public sealed class IdempotencyPurgeTests
{
    private static IdempotencyKeyRecord Record(string tenant, string key, DateTimeOffset created) => new()
    {
        Key = key,
        TenantId = tenant,
        SessionId = Ulid.NewUlid(),
        ResponseStatusCode = 201,
        ResponseBody = "{}",
        CreatedAt = created,
        ExpiresAt = created + TimeSpan.FromHours(24), // Spec §4.4.
    };

    [Fact]
    public async Task PurgeExpired_RemovesExpired_KeepsFresh_AcrossTenants()
    {
        using var factory = new SqliteDbContextFactory();
        var now = DateTimeOffset.UtcNow;

        await using (var seed = factory.CreateContext("seed"))
        {
            // Abgelaufen: vor >24 h erstellt (ExpiresAt < now).
            seed.IdempotencyKeys.Add(Record("tenant-a", "expired-a", now - TimeSpan.FromHours(30)));
            seed.IdempotencyKeys.Add(Record("tenant-b", "expired-b", now - TimeSpan.FromHours(25)));
            // Frisch: ExpiresAt > now.
            seed.IdempotencyKeys.Add(Record("tenant-a", "fresh-a", now - TimeSpan.FromHours(1)));
            await seed.SaveChangesAsync();
        }

        await using (var purgeCtx = factory.CreateContext("purge"))
        {
            var deleted = await IdempotencyPurgeWorker.PurgeExpiredAsync(purgeCtx, now, CancellationToken.None);
            Assert.Equal(2, deleted);
        }

        await using var verify = factory.CreateContext("verify");
        var remaining = await verify.IdempotencyKeys.IgnoreQueryFilters().ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("fresh-a", remaining[0].Key);
    }
}
