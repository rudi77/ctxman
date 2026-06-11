# WP7 — Härtung: Auth (api_key/jwt), Autorisierung, Azure-Blob, Metriken, Retention (Milestone M5)

Lies zuerst `docs/ctxman-spec.md` vollständig. Für dieses Workpaket maßgeblich sind:
**§4.1 (Auth-Modi `api_key`/`jwt`, `ICtxmanAuthorizationHandler`), §6 (Prometheus-Metriken), §7 (Azure-Blob-Adapter), §7.1 (Retention/Cold-Storage), §8 (Security-Pipeline `TenantResolution → Authentication → Authorization`), §10 (Sicherheit)**.
Implementiere exakt, was dort steht. Baue auf WP1–WP6 auf — die Auth-Struktur ist seit WP1 als reine Konfigurations-/Registrierungserweiterung angelegt.

## Auftrag

1. **Auth-Modus `api_key`** (§4.1): statischer Key über `Authorization: Bearer <key>` **oder** `X-Api-Key`. Konfiguration mappt Key → Tenant. Authentication-Stufe in der Pipeline (`TenantResolution → Authentication → Authorization`, §8); unbekannter/fehlender Key ⇒ `401`. Der aufgelöste Tenant kommt weiterhin ausschließlich aus dem Mapping, nie aus dem Body (§10).

2. **Auth-Modus `jwt`** (§4.1): OIDC/JWT-Validierung (Issuer, Audience, Signatur) über die Standard-`JwtBearer`-Middleware. Tenant aus einem konfigurierbaren Claim (Default `tenant_id`). Ungültiges/abgelaufenes Token ⇒ `401`. Konfigurationsschema additiv zu `auth` aus WP1 (`auth.jwt: { authority/issuer, audience, tenant_claim }`).

3. **Autorisierung** (§4.1, §8): `ICtxmanAuthorizationHandler` (Interface seit WP1) als echte Pipeline-Stufe **nach** der Authentication verdrahten. Pro Endpunkt eine `ResourceAction` setzen; `AuthzDecision`-Deny ⇒ `403`. Default-Handler erlaubt alles innerhalb des aufgelösten Tenants; ein externer PDP ist per DI einhängbar, ohne dass ctxman dessen Protokoll kennt. In allen Modi unverändertes Verhalten von Idempotenz/Concurrency/Events (Sicherheit orthogonal zur Korrektheit).

4. **Azure-Blob-`IBlobStore`-Adapter** (§7): produktiver Adapter neben dem fs-Adapter (WP2). Content-addressed (sha256), immutable, Tenant-Key-Prefix `{tenant_id}/…`. Per Konfiguration auswählbar (`fs` | `azure-blob`); Credentials über die Standard-.NET-Kette (N5). Der Mark-and-Sweep aus WP4 bleibt adapter-agnostisch (Source of Truth = `segments`-Tabelle).

5. **Prometheus-Metriken** (§6): Tokens pro Session / Watermark-Verteilung, Compaction-Ratio (`tokens_before/after`), Page-Fault-Rate, Eviction-nach-Expansion-Rate, Epoch-Bump-Rate pro Session, Render-Latenz p50/p99. Über `/metrics` exponiert.

6. **Retention / Cold-Storage** (§7.1): die Retention-Config (`evicted_blob_retention_days`, `archived_session_blobs: delete | cold_storage`, `sweep_interval`) im Sweep-Job (WP4) und im Archive-Pfad (WP6) wirksam machen: bei `cold_storage` werden Blobs archivierter Sessions exportiert statt gelöscht; `blob_swept`-Events tragen den korrekten `reason`.

## Out of scope (NICHT implementieren)

- Population-Based Search, Bandit/Bayesian-Optimierung, Streaming-Render (SSE für große Contexts) — v2+/offene Punkte (§11, §12)
- Änderungen am Domänenmodell, Render-Determinismus oder der GC-Logik (WP1–WP6 bleiben unverändert; hier nur Auth/Storage/Observability/Retention)
- Client-SDKs (§9)

## Verifikation

`dotnet build ctxman.sln` und `dotnet test ctxman.sln` grün. Auth-Tests via `WebApplicationFactory<Program>` mit konfiguriertem Modus (`api_key`/`jwt`); Azure-Blob-Adapter gegen ein Test-Double/Emulator, nicht gegen echtes Azure. Akzeptanzkriterien: `docs/forge-work/wp7-acceptance.md`.
