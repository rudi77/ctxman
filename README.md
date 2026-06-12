# ctxman

**Context Management Service** — ein eigenständiger, wiederverwendbarer Stateful-Service (C# / .NET 9), der den LLM-Context von Agents als first-class Ressource verwaltet.

ctxman beantwortet genau eine Frage: **Was sieht das Modell in diesem Turn?** Der Agent führt den LLM-Call selbst aus; ctxman liefert per `render` das fertige Request-Fragment (System, Tools, Messages) im Format des jeweiligen Provider-Adapters. ctxman ist **kein** LLM-Gateway und ruft das Modell des Agents nie auf.

Die verbindliche Spezifikation ist [`docs/ctxman-spec.md`](docs/ctxman-spec.md) (v0.2).

---

## Mentales Modell

Der Context wird wie Speicherverwaltung behandelt:

| Region | Analogie | Inhalt | Verhalten |
|--------|----------|--------|-----------|
| **Static** | Stack | System Prompt, Tool-Definitionen, Skill-Index | Cache-stabil, kanonisch sortiert; Änderungen nur über Epoch-Bump |
| **Working** | Heap | Conversation, Tool-Results, Skills | Dynamisch; GC (Eviction, Externalisierung, Compaction) hält das Budget ein |

Wichtige Konzepte:

- **Segmente** sind die kanonische Repräsentation — die Message-Liste ist ein Render-Artefakt, nie Source of Truth.
- **Units** koppeln `tool_call` und `tool_result`; unvollständige Units blockieren `render`.
- **Static-Epochen** versionieren Tool-/Skill-Änderungen zur Laufzeit (z. B. MCP-Server ein/aus) und invalidieren Provider-Caches bewusst.
- **Multi-Tenancy** ist intern immer aktiv; der Tenant wird pro Request aufgelöst, nie aus dem Request-Body gelesen.

---

## Implementierungsstand

**Spec v0.2 ist vollständig implementiert** (Workpakete WP1–WP7, Milestones M1a–M5). Alle Server-seitigen Features der Spezifikation sind live und durch Tests abgedeckt.

| Bereich | Status |
|---------|--------|
| Domänenmodell (Session, Segment, Frame, Policy) | ✅ WP1 |
| EF Core + Postgres (Npgsql), Tenant-Isolation, Idempotenz, Optimistic Concurrency | ✅ WP1–WP2 |
| Sessions / Segments / Blobs (Filesystem-Store) | ✅ WP1–WP2 |
| `render` (Anthropic + OpenAI), Static-Epoch-Bump, Render-Determinismus (Golden-Files) | ✅ WP3 |
| **Minor GC** — TTL-Eviction, Externalisierung, Watermarks, Page-Fault (`GET /refs`), Pin/Unpin, Events/SSE, Blob-Mark-and-Sweep | ✅ WP4 |
| **Major GC** — Compaction + Promotion (`ICompactionModel`, write-only `IPromotionSink`), Version-Isolation | ✅ WP5 |
| **Frames** — Push/Pop (LIFO), Frame-Scope-Render, Session-`archive` mit terminaler Promotion | ✅ WP6 |
| **Härtung** — Auth `api_key`/`jwt`, Autorisierungs-Pipeline, Azure-Blob-Adapter, Cold-Storage/Retention, Prometheus-Metriken | ✅ WP7 |

**273 Tests, alle grün** (`dotnet test ctxman.sln`).

Bewusst **nicht** Teil dieses Service (Non-Goals der Spec):

- Client-SDKs (Python / C#) — geplant, aber außerhalb v0.2.
- LLM-Gateway-Funktionalität — ctxman ruft das Agent-Modell nie auf (N1).

---

## Projektstruktur

```
ctxman.sln
src/
  Ctxman.Core/     Domänenmodell, Render-Pipeline, Provider-Adapter, Persistenz-Interfaces
  Ctxman.Api/      ASP.NET Core Minimal APIs, Middleware, Blob-Storage
tests/
  Ctxman.Tests/    Unit- + Integrationstests (WebApplicationFactory, SQLite in-memory)
docs/
  ctxman-spec.md   Verbindliche Spezifikation
```

---

## Voraussetzungen

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- **PostgreSQL** zum lokalen Betrieb der API (Tests laufen ohne Postgres — SQLite in-memory)

---

## Schnellstart

```powershell
cd ctxman

# Build + Tests (Tests brauchen KEIN Postgres — SQLite in-memory)
dotnet build ctxman.sln
dotnet test ctxman.sln

# Postgres bereitstellen (passt zum Default-Connection-String)
docker run -d --name ctxman-pg -e POSTGRES_USER=ctxman -e POSTGRES_PASSWORD=ctxman `
  -e POSTGRES_DB=ctxman -p 5432:5432 postgres:16

# Schema anlegen (siehe Hinweis unten), dann API starten
dotnet run --project src/Ctxman.Api
```

Standard-URL: **`http://localhost:5291`** (HTTP) bzw. `https://localhost:7182` (siehe `launchSettings.json`).

Health-Check:

```powershell
curl http://localhost:5291/healthz
# {"status":"ok","auth_mode":"none"}
```

> **⚠️ DB-Schema:** Beim Start wird das Schema **nicht** automatisch angelegt (`Program.cs` ruft bewusst kein `Migrate`/`EnsureCreated` — die Tests verwalten das Schema selbst). Gegen ein frisches Postgres müssen die Tabellen vorab existieren. Aktuell sind **keine EF-Migrations** eingecheckt; das Schema lässt sich code-first per `dotnet ef migrations add Initial --project src/Ctxman.Api` + `dotnet ef database update` erzeugen, oder für lokale Dev-Zwecke über einen einmaligen `EnsureCreated()`-Aufruf.

---

## Konfiguration

Einstellungen über `appsettings.json` und Umgebungsvariablen:

```json
{
  "auth": {
    "mode": "none",
    "tenant_header": "X-Tenant-Id",
    "default_tenant": "default"
  },
  "ConnectionStrings": {
    "ctxman": "Host=localhost;Database=ctxman;Username=ctxman;Password=ctxman"
  },
  "blobstore": {
    "Root": "C:/temp/ctxman-blobs"
  }
}
```

| Sektion | Beschreibung |
|---------|--------------|
| `auth.mode` | `none` (Default) / `api_key` / `jwt` |
| `auth.tenant_header` | Header für Tenant-Auflösung im Modus `none` |
| `auth.default_tenant` | Fallback-Tenant ohne Header |
| `ConnectionStrings:ctxman` | Postgres-Verbindung (Npgsql) |
| `blobstore:provider` | `fs` (Default, Filesystem) / `azure-blob` |
| `blobstore:Root` | Wurzelverzeichnis des Filesystem-Blob-Stores (Dev) |
| `blobstore:ConnectionString` / `ContainerName` | Azure-Blob-Adapter (Provider `azure-blob`) |
| `compaction:<provider>:api_key` u.a. | LLM-Credentials für **Major GC** (Compaction/Promotion). Nur nötig, wenn `POST /gc {major}` genutzt wird — Render/Minor-GC brauchen keinen LLM-Call. |

JSON-Wire-Format: **snake_case** Property-Namen. Credentials kommen ausschließlich aus der Standard-.NET-Konfigurationskette (Non-Goal N5 — keine eigene Credential-Logik).

---

## API-Überblick

Basis-Pfad: `/v1`. Alle Ressourcen sind tenant-gescoped (`X-Tenant-Id` im Modus `none`).

| Methode | Pfad | Beschreibung |
|---------|------|--------------|
| `GET` | `/healthz` | Status + Auth-Modus |
| `GET` | `/metrics` | Prometheus-Scrape-Endpoint |
| `POST` | `/v1/sessions` | Session anlegen (inkl. initialer Static-Region) |
| `GET` | `/v1/sessions/{sid}` | Session-Metadaten + Budget-/Watermark-Status |
| `POST` | `/v1/sessions/{sid}/segments` | Working-Segmente anhängen (single + batch) |
| `POST` | `/v1/sessions/{sid}/blobs` | Blob hochladen (Content-Addressed, SHA-256) |
| `POST` | `/v1/sessions/{sid}/render` | Context rendern → Provider-Request-Fragment (`scope=path\|frame`) |
| `PUT` | `/v1/sessions/{sid}/static-segments` | Static-Region ersetzen (Epoch-Bump) |
| `POST` | `/v1/sessions/{sid}/gc` | GC triggern (`minor` \| `major`) → `202 { job_id }` |
| `GET` | `/v1/sessions/{sid}/refs/{segment_id}` | Page-Fault: externalisierte Unit lazy expandieren |
| `POST`/`DELETE` | `/v1/sessions/{sid}/segments/{segid}/pin` | Segment pinnen / entpinnen |
| `POST`/`DELETE` | `/v1/sessions/{sid}/frames[/{fid}]` | Frame push / pop (LIFO) |
| `GET` | `/v1/sessions/{sid}/events?after_seq=…` | Event-Outbox (Pull + SSE) |
| `POST` | `/v1/sessions/{sid}/archive` | Session archivieren (terminale Promotion → `archived`) |

### Beispiel: Session anlegen, anhängen, rendern

```powershell
$h = @{ "X-Tenant-Id" = "my-tenant"; "Content-Type" = "application/json" }

# Session erstellen (System-Prompt im Static-Bereich)
$sid = (Invoke-RestMethod -Method Post -Uri http://localhost:5291/v1/sessions -Headers $h `
  -Body '{"static_segments":[{"kind":"system_prompt","content":"You are helpful."}]}').session_id

# User-Nachricht anhängen
Invoke-RestMethod -Method Post -Uri "http://localhost:5291/v1/sessions/$sid/segments" -Headers $h `
  -Body '{"kind":"message","role":"user","content":"Hallo"}'

# Render für Anthropic (Idempotency-Key bei turn_advance Pflicht)
Invoke-RestMethod -Method Post -Uri "http://localhost:5291/v1/sessions/$sid/render" `
  -Headers ($h + @{"Idempotency-Key"="render-001"}) `
  -Body '{"provider":"anthropic","scope":"path","turn_advance":false}'
```

Antwort von `render`:

```json
{
  "request_fragment": { "system": "...", "tools": [], "messages": [...] },
  "cache_breakpoints": [...],
  "builtin_tools": [...],
  "context_version": 2,
  "tokens_total": 42,
  "watermark_state": "ok"
}
```

Registrierte Provider: **`anthropic`**, **`openai`**. Unbekannte Provider → `400` mit Liste der verfügbaren Adapter.

---

## Authentifizierung

Drei Modi über `auth.mode`:

- **`none`** (Default) — keine Authentifizierung; der Tenant kommt aus `X-Tenant-Id` (oder `default_tenant`). Beim Start loggt der Service eine Warnung — **nicht für Produktion ohne vorgelagertes Gateway**.
- **`api_key`** / **`jwt`** — vollständig implementiert (WP7). Der Wechsel ist eine reine Konfigurationsänderung ohne Datenmigration, weil die Security-Pipeline (`TenantResolution → Authentication → Authorization`) von Anfang an verdrahtet ist.

Autorisierung über den Erweiterungspunkt `ICtxmanAuthorizationHandler` — Default `AllowAllWithinTenantAuthorizationHandler`: alles innerhalb des aufgelösten Tenants erlaubt.

---

## Entwicklung

```powershell
# Nur Core-Unit-Tests
dotnet test ctxman.sln --filter "FullyQualifiedName~Ctxman.Tests.Domain"

# Golden-File-Tests (Render-Determinismus)
dotnet test ctxman.sln --filter "FullyQualifiedName~RenderGoldenTests"
```

Konventionen und Architektur-Notizen für Agents: [`CLAUDE.md`](CLAUDE.md).

---

## Spezifikation & Roadmap

| Dokument | Inhalt |
|----------|--------|
| [`docs/ctxman-spec.md`](docs/ctxman-spec.md) | Verbindliche Spec v0.2 — Domänenmodell, API, GC, Events |
| [`CLAUDE.md`](CLAUDE.md) | Projekt-Konventionen, Teststrategie, Grenzen |

Milestones (Spec §12) — **alle abgeschlossen** (WP1–WP7):

1. ✅ **M1 — Core Store:** Domänenmodell, Persistenz, Sessions/Segments/Blobs, Render
2. ✅ **M2 — Minor GC:** Externalisierung, TTL-Eviction, `GET /refs`, Watermarks
3. ✅ **M3 — Major GC:** Compaction-Worker, Promotion
4. ✅ **M4 — Frames:** Subagent-Frames, Frame-Scope-Render, Archivierung
5. ✅ **M5 — Härtung:** Auth-Modi, Azure-Blob, Metriken, Retention/Cold-Storage

Offen jenseits v0.2: Client-SDKs (Python / C#).

---

## Lizenz

Noch nicht festgelegt.
