using Ctxman.Api.Auth;
using Ctxman.Core;
using Ctxman.Core.Auth;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Spec §4.1: Auth-Konfiguration aus Sektion `auth`. AuthMode bindet case-insensitiv aus dem
// snake_case-String ("none" -> AuthMode.None) über den Standard-Konfigurationsbinder.
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

// Spec §10: dieselbe scoped TenantContext-Instanz für Middleware (setzt TenantId) und
// CtxmanDbContext (liest ITenantContext für den Global Query Filter).
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

// Spec §7/§8: DbContext scoped (für ITenantContext-Injektion). Kein Migrate/EnsureCreated beim
// Start — Tests verwalten das Schema und tauschen den Provider (WebApplicationFactory).
builder.Services.AddDbContext<CtxmanDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("ctxman")
        ?? "Host=localhost;Database=ctxman;Username=ctxman;Password=ctxman"));

// Spec §4.1: Default-Authorization erlaubt jede Aktion innerhalb des aufgelösten Tenants.
builder.Services.AddSingleton<ICtxmanAuthorizationHandler, AllowAllWithinTenantAuthorizationHandler>();

// Wire-Format ist snake_case (CLAUDE.md): HealthzResponse.AuthMode -> "auth_mode".
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

var app = builder.Build();

// Spec §4.1: Modus `none` hat keine Authentifizierung — beim Start klar warnen.
var authOptions = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
if (authOptions.Mode == AuthMode.None)
{
    app.Logger.LogWarning(
        "Auth mode 'none' active — all requests resolve to a tenant from header/default with no " +
        "authentication. Do not use in production.");
}

// Spec §4.1 / §8: Tenant-Auflösung läuft für jeden Request vor den Endpoints.
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapGet("/healthz", (IOptions<AuthOptions> options) =>
    Results.Ok(new HealthzResponse("ok", options.Value.Mode.ToWire())));

app.Run();

// /healthz-Metadaten (Spec §4.1): Status + aktiver Auth-Modus als snake_case-Wire-String.
internal sealed record HealthzResponse(string Status, string AuthMode);

// Für WebApplicationFactory-basierte API-Tests sichtbar machen.
public partial class Program;
