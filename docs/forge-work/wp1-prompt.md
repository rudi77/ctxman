# WP1 — Domänenmodell, Persistenz, Tenant-Pipeline (Milestone M1a)

Lies zuerst `docs/ctxman-spec.md` vollständig. Für dieses Workpaket maßgeblich sind:
**§1 (Zweck), §2 (Domänenmodell, Invarianten I1–I5), §4.1 (Auth-Modi/Tenant-Auflösung), §7 (Persistenz), §8 (C#-Implementierung)**.
Implementiere exakt, was dort steht — keine Abweichungen ohne Not. Konventionen stehen in `CLAUDE.md`.

## Auftrag

Lege das Fundament des ctxman-Service:

1. **Domänenmodell in `Ctxman.Core`** (Spec §2):
   - `Session` (§2.1): id (ULID), tenant_id, agent_template_id?, policy (Snapshot), context_version (long), static_epoch (int), current_turn (int), status (active|archived), created_at/updated_at.
   - `Segment` (§2.2): alle dort gelisteten Felder inkl. region (Static|Working), kind (offener String), source, role, content, blob_ref, refetchable, origin, summary, tool_call_id, frame_id, pinned, created_turn, last_referenced_turn, tokens, seq (long), state (live|externalized|compacted|evicted).
   - `Frame` (§2.5): id, session_id, parent_frame_id, label, opened_turn, status (open|popped).
   - `BlobRef` (§2.6): store, key (sha256-content-addressed), size_bytes, content_type.
   - `PolicyConfig` (§5): budget_tokens, watermarks (soft/hard/emergency), externalize_threshold_tokens, tokenizer, kinds-Map (ttl_turns, externalize, refetchable, promote), compaction (model, prompt_template_id, max_share), promotion (sink), retention (§7.1) — mit den Spec-Defaults. Unendliche TTL (`∞` in der Spec) als nullable/Sentinel abbilden.
   - Invarianten I1–I3 als Domänenlogik durchsetzbar machen (z. B. Methoden/Guards, die ungültige Zustandsübergänge ablehnen). I4/I5 sind Render-Garantien — hier nur die nötigen Datengrundlagen (seq, tool_call_id), keine Render-Implementierung.

2. **Persistenz** (Spec §7, §8): EF Core DbContext mit Tabellen `sessions`, `segments`, `frames`, `events` (Outbox, append-only), `idempotency_keys`. Npgsql-Provider für Produktion konfiguriert, aber Tests laufen gegen SQLite/InMemory (siehe CLAUDE.md Teststrategie — Schema provider-neutral halten). Alle Entitäten tragen `tenant_id`; jede Query MUSS tenant-gefiltert sein (Global Query Filter pro TenantContext oder äquivalentes Muster).

3. **Tenant-Resolution-Pipeline** in `Ctxman.Api` (Spec §4.1, §8): Middleware, die jeden Request zu einem `TenantContext` auflöst, **bevor** Handler laufen. Auth-Modus v1: `none` — Tenant aus `X-Tenant-Id`-Header (Header-Name konfigurierbar via `auth.tenant_header`), sonst `default_tenant` (Default "default"). Konfigurationsschema `auth: { mode, tenant_header, default_tenant }` anlegen; Modi `api_key`/`jwt` NICHT implementieren, aber die Struktur so, dass sie reine Konfigurations-/Registrierungserweiterung sind. Beim Start im Modus `none` eine deutliche Warnung loggen und den Modus in `/healthz`-Metadaten exponieren (§4.1 Betriebshinweise).
   `ICtxmanAuthorizationHandler`-Interface (§4.1, wörtlich aus der Spec) mit Default-Implementierung "alles erlaubt innerhalb des aufgelösten Tenants", per DI austauschbar.

4. **Tests** in `Ctxman.Tests`: Domänen-Invarianten (I1–I3), Tenant-Resolution (Header → Tenant, kein Header → default, Header-Name konfigurierbar), DbContext-Roundtrip für alle Entitäten, Tenant-Isolation (Daten von Tenant A sind für Tenant B unsichtbar).

## Out of scope (spätere Workpakete — NICHT implementieren)

- HTTP-Endpoints für Sessions/Segments (WP2)
- Render/Provider-Adapter/Tokenizer (WP3)
- GC, Watermarks, Blob-Store-Adapter, expand_context_ref (WP4)
- Compaction/Promotion (WP5), Frame-Push/Pop-Verhalten (WP6), api_key/jwt (WP7)

## Verifikation

`dotnet build ctxman.sln` und `dotnet test ctxman.sln` müssen grün sein. Die Akzeptanzkriterien stehen in `docs/forge-work/wp1-acceptance.md`.
