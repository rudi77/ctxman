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

## Bereits implementiert (WP1–WP4)

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

**GC / Lifecycle (WP4, PR #3 gemerged)** — `src/Ctxman.Api/Gc/`, `src/Ctxman.Core/Gc/`:
- **Minor GC** (TTL-Eviction/Externalisierung im Hot Path): `MinorCollector`, `MinorGcWorker`
  (BackgroundService), `MinorGcJob`, `UnitGrouping`, `IGcJobQueue`/`ChannelGcJobQueue`,
  `GcLevel` (`minor` | `major`). `POST /v1/sessions/{sid}/gc` (`GcEndpoints`) enqueued einen
  Lauf — `major`-Ausführung selbst kommt erst mit WP5, die Queue/Watermark-Verdrahtung steht.
- **Watermarks**: `WatermarkState`/`PolicyConfig`-Erweiterung; in Render- und Session-Response
  exponiert.
- **Page-Fault / Refs**: `GET /v1/sessions/{sid}/refs/{segment_id}` (`RefEndpoints`,
  `RefDtos`, `BlobRef`) expandiert externalisierte Units zurück.
- **Pin/Unpin**: `POST`/`DELETE /v1/sessions/{sid}/segments/{segid}/pin` (`SegmentEndpoints`).
- **Events/SSE**: `GET /v1/sessions/{sid}/events` (`EventEndpoints`, `text/event-stream`).
- **Blob-Mark-and-Sweep**: `BlobSweeper` + `BlobSweepWorker`; `IBlobStore`/`FileSystemBlobStore`
  um Enumerate/`BlobInfo` erweitert.

**Cross-cutting**:
- `ITokenCounter` → `HeuristicTokenCounter` (Singleton)
- `ICtxmanAuthorizationHandler` → `AllowAllWithinTenantAuthorizationHandler`
- `Program` ist `public partial` (für `WebApplicationFactory`)

## Noch offen (nicht vorgezogen implementieren)

- **WP5** — Major GC: Compaction, Promotion, vollständige Policy (M3). Spec §3.3/§5/§6/§8,
  `ICompactionModel`, Advisory-Lock pro Session. Baut auf der Major-Queue/Watermark aus WP4 auf.
- **WP6** — Frames: frames-Stack, Frame-Scope-Render, Archivierung (M4). Spec §2.1/§2.5/§3.3/§4.3/§6.
- **WP7** — Härtung: Auth (`api_key`/`jwt`), Autorisierung, Azure-Blob, Prometheus-Metriken,
  Retention/Cold-Storage (M5). Spec §4.1/§6/§7/§7.1/§8/§10.
- Siehe jeweils `docs/forge-work/wp5-prompt.md` ff. und `wpN-acceptance.md`.

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
| GC (Minor/Major, Queue, Worker) | `src/Ctxman.Core/Gc/*.cs`, `src/Ctxman.Api/Gc/*.cs` |
| Refs/Events/GC-Endpoints | `RefEndpoints.cs`, `EventEndpoints.cs`, `GcEndpoints.cs` |
| Blob-Sweep | `src/Ctxman.Api/Gc/BlobSweeper.cs` + `BlobSweepWorker.cs` |
| API-Endpoint-Muster | bestehende `*Endpoints.cs` im gleichen Stil erweitern |

## Effizienz-Hinweis für Agents

- Nutze dieses Memory + den Plan aus früheren Runs für Layout und Patterns.
- Lies **nur** die Spec-Abschnitte und Akzeptanzkriterien des **aktuellen** Workpakets vollständig.
- Kein erneutes Breit-Scannen von `src/**` oder der ganzen Spec, wenn die relevanten Pfade
  hier oder im Plan bereits genannt sind.
