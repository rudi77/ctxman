using Ctxman.Api.Auth;
using Ctxman.Api.Compaction;
using Ctxman.Api.Endpoints;
using Ctxman.Api.Gc;
using Ctxman.Api.Idempotency;
using Ctxman.Api.Notifications;
using Ctxman.Api.Observability;
using Ctxman.Api.Promotion;
using Ctxman.Api.Storage;
using Ctxman.Core;
using Prometheus;
using Ctxman.Core.Auth;
using Ctxman.Core.Compaction;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Core.Promotion;
using Ctxman.Core.Rendering;
using Ctxman.Core.Storage;
using Ctxman.Core.Tokenization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Spec §4.1: Auth-Konfiguration aus Sektion `auth`. AuthMode bindet case-insensitiv aus dem
// snake_case-String ("none" -> AuthMode.None) über den Standard-Konfigurationsbinder.
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

// Spec §4.1: JwtBearer immer registrieren (harmlos wenn Modus != jwt, da UseAuthentication
// nur im jwt-Modus in die Pipeline eingefügt wird). Production-Parameter aus auth:jwt;
// Tests überschreiben via PostConfigure<JwtBearerOptions> — kein Authority-Discovery in Tests.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        // Spec §4.1: production OIDC/JWT params from auth:jwt. Tests override via PostConfigure.
        var jwt = builder.Configuration.GetSection("auth").GetSection("jwt");
        var authority = jwt["authority"];
        if (!string.IsNullOrWhiteSpace(authority)) options.Authority = authority;
        var issuer  = jwt["issuer"];
        var audience = jwt["audience"];
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer           = !string.IsNullOrWhiteSpace(issuer),
            ValidIssuer              = issuer,
            ValidateAudience         = !string.IsNullOrWhiteSpace(audience),
            ValidAudience            = audience,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
        };
    });

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

// Spec §3.3: PromotionService extrahiert Fakten via ICompactionModel und schreibt an IPromotionSink.
// Scoped (nicht Singleton): nutzt Singleton ICompactionModel + IPromotionSink — kein Captive-Dependency.
// Gibt PendingPromotionEvent-Liste zurück (ohne seq); Caller setzt seq im eigenen Transaction-Scope.
builder.Services.AddScoped<PromotionService>();

// Spec §8: konservativer Default-Token-Zähler (stateless ⇒ Singleton). Provider-genaue Zähler
// sind eigene Registrierungen.
builder.Services.AddSingleton<ITokenCounter, HeuristicTokenCounter>();

// Spec §4.6: zustandslose Provider-Adapter-Registry (Anthropic + OpenAI in v1).
builder.Services.AddSingleton<IProviderAdapter, AnthropicMessagesAdapter>();
builder.Services.AddSingleton<IProviderAdapter, OpenAiChatAdapter>();
builder.Services.AddSingleton<ProviderAdapterRegistry>();

// Spec §7: Blob-Adapter-Auswahl über `blobstore:provider` (default "fs" = Filesystem, "azure-blob" = Azure).
// Credentials via Standard-.NET-Konfigurationskette (N5).
builder.Services.Configure<BlobStoreOptions>(builder.Configuration.GetSection("blobstore"));
builder.Services.PostConfigure<BlobStoreOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.Root))
    {
        o.Root = Path.Combine(Path.GetTempPath(), "ctxman-blobs");
    }
});

var blobProvider = builder.Configuration["blobstore:provider"] ?? "fs";
if (blobProvider.Equals("azure-blob", StringComparison.OrdinalIgnoreCase))
{
    // Spec §7: Azure Blob Storage-Adapter. Container-Client aus Options, Gateway als Seam,
    // AzureBlobStore enthält die gesamte Content-Addressing- und Tenant-Logik.
    builder.Services.AddSingleton<IAzureBlobGateway>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<BlobStoreOptions>>().Value;
        var containerClient = new Azure.Storage.Blobs.BlobContainerClient(
            opts.ConnectionString,
            opts.ContainerName);
        return new AzureBlobContainerGateway(containerClient);
    });
    builder.Services.AddSingleton<IBlobStore, AzureBlobStore>();
}
else
{
    // Default: "fs" — Filesystem-Adapter (Dev/Test).
    builder.Services.AddSingleton<IBlobStore, FileSystemBlobStore>();
}

