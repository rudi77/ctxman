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

## Bereits implementiert (WP1–WP5)

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
  Lauf. **Major-Ausführung selbst kam mit WP5** (s. u.); WP4 lieferte die Queue/Watermark-Verdrahtung.
- **Watermarks**: `WatermarkState`/`PolicyConfig`-Erweiterung; in Render- und Session-Response
  exponiert.
- **Page-Fault / Refs**: `GET /v1/sessions/{sid}/refs/{segment_id}` (`RefEndpoints`,
  `RefDtos`, `BlobRef`) expandiert externalisierte Units zurück.
- **Pin/Unpin**: `POST`/`DELETE /v1/sessions/{sid}/segments/{segid}/pin` (`SegmentEndpoints`).
- **Events/SSE**: `GET /v1/sessions/{sid}/events` (`EventEndpoints`, `text/event-stream`).
- **Blob-Mark-and-Sweep**: `BlobSweeper` + `BlobSweepWorker`; `IBlobStore`/`FileSystemBlobStore`
  um Enumerate/`BlobInfo` erweitert.

**Major GC (WP5, PR #4 gemerged)** — `src/Ctxman.{Core,Api}/Gc/`, `Compaction/`, `Promotion/`:
- **Ein** `GcWorker` (Klasse heißt weiter `MinorGcWorker`) dispatcht `GcLevel.Major` →
  `MajorCollection.ExecuteAsync`. Minor + Major teilen den **gemeinsamen** `SessionGcLocks`-
  Singleton (pg_advisory_lock-Äquivalent) — nie zwei parallele Collections auf einer Session.
- **Ablauf** (Spec §3.3): Promotion **vor** Compaction. Step 1 schreibt extrahierte Fakten
  write-only an `IPromotionSink` (`WebhookPromotionSink`), emittiert `fact_promoted`. Step 2
  fasst das `compaction.max_share`-Fenster der non-pinned Working-**Units** (oldest→youngest,
  via `MajorCollector.PlanCompaction`, I/O-frei) zu **einem** `compaction_summary`-Segment auf
  der `seq` des ältesten Quellsegments zusammen; Quellen → `compacted`. Events
  `compaction_started`/`compaction_completed{source_ids,summary_id,tokens_before,tokens_after}`.
- **`ICompactionModel`** (Core) + Adapter `AnthropicCompactionModel`/`AzureOpenAiCompactionModel`
  (Api). Credentials nur via `IOptions<CompactionOptions>` (N5). LLM-Call **außerhalb** der
  Transaktion. Tests gegen `FakeCompactionModel`/`RecordingPromotionSink` — kein Netzwerk.
- **Version-Isolation**: Fenster-IDs zu Planungsbeginn eingefroren; nebenläufig angehängte
  Segmente bleiben unberührt. `compaction{model,prompt_template_id,max_share}` + `promotion{sink}`
  in `PolicyOverridesDto` override-/validierbar (`TryBuildPolicy`).
- **Bekannte Vereinfachungen** (kein Blocker, ggf. Folge-Subtask): nur **ein** kombiniertes
  `fact_promoted` pro Lauf (segment_id = ältestes Quellsegment), nicht pro Fakt; **zwei**
  LLM-Calls pro Major-Lauf (Fact-Extraction + Compaction); `DbUpdateConcurrencyException`-Guard
  ist bis zum Optimistic-Concurrency-Token (WP7) toter Defense-in-depth-Code.

**Cross-cutting**:
- `ITokenCounter` → `HeuristicTokenCounter` (Singleton)
- `ICtxmanAuthorizationHandler` → `AllowAllWithinTenantAuthorizationHandler`
- `Program` ist `public partial` (für `WebApplicationFactory`)

## Noch offen (nicht vorgezogen implementieren)

- **WP6** — Frames: frames-Stack, Frame-Scope-Render, Archivierung (M4). Spec §2.1/§2.5/§3.3/§4.3/§6.
  Baut auf Promotion aus WP5 auf (terminale Promotion bei Frame-Pop / `archive`).
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
| GC (Minor/Major, Queue, Worker, Locks) | `src/Ctxman.Core/Gc/*.cs`, `src/Ctxman.Api/Gc/*.cs` |
| Compaction-LLM / Promotion-Sink | `src/Ctxman.{Core,Api}/Compaction/*.cs`, `.../Promotion/*.cs` |
| Refs/Events/GC-Endpoints | `RefEndpoints.cs`, `EventEndpoints.cs`, `GcEndpoints.cs` |
| Blob-Sweep | `src/Ctxman.Api/Gc/BlobSweeper.cs` + `BlobSweepWorker.cs` |
| API-Endpoint-Muster | bestehende `*Endpoints.cs` im gleichen Stil erweitern |

## Effizienz-Hinweis für Agents

- Nutze dieses Memory + den Plan aus früheren Runs für Layout und Patterns.
- Lies **nur** die Spec-Abschnitte und Akzeptanzkriterien des **aktuellen** Workpakets vollständig.
- Kein erneutes Breit-Scannen von `src/**` oder der ganzen Spec, wenn die relevanten Pfade
  hier oder im Plan bereits genannt sind.
