using Ctxman.Core.Domain;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Persistence;

/// <summary>
/// Spec §4.4: <c>context_version</c> ist ein EF-Concurrency-Token. Lesen zwei Worker dieselbe
/// Version und schreiben beide, darf der zweite nicht still gewinnen (Lost-Update), sondern muss
/// als <see cref="DbUpdateConcurrencyException"/> auffallen — die Endpunkte übersetzen das in 409.
/// </summary>
public sealed class ConcurrencyTokenTests
{
    private const string Tenant = "t-concurrency";

    private static Session NewSession() => new(
        id: Ulid.NewUlid(),
        tenantId: Tenant,
        agentTemplateId: null,
        policy: PolicyConfig.Default(),
        contextVersion: 0,
        staticEpoch: 0,
        currentTurn: 0,
        status: SessionStatus.Active,
        createdAt: DateTimeOffset.UtcNow,
        updatedAt: DateTimeOffset.UtcNow);

    [Fact] // Spec §4.4: konkurrierendes Update auf derselben Version ⇒ DbUpdateConcurrencyException.
    public async Task ConcurrentVersionIncrement_SecondWriter_Conflicts()
    {
        using var factory = new SqliteDbContextFactory();

        var session = NewSession();
        await using (var seed = factory.CreateContext(Tenant))
        {
            seed.Sessions.Add(session);
            await seed.SaveChangesAsync();
        }

        // Zwei unabhängige Contexts lesen DIESELBE Version (0).
        await using var ctxA = factory.CreateContext(Tenant);
        await using var ctxB = factory.CreateContext(Tenant);

        var a = await ctxA.Sessions.SingleAsync(s => s.Id == session.Id);
        var b = await ctxB.Sessions.SingleAsync(s => s.Id == session.Id);

        // Worker A committet zuerst (0 → 1).
        a.IncrementVersion(DateTimeOffset.UtcNow);
        await ctxA.SaveChangesAsync();

        // Worker B schreibt gegen die veraltete Version 0 ⇒ Konflikt (kein Lost-Update).
        b.IncrementVersion(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());
    }

    [Fact] // Sequentielle Updates (jeweils frisch geladen) bleiben konfliktfrei.
    public async Task SequentialVersionIncrements_DoNotConflict()
    {
        using var factory = new SqliteDbContextFactory();

        var session = NewSession();
        await using (var seed = factory.CreateContext(Tenant))
        {
            seed.Sessions.Add(session);
            await seed.SaveChangesAsync();
        }

        for (var i = 0; i < 3; i++)
        {
            await using var ctx = factory.CreateContext(Tenant);
            var s = await ctx.Sessions.SingleAsync(x => x.Id == session.Id);
            s.IncrementVersion(DateTimeOffset.UtcNow);
            await ctx.SaveChangesAsync();
        }

        await using var verify = factory.CreateContext(Tenant);
        var final = await verify.Sessions.SingleAsync(x => x.Id == session.Id);
        Assert.Equal(3, final.ContextVersion);
    }
}