// Spec §7.1: Cold-Storage-Exporter (best-effort beim Archivieren, wenn archived_session_blobs == "cold_storage").
builder.Services.Configure<ColdStorageOptions>(builder.Configuration.GetSection(ColdStorageOptions.SectionName));
builder.Services.PostConfigure<ColdStorageOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.Root))
    {
        o.Root = Path.Combine(Path.GetTempPath(), "ctxman-cold");
    }
});
builder.Services.AddSingleton<IColdStorageExporter, ColdStorageExporter>();

// Spec §8: Minor-GC läuft asynchron außerhalb des Request-Pfads — Channel-Queue (Singleton) +
// Hosted-Service-Worker. Der Worker serialisiert pro session_id und setzt den Tenant-Scope selbst.
builder.Services.AddSingleton<ChannelGcJobQueue>();
builder.Services.AddSingleton<IGcJobQueue>(sp => sp.GetRequiredService<ChannelGcJobQueue>());

// Spec §8: Shared per-Session-Lock (Minor + Major teilen denselben Singleton — WP5 Subtask 5).
// Entspricht dem SQLite-Äquivalent zu pg_advisory_lock(session_id); echter Postgres-Lock in WP7.
builder.Services.AddSingleton<SessionGcLocks>();

// Spec §3.3 / §8: CompactionOptions aus Sektion "Compaction" (Non-Goal N5: Credentials via Config).
builder.Services.Configure<CompactionOptions>(builder.Configuration.GetSection(CompactionOptions.SectionName));

// Spec §3.3 / §8: ICompactionModel — Adapter-Auswahl über Compaction:Provider (default "anthropic").
// Singleton-Registrierung via IHttpClientFactory-Factory: HttpClient ist thread-safe und für
// Singleton-Nutzung ausgelegt; IHttpClientFactory rotiert den Handler intern (DNS-TTL-sicher).
// MajorCollection ist Singleton ⇒ Adapter muss Singleton sein — kein Captive-Dependency.
builder.Services.AddHttpClient(); // registriert IHttpClientFactory falls nicht bereits vorhanden.
var compactionProvider = builder.Configuration["Compaction:Provider"] ?? "anthropic";
if (compactionProvider.Equals("azure_openai", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ICompactionModel>(sp =>
        new AzureOpenAiCompactionModel(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AzureOpenAiCompactionModel)),
            sp.GetRequiredService<IOptions<CompactionOptions>>()));
}
else
{
    // Default: "anthropic" (Spec §8).
    builder.Services.AddSingleton<ICompactionModel>(sp =>
        new AnthropicCompactionModel(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AnthropicCompactionModel)),
            sp.GetRequiredService<IOptions<CompactionOptions>>()));
}

// Spec §3.3 / §5: IPromotionSink — WebhookPromotionSink als Singleton mit IHttpClientFactory.
builder.Services.AddSingleton<IPromotionSink>(sp =>
    new WebhookPromotionSink(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(WebhookPromotionSink))));

// Spec §3.3: MajorCollection führt Major GC aus (Promotion→Compaction — Logik in Subtask 6).
// Singleton, weil zustandslos; nutzt IServiceScopeFactory für per-Job-Scoping.
builder.Services.AddSingleton<MajorCollection>();

builder.Services.AddHostedService<MinorGcWorker>();

// Spec §7.1: Blob-Mark-and-Sweep als Hosted Service (Default täglich, pro Tenant, serialisiert).
// TimeProvider als Clock-Abstraktion, damit Tests die Grace-Grenze deterministisch steuern können
// (BlobSweeper ist auch direkt aufrufbar). TimeProvider.System ist der reale Default.
builder.Services.AddSingleton(TimeProvider.System);

// Spec §7.1: Retention-Konfiguration aus Sektion "retention" (snake_case via ConfigurationKeyName).
// Als Singleton registriert, damit BlobSweeper und BlobSweepWorker per DI injiziert bekommen.
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<RetentionOptions>>().Value.ToRetentionConfig());

builder.Services.AddSingleton<BlobSweeper>();
builder.Services.AddHostedService<BlobSweepWorker>();

