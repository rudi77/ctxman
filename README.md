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

Aktuell umgesetzt (Milestone **M1 — Core Store**, Workpakete WP1–WP3):

| Bereich | Status |
|---------|--------|
| Domänenmodell (Session, Segment, Frame, Policy) | ✅ |
| EF Core + Postgres (Npgsql), Tenant-Isolation | ✅ |
| Auth-Modus `none` + Tenant-Resolution-Pipeline | ✅ |
| `POST/GET /v1/sessions` | ✅ |
| `POST /v1/sessions/{sid}/segments` (Single + Batch) | ✅ |
| `POST /v1/sessions/{sid}/blobs` (Filesystem-Store) | ✅ |
| Idempotenz + Optimistic Concurrency (`If-Match`) | ✅ |
| `POST /v1/sessions/{sid}/render` (Anthropic + OpenAI) | ✅ |
| `PUT /v1/sessions/{sid}/static-segments` (Epoch-Bump) | ✅ |
| Render-Determinismus + Golden-File-Tests | ✅ |
| Event-Outbox (`render_served`, `static_epoch_bumped`) | ✅ |

Noch nicht implementiert (geplante Milestones M2–M5):

- Garbage Collection (Minor/Major), `GET /refs`, `POST /gc`
- Frames (Push/Pop), Pin-Endpunkte
- Events-HTTP / SSE, Archivierung
- Auth-Modi `api_key` und `jwt`
- Client-SDKs (Python, C#)

129 Tests, alle grün (`dotnet test ctxman.sln`).

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
# Repository klonen und ins Verzeichnis wechseln
cd ctxman

# Build + Tests
dotnet build ctxman.sln
dotnet test ctxman.sln

# API starten (benötigt laufende Postgres-Instanz)
dotnet run --project src/Ctxman.Api
```

Standard-URL: `http://localhost:5000` (oder der in `launchSettings.json` konfigurierte Port).

Health-Check:

```powershell
curl http://localhost:5000/healthz
# {"status":"ok","auth_mode":"none"}
```

> **Hinweis:** Beim Start wird das DB-Schema nicht automatisch angelegt (`Program.cs` ruft kein `Migrate`/`EnsureCreated` auf). Für den lokalen Betrieb muss Postgres erreichbar sein und das Schema bereitstehen (EF-Migrations folgen in einem späteren Workpaket).

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
| `auth.mode` | `none` (Default), später `api_key` / `jwt` |
| `auth.tenant_header` | Header für Tenant-Auflösung im Modus `none` |
| `auth.default_tenant` | Fallback-Tenant ohne Header |
| `ConnectionStrings:ctxman` | Postgres-Verbindung (Npgsql) |
| `blobstore:Root` | Wurzelverzeichnis für den Filesystem-Blob-Store (Dev) |

JSON-Wire-Format: **snake_case** Property-Namen.

---

## API-Überblick (implementiert)

Basis-Pfad: `/v1`. Alle Ressourcen sind tenant-gescoped.

| Methode | Pfad | Beschreibung |
|---------|------|--------------|
| `GET` | `/healthz` | Status + Auth-Modus |
| `POST` | `/v1/sessions` | Session anlegen (inkl. initialer Static-Region) |
| `GET` | `/v1/sessions/{sid}` | Session-Metadaten + Budget-Status |
| `POST` | `/v1/sessions/{sid}/segments` | Working-Segmente anhängen (Idempotency-Key **Pflicht**) |
| `POST` | `/v1/sessions/{sid}/blobs` | Blob hochladen (Content-Addressed, SHA-256) |
| `POST` | `/v1/sessions/{sid}/render` | Context rendern → Provider-Request-Fragment |
| `PUT` | `/v1/sessions/{sid}/static-segments` | Static-Region ersetzen (Epoch-Bump) |

### Beispiel: Session anlegen und rendern

```powershell
# Session erstellen
curl -X POST http://localhost:5000/v1/sessions `
  -H "Content-Type: application/json" `
  -H "X-Tenant-Id: my-tenant" `
  -d '{"static_segments":[{"kind":"system_prompt","role":"system","content":"You are helpful."}]}'

# User-Nachricht anhängen
curl -X POST http://localhost:5000/v1/sessions/{session_id}/segments `
  -H "Content-Type: application/json" `
  -H "Idempotency-Key: append-001" `
  -d '{"kind":"user_msg","role":"user","content":"Hello"}'

# Render für Anthropic (Turn-Advance + Idempotency-Key)
curl -X POST http://localhost:5000/v1/sessions/{session_id}/render `
  -H "Content-Type: application/json" `
  -H "Idempotency-Key: render-turn-001" `
  -d '{"provider":"anthropic"}'
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

Im Default-Modus `none` gibt es keine Authentifizierung. Jeder Request wird über den `X-Tenant-Id`-Header (oder `default_tenant`) einem Tenant zugeordnet. Beim Start loggt der Service eine Warnung — **nicht für Produktion ohne Gateway geeignet**.

Die Tenant-Pipeline ist von Anfang an vollständig verdrahtet; ein Upgrade auf `api_key` oder `jwt` ist eine reine Konfigurationsänderung ohne Datenmigration (Implementierung folgt in M5).

Autorisierung über den Erweiterungspunkt `ICtxmanAuthorizationHandler` — Default: alles innerhalb des Tenants erlaubt.

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

Geplante Milestones (Spec §12):

1. **M1 — Core Store** ← *aktuell (WP1–WP3 abgeschlossen)*
2. **M2 — Minor GC:** Externalisierung, TTL-Eviction, `expand_context_ref`, Watermarks
3. **M3 — Major GC:** Compaction-Worker, Promotion
4. **M4 — Frames & SDKs:** Subagent-Frames, Python/C#-Clients
5. **M5 — Härtung:** Auth-Modi, Azure-Blob, Metriken, Archivierung

---

## Lizenz

Noch nicht festgelegt.
