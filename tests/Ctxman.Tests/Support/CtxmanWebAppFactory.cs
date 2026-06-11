using System.Collections.Generic;
using Ctxman.Api.Auth;
using Ctxman.Core;
using Ctxman.Core.Auth;
using Ctxman.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ctxman.Tests.Support;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> für die API-Tests (CLAUDE.md: WebApplicationFactory
/// über <c>public partial Program</c>). Ersetzt die Npgsql-Registrierung des <see cref="CtxmanDbContext"/>
/// durch SQLite (kein Postgres in Tests, §8), erlaubt das Überschreiben der Auth-Konfiguration
/// (<c>auth:mode</c>, <c>auth:tenant_header</c>, <c>auth:default_tenant</c>) und mappt eine NUR im
/// Test-Host existierende Probe <c>GET /test/whoami</c>, die den aufgelösten Tenant zurückgibt —
/// damit lässt sich die Tenant-Pipeline (§4.1) beobachten, ohne Produktions-Endpoints (Scope WP1).
///
/// Die Probe wird über einen <see cref="IStartupFilter"/> nach der Tenant-Resolution-Middleware
/// eingehängt; <c>Program.cs</c> bleibt unangetastet.
/// </summary>
public sealed class CtxmanWebAppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = new();
    private readonly SqliteConnection _connection;
    private readonly string _blobRoot;

    public CtxmanWebAppFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Pro Factory-Instanz isoliertes Blob-Verzeichnis (Spec §7, Dev-Adapter). Eindeutig genug
        // über die GUID des Connection-Objekts statt Date/Random im statischen Scope.
        _blobRoot = Path.Combine(Path.GetTempPath(), "ctxman-test-blobs", Guid.NewGuid().ToString("n"));
        _settings["blobstore:Root"] = _blobRoot;
    }

    /// <summary>Überschreibt eine Konfigurations-Einstellung (z. B. <c>auth:tenant_header</c>).</summary>
    public CtxmanWebAppFactory WithSetting(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    /// <summary>
    /// Test-only: liefert einen <see cref="CtxmanDbContext"/> für den angegebenen Tenant, gebunden an
    /// DIESELBE offene SQLite-Verbindung wie der API-Host. Damit können Integrationstests den DB-Zustand
    /// (z. B. server-vergebene <c>seq</c>, Segment-Anzahl, Idempotency-Records) nach einem HTTP-Request
    /// direkt inspizieren. Reine Test-Infrastruktur; Program.cs/Produktionscode bleibt unberührt.
    /// </summary>
    public CtxmanDbContext CreateDbContext(string tenantId)
    {
        var options = new DbContextOptionsBuilder<CtxmanDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new CtxmanDbContext(options, new TenantContext { TenantId = tenantId });
    }

    /// <summary>
    /// Liefert einen Host mit aktiver test-only Probe <c>GET /test/whoami</c>. Die Probe ist bereits
    /// im Basis-Host registriert; diese Methode existiert für lesbare Aufrufe in den Pipeline-Tests.
    /// </summary>
    public WebApplicationFactory<Program> WithProbe() => WithWebHostBuilder(_ => { });

    /// <summary>
    /// Leitet einen Host ab, in dem der Default-<see cref="ICtxmanAuthorizationHandler"/> per DI
    /// durch <typeparamref name="THandler"/> ersetzt ist (Spec §4.1: per DI ersetzbar). Belegt,
    /// dass das Override gewinnt.
    /// </summary>
    public WebApplicationFactory<Program> WithAuthorizationHandler<THandler>()
        where THandler : class, ICtxmanAuthorizationHandler
    {
        return WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICtxmanAuthorizationHandler>();
                services.AddSingleton<ICtxmanAuthorizationHandler, THandler>();
            });
        });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:ctxman", "DataSource=:memory:");

        // In-Memory-Quelle als LETZTE Konfigurationsquelle ⇒ überschreibt appsettings.json
        // (Spec §4.1: auth:tenant_header etc. im Test überschreibbar). Die Factory-Callback läuft
        // nach den App-eigenen Quellen, daher gewinnt das Append.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            if (_settings.Count > 0)
            {
                config.AddInMemoryCollection(_settings);
            }
        });

        builder.ConfigureTestServices(services =>
        {
            // Npgsql-Registrierung des CtxmanDbContext entfernen und durch SQLite ersetzen.
            // Nur ein Provider darf pro Service-Provider registriert sein — daher alle von
            // AddDbContext erzeugten Options-/Konfigurations-Deskriptoren entfernen.
            RemoveDbContextRegistrations(services);

            services.AddDbContext<CtxmanDbContext>(o => o.UseSqlite(_connection));

            // AuthOptions-Overrides aus den Test-Settings als PostConfigure anwenden. In minimal
            // hosting erfasst Program.Configure<AuthOptions> die Sektion gegen den
            // ConfigurationManager, bevor der Test-Host seine In-Memory-Quelle anhängt — die
            // Optionen würden sonst die appsettings-Defaults behalten. PostConfigure läuft nach
            // allen Configure-Schritten und setzt die Test-Overrides verlässlich (Spec §4.1).
            if (_settings.Count > 0)
            {
                services.PostConfigure<AuthOptions>(ApplyAuthOverrides);
            }

            // Test-only Probe-Endpoint NUR im Test-Host (nicht in Program.cs).
            services.AddSingleton<IStartupFilter, WhoAmIProbeStartupFilter>();
        });
    }

    /// <summary>
    /// Erzeugt das Schema einmalig auf DERSELBEN offenen SQLite-Verbindung, die der API-Host für den
    /// <see cref="CtxmanDbContext"/> nutzt (<c>UseSqlite(_connection)</c> in <see cref="ConfigureWebHost"/>).
    /// Program.cs ruft bewusst kein EnsureCreated beim Start (Tests verwalten das Schema) — daher hier
    /// nach dem Host-Build, sonst hätten die Integrationstests leere Tabellen. Reine Test-Infrastruktur.
    ///
    /// Ein eigenständiger, an dieselbe offene Verbindung gebundener SQLite-Context erzeugt das Schema —
    /// es landet auf derselben In-Memory-DB, die der API-Host nutzt.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        var options = new DbContextOptionsBuilder<CtxmanDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CtxmanDbContext(options, new TenantContext { TenantId = "schema-setup" });
        db.Database.EnsureCreated();

        return host;
    }

    private void ApplyAuthOverrides(AuthOptions options)
    {
        if (_settings.TryGetValue("auth:mode", out var mode) && !string.IsNullOrWhiteSpace(mode))
        {
            options.Mode = Ctxman.Core.Domain.EnumWire.ParseAuthMode(mode);
        }

        if (_settings.TryGetValue("auth:tenant_header", out var header) && header is not null)
        {
            options.TenantHeader = header;
        }

        if (_settings.TryGetValue("auth:default_tenant", out var tenant) && tenant is not null)
        {
            options.DefaultTenant = tenant;
        }
    }

    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var toRemove = services
            .Where(d =>
                d.ServiceType == typeof(CtxmanDbContext)
                || d.ServiceType == typeof(DbContextOptions<CtxmanDbContext>)
                || d.ServiceType == typeof(DbContextOptions))
            .ToList();

        foreach (var descriptor in toRemove)
        {
            services.Remove(descriptor);
        }

        // Npgsql registriert seine Provider-Konfiguration über IDbContextOptionsConfiguration<T>;
        // wird das nicht entfernt, landen Npgsql- und SQLite-Provider auf demselben Options-Objekt.
        services.RemoveAll<IDbContextOptionsConfiguration<CtxmanDbContext>>();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            if (Directory.Exists(_blobRoot))
            {
                try
                {
                    Directory.Delete(_blobRoot, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort-Cleanup des Test-Blob-Verzeichnisses; Lecks im Temp sind unkritisch.
                }
            }
        }
    }
}