// Spec §6: Prometheus metrics singleton — collectors register at construction time.
builder.Services.AddSingleton<CtxmanMetrics>();

// Live-Discovery: In-Memory-Hub für Session-Lifecycle-Push (session_created/_archived). Singleton,
// damit POST /v1/sessions (Producer) und der SSE-Endpunkt (Consumer) dieselbe Instanz teilen.
builder.Services.AddSingleton<SessionNotificationHub>();

// Wire-Format ist snake_case (CLAUDE.md): HealthzResponse.AuthMode -> "auth_mode".
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

// OpenAPI-Dokument für /openapi/v1.json (Microsoft.AspNetCore.OpenApi). Scalar rendert daraus die
// interaktive API-Referenz unter /scalar. Beide Endpunkte tragen keine ResourceAction-Metadaten,
// werden also von der CtxmanAuthorizationMiddleware übersprungen (wie /healthz und /metrics).
builder.Services.AddOpenApi();

var app = builder.Build();

// Spec §6: force-resolve CtxmanMetrics so all series appear in /metrics at 0 from startup.
_ = app.Services.GetRequiredService<CtxmanMetrics>();

// Spec §4.1: Modus `none` hat keine Authentifizierung — beim Start klar warnen.
var authOptions = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
if (authOptions.Mode == AuthMode.None)
{
    app.Logger.LogWarning(
        "Auth mode 'none' active — all requests resolve to a tenant from header/default with no " +
        "authentication. Do not use in production.");
}

// Spec §4.1: Im jwt-Modus muss UseAuthentication() VOR TenantResolutionMiddleware laufen,
// damit context.User/IsAuthenticated bereits gesetzt ist, wenn der Middleware der Claim liest.
if (authOptions.Mode == AuthMode.Jwt)
{
    app.UseAuthentication();
}

// Spec §4.1 / §8: Tenant-Auflösung läuft für jeden Request vor den Endpoints.
app.UseMiddleware<TenantResolutionMiddleware>();

// Spec §4.1: UseRouting() erzwingt das Endpoint-Matching VOR CtxmanAuthorizationMiddleware,
// damit context.GetEndpoint() in der Middleware bereits die ResourceAction-Metadaten liefert.
// In WebApplication (Minimal Hosting) läuft das Endpoint-Matching sonst erst nach der
// Middleware-Pipeline — ohne diesen Aufruf wäre GetEndpoint() immer null.
app.UseRouting();

// Spec §4.1 / §8: Authorization läuft nach TenantResolution (Handler sieht bereits aufgelösten Tenant).
// Pipeline-Reihenfolge: TenantResolution → [Authentication] → Authorization → Endpoints.
app.UseMiddleware<CtxmanAuthorizationMiddleware>();

// OpenAPI-JSON unter /openapi/v1.json + Scalar-UI unter /scalar. Im Auth-Modus `none` (Dev)
// frei erreichbar; in api_key/jwt-Modus greift die normale TenantResolution-Middleware.
app.MapOpenApi();
app.MapScalarApiReference();

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

// Spec §4.3 / §8: GC-Trigger (POST /v1/sessions/{sid}/gc — minor | major, 202 { job_id }).
app.MapGcEndpoints();

// Spec §3.4 / §4.3: Page-Fault (GET /v1/sessions/{sid}/refs/{segment_id} — Lazy-Expansion).
app.MapRefEndpoints();

// Spec §4.3 / §6: Event-Outbox (GET /v1/sessions/{sid}/events?after_seq=… — Pull + SSE).
app.MapEventEndpoints();

// Spec §2.5 / §4.3: Frame-Endpunkte (POST /v1/sessions/{sid}/frames — push).
app.MapFrameEndpoints();

// Spec §6: Prometheus scrape endpoint — no ResourceAction metadata, auth middleware skips it.
app.MapMetrics();

app.Run();

// /healthz-Metadaten (Spec §4.1): Status + aktiver Auth-Modus als snake_case-Wire-String.
internal sealed record HealthzResponse(string Status, string AuthMode);

// Für WebApplicationFactory-basierte API-Tests sichtbar machen.
public partial class Program;
