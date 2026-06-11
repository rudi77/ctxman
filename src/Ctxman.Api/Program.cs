using Ctxman.Api.Auth;
using Ctxman.Api.Endpoints;
using Ctxman.Api.Idempotency;
using Ctxman.Api.Storage;
using Ctxman.Core;
using Ctxman.Core.Auth;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Core.Rendering;
using Ctxman.Core.Storage;
using Ctxman.Core.Tokenization;
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

// Spec §4.4: Replay-/Store-Logik für den Idempotency-Key, geteilt von den mutierenden Endpunkten.
builder.Services.AddScoped<IdempotencyService>();

// Spec §8: konservativer Default-Token-Zähler (stateless ⇒ Singleton). Provider-genaue Zähler
// sind eigene Registrierungen.
builder.Services.AddSingleton<ITokenCounter, HeuristicTokenCounter>();

// Spec §4.6: zustandslose Provider-Adapter-Registry (Anthropic + OpenAI in v1).
builder.Services.AddSingleton<IProviderAdapter, AnthropicMessagesAdapter>();
builder.Services.AddSingleton<IProviderAdapter, OpenAiChatAdapter>();
builder.Services.AddSingleton<ProviderAdapterRegistry>();

// Spec §7: Filesystem-Blob-Adapter (Dev). Root aus Sektion `blobstore`; ohne Konfiguration ein
// fester Default unter dem System-Temp-Pfad (keine Date/Random-APIs zur Startzeit).
builder.Services.Configure<BlobStoreOptions>(builder.Configuration.GetSection("blobstore"));
builder.Services.PostConfigure<BlobStoreOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.Root))
    {
        o.Root = Path.Combine(Path.GetTempPath(), "ctxman-blobs");
    }
});
builder.Services.AddSingleton<IBlobStore, FileSystemBlobStore>();

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

// Spec §4.3: Session-Endpunkte (POST /v1/sessions, GET /v1/sessions/{sid}).
app.MapSessionEndpoints();

// Spec §4.3: Segment-Endpunkte (POST /v1/sessions/{sid}/segments — Single + Batch).
app.MapSegmentEndpoints();

// Spec §4.3 / §7: Blob-Endpunkt (POST /v1/sessions/{sid}/blobs — Streaming-Upload).
app.MapBlobEndpoints();

// Spec §4.2 / §4.3: Render + Static-Epoch-Bump.
app.MapRenderEndpoints();

app.Run();

// /healthz-Metadaten (Spec §4.1): Status + aktiver Auth-Modus als snake_case-Wire-String.
internal sealed record HealthzResponse(string Status, string AuthMode);

// Für WebApplicationFactory-basierte API-Tests sichtbar machen.
public partial class Program;
