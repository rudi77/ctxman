# CLAUDE.md — ctxman

Read the following file for additional important information:
- [CLAUDE_BEHAVIORAL.md](CLAUDE_BEHAVIORAL.md)


> Architektur- und Konventions-Notizen für alle Agents, die an ctxman arbeiten.
> **Die Spec ist Vertrag:** `docs/ctxman-spec.md` (v0.2) definiert verbindlich, was gebaut wird.
> Bei jedem Task zuerst die für das Workpaket maßgeblichen Spec-Abschnitte lesen und exakt
> umsetzen. Abweichungen von der Spec sind keine Designfreiheit, sondern ein Fehler —
> wenn die Spec unklar oder widersprüchlich erscheint, STOP und an den Operator melden,
> nicht improvisieren.

## Was ctxman ist

Ein eigenständiger, wiederverwendbarer Stateful-Service (C#/.NET 9), der den LLM-Context
von Agents als first-class Ressource verwaltet (Spec §1). Mentales Modell: Speicherverwaltung —
Static-Region (Stack), Working Set (Heap), Garbage Collector (Externalisierung, Eviction,
Compaction, Promotion). ctxman ruft **nie** selbst das LLM des Agents auf (Non-Goal N1).

## Projektlayout

```
ctxman.sln
nuget.config              # nur nuget.org — globale private Feeds sind hier nicht erreichbar
src/
  Ctxman.Core/            # Domänenmodell + Logik: Session, Segment, Frame, BlobRef, Units,
                          # Policies, GC, Render-Pipeline, Provider-Adapter-Interfaces.
                          # KEINE ASP.NET-Abhängigkeit, keine EF-Abhängigkeit nach Möglichkeit
                          # (Persistenz-Interfaces hier, EF-Implementierung in Api oder
                          # späterem Ctxman.Infrastructure-Projekt).
  Ctxman.Api/             # ASP.NET Core Minimal APIs (/v1/...), Middleware-Pipeline
                          # (TenantResolution → Authentication → Authorization),
                          # EF Core + Npgsql, Hosted Services (GC-Worker).
tests/
  Ctxman.Tests/           # xUnit. Unit-Tests gegen Core; API-Tests via WebApplicationFactory.
docs/
  ctxman-spec.md          # DIE Spezifikation. Read-only für Agents.
  forge-work/             # Workpaket-Prompts + Akzeptanzkriterien. Read-only für Agents.
```

Neue Projekte (z. B. `Ctxman.Infrastructure`) sind erlaubt, wenn ein Workpaket es verlangt —
immer unter `src/` bzw. `tests/`, immer zur `ctxman.sln` hinzufügen.

## Konventionen

- **.NET 9**, `net9.0`, Nullable enabled, ImplicitUsings enabled.
- **Minimal APIs**, keine Controller. Endpoints gruppiert pro Ressource
  (z. B. `SessionEndpoints.cs` mit `MapSessionEndpoints(this IEndpointRouteBuilder)`).
- **EF Core + Npgsql** für Postgres (Spec §7/§8). Alle Queries tenant-gefiltert —
  `tenant_id` kommt ausschließlich aus dem `TenantContext` der Middleware, nie aus dem Body.
- **ULIDs** für Session-/Segment-/Frame-IDs: NuGet-Package `Ulid` (Cysharp).
- **Records/sealed classes** für Domänenmodelle; Enums als C#-`enum` mit expliziter
  String-Serialisierung im Wire-Format (snake_case wie in der Spec).
- JSON-Wire-Format: snake_case Property-Namen (Spec-API), `System.Text.Json` mit
  `JsonNamingPolicy.SnakeCaseLower`.
- Interfaces aus der Spec heißen wörtlich wie dort: `IProviderAdapter`, `ITokenCounter`,
  `IBlobStore`, `ICompactionModel`, `ICtxmanAuthorizationHandler`.
- Konfiguration über Standard-.NET-Kette (appsettings + Env). Keine eigene Credential-Logik (N5).
- Kommentare nur, wo das WHY nicht offensichtlich ist; Spec-Verweise als `// Spec §x.y` sind
  erwünscht bei Invarianten (I1–I5).

## Teststrategie

- **Unit-Tests** gegen `Ctxman.Core` ohne I/O.
- **API-/Integrationstests** via `WebApplicationFactory<Program>` (Program ist `public partial`).
- **Kein Postgres-Zwang in Tests:** EF Core mit SQLite in-memory oder InMemory-Provider als
  Test-Double; das DB-Schema muss provider-neutral genug bleiben. Echte Npgsql-Integration
  wird manuell/später in CI mit Container getestet — nicht Teil der Eval-Gates.
- **Determinismus-Tests** (Render-Prefix, I4) als Golden-File-Tests: kanonisches JSON in
  `tests/Ctxman.Tests/Golden/` einchecken, byte-genau vergleichen.
- Verifikation lokal immer: `dotnet build ctxman.sln` und `dotnet test ctxman.sln`.
  Beide müssen grün sein, bevor ein Subtask als fertig gilt.

## Grenzen für Agents (zusätzlich zu .forge/project.yaml)

- `docs/**`, `CLAUDE.md`, `README.md`, `.forge/**`, `.github/workflows/**` sind tabu.
- Tests sind der Vertrag: bestehende Tests nicht umdeuten/löschen, um grün zu werden.
- Keine zusätzlichen NuGet-Feeds; nur nuget.org (siehe `nuget.config`).
- Scope-Disziplin: genau das Workpaket umsetzen, keine vorgezogenen Features aus
  späteren Milestones (Spec §12).
