# ctxman — forge project memory (operator seed)

> Orientierung für forge-Runs. **Vertrag bleibt** `docs/ctxman-spec.md` v0.2 — für jedes
> Workpaket die dort genannten Abschnitte und `docs/forge-work/wpN-acceptance.md` lesen.
> Dieses File ersetzt die Spec nicht.

## Was ctxman ist

Stateful .NET-9-Service für LLM-Context-Verwaltung (Stack/Heap/GC-Metapher). ctxman ruft
**nie** selbst das Agent-LLM auf (Non-Goal N1).

## Layout

| Pfad | Rolle |
|------|-------|
| `src/Ctxman.Core/` | Domäne, Rendering, Tokenization, Persistence-Modelle — **kein** ASP.NET |
| `src/Ctxman.Api/` | Minimal APIs, Middleware, EF-Wiring, `FileSystemBlobStore` |
| `tests/Ctxman.Tests/` | xUnit; Core unit tests + API via `WebApplicationFactory<Program>` |
| `ctxman.sln` | Solution — neue Projekte hier eintragen |

## Konventionen (bereits etabliert)

- **Minimal APIs**, gruppiert: `SessionEndpoints.cs`, `SegmentEndpoints.cs`, `BlobEndpoints.cs`,
  `RenderEndpoints.cs` — jeweils `Map*Endpoints(this IEndpointRouteBuilder)`.
- DTOs in sibling `*Dtos.cs`. Wire-Format **snake_case** via
  `JsonNamingPolicy.SnakeCaseLower` (auch Event-Payloads).
- **Tenant**: `TenantResolutionMiddleware` → scoped `TenantContext` / `ITenantContext`;
  `CtxmanDbContext` Global Query Filter — `tenant_id` nie aus dem Body.
- **IDs**: ULIDs (`Ulid`-Package). Domänenmodelle: `sealed class` / records; Enums über
  `EnumWire.cs` serialisiert.
- **Idempotency**: `IdempotencyService` — mutierende Endpoints; Key im Header
  `Idempotency-Key` (Pflicht bei `turn_advance=true` auf render).
- Spec-Invarianten in Code: Kommentare `// Spec §x.y` bei nicht-offensichtlichen Stellen.

## Bereits implementiert (WP1–WP3)

**Sessions** (`SessionEndpoints`):
- `POST /v1/sessions`, `GET /v1/sessions/{sid}`

**Segments** (`SegmentEndpoints`):
- `POST /v1/sessions/{sid}/segments` (single + batch append)
- Static-Kinds (`system_prompt`, `tool_def`, `skill_index`) → Append in Static-Region nur via
  Epoch-Bump, sonst `409` (I1)

**Blobs** (`BlobEndpoints`):
- Upload/Download über `IBlobStore` / `FileSystemBlobStore`

**Render** (`RenderEndpoints` + `Ctxman.Core/Rendering/`):
- `POST /v1/sessions/{sid}/render` — Provider-Adapter (`AnthropicMessagesAdapter`,
  `OpenAiChatAdapter`), `ProviderAdapterRegistry`, `RenderPlanner`, `CanonicalJson`
- `PUT /v1/sessions/{sid}/static-segments` — Static-Epoch-Bump
- Golden-Determinismus: `tests/Ctxman.Tests/Golden/render-anthropic.json`,
  `render-openai.json` + `RenderGoldenTests`

**Cross-cutting**:
- `ITokenCounter` → `HeuristicTokenCounter` (Singleton)
- `ICtxmanAuthorizationHandler` → `AllowAllWithinTenantAuthorizationHandler`
- `Program` ist `public partial` (für `WebApplicationFactory`)

## Noch offen (nicht vorgezogen implementieren)

- **WP4+**: Minor-GC-Worker, Watermarks, Page-Fault (`GET /refs/...`), Pin/Unpin, Events/SSE,
  Blob-Mark-and-Sweep, Major Collection — siehe `docs/forge-work/wp4-prompt.md` ff.

## Test-Patterns

- API-Tests: `CtxmanWebAppFactory` + SQLite in-memory (`SqliteDbContextFactory`)
- Kein Postgres in Tests — Schema provider-neutral halten
- Gezielt: `dotnet test ctxman.sln --filter "FullyQualifiedName~<Klasse>"`
- Volle Suite: `dotnet test ctxman.sln` / forge eval: `pwsh -NoProfile -File .forge/eval.ps1`

## Surfaces / Forbidden (aus `.forge/project.yaml`)

- **Editierbar**: `src/`, `tests/`, `ctxman.sln`
- **Tabu**: `docs/**`, `.forge/**`, `CLAUDE.md`, `README.md`, `nuget.config`, CI-Workflows
- Nur `nuget.org` — keine zusätzlichen Feeds

## Typische Dateien bei neuen Features

| Thema | Wo nachschauen |
|-------|----------------|
| Session/Version/Turn | `src/Ctxman.Core/Domain/Session.cs` |
| Segment/Frame/Units | `Segment.cs`, `Frame.cs`, `Enums.cs` |
| Policy | `PolicyConfig.cs`, `WatermarkState.cs` |
| EF + Tenant-Filter | `Ctxman.Core/Persistence/CtxmanDbContext.cs` |
| Idempotency | `src/Ctxman.Api/Idempotency/IdempotencyService.cs` |
| Render-Pipeline | `src/Ctxman.Core/Rendering/*.cs` |
| API-Endpoint-Muster | bestehende `*Endpoints.cs` im gleichen Stil erweitern |

## Effizienz-Hinweis für Agents

- Nutze dieses Memory + den Plan aus früheren Runs für Layout und Patterns.
- Lies **nur** die Spec-Abschnitte und Akzeptanzkriterien des **aktuellen** Workpakets vollständig.
- Kein erneutes Breit-Scannen von `src/**` oder der ganzen Spec, wenn die relevanten Pfade
  hier oder im Plan bereits genannt sind.
