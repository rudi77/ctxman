# WP2 — Sessions-, Segments- und Blob-Endpoints, Idempotenz, Concurrency (Milestone M1b)

Lies zuerst `docs/ctxman-spec.md` vollständig. Für dieses Workpaket maßgeblich sind:
**§4 (API-Basis, §4.1 Tenant, §4.3 Endpunkte, §4.4 Idempotenz und Konsistenz), §2 (Invarianten I1, I2), §7 (idempotency_keys)**.
Implementiere exakt, was dort steht. Baue auf dem WP1-Stand auf (Domänenmodell, DbContext, Tenant-Pipeline).

## Auftrag

REST-Endpoints unter Basis-Pfad `/v1` (Minimal APIs, snake_case-JSON):

1. **`POST /v1/sessions`** (§4.3): Body `{ agent_template_id?, policy_overrides?, static_segments[] }` → `201 { session_id, context_version }`. Die Static-Region wird hier initial gesetzt (Segmente mit `region=Static`, `static_epoch=0`); Policy-Overrides werden über die Defaults gelegt und als Snapshot eingefroren (§5 Validierung beim Anlegen).
2. **`GET /v1/sessions/{sid}`**: Session-Metadaten + Budget-Status (`tokens_used`, `watermark_state`). Für `tokens_used` reicht in diesem WP die Summe der `tokens` aller render-eligiblen Segmente; `watermark_state` aus den Policy-Watermarks abgeleitet.
3. **`POST /v1/sessions/{sid}/segments`** (§4.3): Einzel- oder Batch-Append (`{ kind, role, content | blob_ref, tool_call_id?, pinned?, source? }` bzw. `{ segments: [...] }`) → `201 { segment_ids[], context_version }`. Fehlerfälle exakt nach Spec: `409` bei If-Match-Versionskonflikt, `409` bei Schreibversuch in die Static-Region (I1), `413` bei Inline-Content > 1 MB (Hinweis auf Upload-Pfad). `tokens` wird beim Append gezählt — in diesem WP über eine einfache, austauschbare `ITokenCounter`-Heuristik (z. B. chars/4, konservativ); das Interface ist §8-Vorgabe, die exakte Zählung kommt mit den Provider-Adaptern in WP3. `seq` wird serverseitig monoton pro Session vergeben.
4. **`POST /v1/sessions/{sid}/blobs`** (§4.3): Streaming-Upload → `201 { blob_ref }`. Content-addressed (sha256-Key, tenant-geprefixt `{tenant_id}/…`, §2.6/§10). In diesem WP genügt ein Filesystem-`IBlobStore`-Adapter (Interface `IBlobStore { Put, Get, Exists }` aus §7) mit konfigurierbarem Root-Verzeichnis.
5. **Idempotenz** (§4.4): `Idempotency-Key`-Header ist **Pflicht** auf mutierenden Endpunkten (Segments-Append; Sessions-Create nimmt ihn optional an). Wiederholter Key ⇒ identische gespeicherte Antwort, kein Doppel-Append. Persistiert in `idempotency_keys` mit 24-h-Aufbewahrung (Aufräumen darf ein einfacher Hosted Service oder Lazy-Cleanup sein).
6. **Optimistic Concurrency** (§4.4): `context_version` erhöht sich bei jeder Mutation; optionaler `If-Match`-Header; Konflikt ⇒ `409` mit aktueller Version im Body.

## Out of scope (NICHT implementieren)

- `render`, Provider-Adapter, `cache_breakpoints`, Epoch-Bump `PUT /static-segments` (WP3)
- GC/Watermark-Aktionen, `GET /refs`, `POST /gc` (WP4); Events-Endpoint (WP4)
- Frames-Endpoints (WP6), `archive` (WP6), Auth-Modi api_key/jwt (WP7)

## Verifikation

`dotnet build ctxman.sln` und `dotnet test ctxman.sln` grün. API-Tests via `WebApplicationFactory<Program>`. Akzeptanzkriterien: `docs/forge-work/wp2-acceptance.md`.
