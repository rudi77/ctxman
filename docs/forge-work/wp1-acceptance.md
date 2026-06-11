# WP1 — Akzeptanzkriterien

Maßstab ist `docs/ctxman-spec.md` v0.2. Jedes Kriterium muss im Diff nachweisbar erfüllt sein (Code + Test).

## Domänenmodell (Spec §2)

1. `Session`, `Segment`, `Frame`, `BlobRef` existieren in `Ctxman.Core` mit **allen** in §2.1/§2.2/§2.5/§2.6 gelisteten Feldern; IDs sind ULIDs; Enums (region, role, state, status) entsprechen den Spec-Werten und serialisieren im Wire-Format als snake_case-Strings.
2. `PolicyConfig` bildet §5 ab (watermarks-Defaults 0.60/0.80/0.95, externalize_threshold 2000, kinds-Map mit ttl_turns/externalize/refetchable/promote, compaction.max_share, promotion.sink, retention aus §7.1) und ist als Snapshot an der Session gespeichert.
3. Invariante **I1**: Ein Update/Eviction/Externalisierung/Compaction-Versuch auf einem Static-Segment wird von der Domänenlogik abgelehnt (Test vorhanden).
4. Invariante **I2**: Ein Segment mit `state = live` oder `externalized` kann nicht mit `content == null && blob_ref == null` konstruiert/gespeichert werden (Test vorhanden).
5. Invariante **I3**: `evicted`/`compacted`-Segmente sind Soft-Deletes — sie bleiben in der DB erhalten (Test: nach Eviction noch per Query auffindbar, aber als nicht-live markiert).

## Persistenz (Spec §7)

6. EF-Core-DbContext mit Tabellen `sessions`, `segments`, `frames`, `events`, `idempotency_keys`; alle mit `tenant_id`-Spalte.
7. Tenant-Isolation auf Query-Ebene: Daten von Tenant A sind über den regulären Datenzugriffsweg für Tenant B nicht sichtbar (Test mit zwei Tenants vorhanden).
8. Tests laufen ohne Postgres (SQLite oder InMemory); Npgsql ist für Produktion konfiguriert.

## Tenant-Pipeline (Spec §4.1)

9. Middleware löst **jeden** Request zu einem `TenantContext` auf, bevor Handler laufen; `tenant_id` kommt nie aus dem Request-Body.
10. Modus `none`: `X-Tenant-Id`-Header (Name via `auth.tenant_header` konfigurierbar) bestimmt den Tenant; ohne Header gilt `auth.default_tenant` (Default "default"). Beides durch Tests belegt.
11. Beim Start in Modus `none` wird eine Warnung geloggt; `/healthz` exponiert den Auth-Modus in den Metadaten.
12. `ICtxmanAuthorizationHandler` existiert mit der Signatur aus §4.1 (`ValueTask<AuthzDecision> AuthorizeAsync(TenantContext, ClaimsPrincipal?, ResourceAction, CancellationToken)`); Default-Implementierung erlaubt alles innerhalb des Tenants; per DI ersetzbar.

## Allgemein

13. `dotnet build ctxman.sln` fehlerfrei; `dotnet test ctxman.sln` vollständig grün.
14. Kein Out-of-scope-Code (keine Session/Segment-HTTP-Endpoints, kein Render, kein GC).
15. `Ctxman.Core` referenziert kein ASP.NET Core; Wire-/HTTP-Belange liegen in `Ctxman.Api`.
