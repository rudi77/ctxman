using Ctxman.Core;
using Ctxman.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ctxman.Api.Persistence;

/// <summary>
/// Design-Time-Factory für <c>dotnet ef migrations ...</c>. Nötig, weil der
/// <see cref="CtxmanDbContext"/>-Konstruktor einen <see cref="ITenantContext"/> verlangt,
/// den es zur Design-Zeit nicht gibt — der leere <see cref="TenantContext"/> ist unkritisch,
/// da er nur in die Query-Filter eingeht, nicht ins Schema.
///
/// Migrationen liegen in Ctxman.Api (nicht in Ctxman.Core, dort keine Npgsql-Abhängigkeit) —
/// daher MigrationsAssembly explizit. Der Connection-String dient nur der Modell-Erstellung;
/// für <c>database update</c> kann er per Env <c>CTXMAN_CONNECTIONSTRING</c> überschrieben werden.
/// </summary>
public sealed class CtxmanDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CtxmanDbContext>
{
    public CtxmanDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CTXMAN_CONNECTIONSTRING")
            ?? "Host=localhost;Database=ctxman;Username=ctxman;Password=ctxman";

        var options = new DbContextOptionsBuilder<CtxmanDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(CtxmanDbContextDesignTimeFactory).Assembly.GetName().Name))
            .Options;

        return new CtxmanDbContext(options, new TenantContext());
    }
}
