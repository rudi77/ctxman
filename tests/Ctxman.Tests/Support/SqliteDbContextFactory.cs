using Ctxman.Core;
using Ctxman.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Support;

/// <summary>
/// Baut <see cref="CtxmanDbContext"/>-Instanzen über eine offen gehaltene SQLite-":memory:"-
/// Verbindung (CLAUDE.md Teststrategie: kein Postgres-Zwang). Solange die Verbindung offen ist,
/// existiert die In-Memory-DB; das ermöglicht mehrere Contexts über *dieselbe* DB — auch mit
/// unterschiedlichem <see cref="ITenantContext.TenantId"/>, was die Tenant-Isolations-Tests (§10)
/// brauchen (als Tenant A schreiben, als Tenant B lesen, eine DB).
///
/// <see cref="IDisposable"/>: schließt die Verbindung (und verwirft damit die In-Memory-DB).
/// </summary>
public sealed class SqliteDbContextFactory : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _schemaCreated;

    public SqliteDbContextFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    /// <summary>
    /// Liefert einen <see cref="CtxmanDbContext"/> mit dem angegebenen Tenant, gebunden an die
    /// gemeinsame offene Verbindung. Beim ersten Aufruf wird das Schema via
    /// <c>EnsureCreated()</c> erzeugt (einmalig — danach teilen sich alle Contexts diese DB).
    /// </summary>
    public CtxmanDbContext CreateContext(string tenantId)
    {
        var options = new DbContextOptionsBuilder<CtxmanDbContext>()
            .UseSqlite(_connection)
            .Options;

        var context = new CtxmanDbContext(options, new TenantContext { TenantId = tenantId });

        if (!_schemaCreated)
        {
            context.Database.EnsureCreated();
            _schemaCreated = true;
        }

        return context;
    }

    /// <summary>
    /// Öffnet eine eigene Verbindung auf dieselbe In-Memory-DB? Nicht möglich bei ":memory:" —
    /// daher gibt diese Methode die gemeinsame, offen gehaltene Verbindung zurück, über die
    /// Tests rohes SQL ausführen können (z. B. SELECT der snake_case-Enum-Spalten).
    /// </summary>
    public SqliteConnection Connection => _connection;

    public void Dispose() => _connection.Dispose();
}
