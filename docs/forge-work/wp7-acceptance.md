# WP7 — Akzeptanzkriterien

Maßstab ist `docs/ctxman-spec.md` v0.2 (§4.1, §6, §7, §7.1, §8, §10). Jedes Kriterium muss im Diff nachweisbar erfüllt sein (Code + Test).

## Auth-Modus api_key (Spec §4.1)

1. Modus `api_key`: gültiger Key (`Authorization: Bearer` **oder** `X-Api-Key`) wird zum konfigurierten Tenant aufgelöst; fehlender/unbekannter Key ⇒ `401` (Tests vorhanden).
2. Der Tenant kommt aus dem Key→Tenant-Mapping, nie aus dem Body; Tenant-Isolation gilt unverändert (Test mit zwei Keys/Tenants).

## Auth-Modus jwt (Spec §4.1)

3. Modus `jwt`: gültiges Token (Issuer/Audience/Signatur) wird akzeptiert, Tenant aus dem konfigurierbaren Claim (Default `tenant_id`); ungültiges/abgelaufenes Token ⇒ `401` (Tests vorhanden).
4. Der Wechsel `none → api_key → jwt` ist reine Konfiguration — keine Datenmigration, restlicher Code identisch (durch bestehende WP1–WP6-Tests in allen Modi belegt).

## Autorisierung (Spec §4.1, §8)

5. `ICtxmanAuthorizationHandler` ist als Pipeline-Stufe nach der Authentication verdrahtet; Default-Handler erlaubt alles innerhalb des Tenants; ein per DI eingehängter Deny-Handler liefert `403` (Test vorhanden).
6. Pipeline-Reihenfolge `TenantResolution → Authentication → Authorization` ist eingehalten; Idempotenz/Concurrency/Events verhalten sich in allen Modi identisch.

## Azure-Blob-Adapter (Spec §7)

7. Ein Azure-Blob-`IBlobStore`-Adapter existiert neben dem fs-Adapter; per Konfiguration auswählbar; content-addressed (sha256), Tenant-Key-Prefix `{tenant_id}/…`; Credentials über die Standard-Konfigurationskette (Test gegen Test-Double/Emulator).
8. Der Mark-and-Sweep (WP4) funktioniert mit beiden Adaptern identisch (Source of Truth = `segments`-Tabelle).

## Metriken (Spec §6)

9. `/metrics` exponiert die Spec-Metriken: Token-/Watermark-Verteilung, Compaction-Ratio, Page-Fault-Rate, Eviction-nach-Expansion-Rate, Epoch-Bump-Rate, Render-Latenz p50/p99 (Test belegt, dass die Counter/Histogramme registriert sind und sich bei den jeweiligen Operationen ändern).

## Retention / Cold-Storage (Spec §7.1)

10. Die Retention-Config (`evicted_blob_retention_days`, `archived_session_blobs`, `sweep_interval`) ist im Sweep-Job und im Archive-Pfad wirksam; bei `cold_storage` werden Blobs archivierter Sessions exportiert statt gelöscht; `blob_swept`-Events tragen den korrekten `reason` (Tests vorhanden).

## Allgemein

11. `dotnet build ctxman.sln` fehlerfrei; `dotnet test ctxman.sln` vollständig grün; WP1–WP6-Tests unverändert grün.
12. Kein Eingriff in Domänenmodell, Render-Determinismus oder GC-Logik über die Auth-/Storage-/Observability-/Retention-Verdrahtung hinaus; keine Client-SDKs.
