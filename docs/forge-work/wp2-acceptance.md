# WP2 — Akzeptanzkriterien

Maßstab ist `docs/ctxman-spec.md` v0.2 (§4.3/§4.4). Jedes Kriterium muss im Diff nachweisbar erfüllt sein (Code + Test).

## Endpoints (Spec §4.3)

1. `POST /v1/sessions` liefert `201` mit `{ session_id, context_version }`; static_segments aus dem Body landen als Static-Region (`static_epoch = 0`); Policy-Overrides werden validiert und als Snapshot an der Session eingefroren.
2. `GET /v1/sessions/{sid}` liefert Session-Metadaten + `tokens_used` + `watermark_state`; unbekannte Session ⇒ `404`; Session eines anderen Tenants ⇒ `404` (keine Existenz-Leaks).
3. `POST /v1/sessions/{sid}/segments` akzeptiert Einzel- und Batch-Form und liefert `201 { segment_ids[], context_version }`.
4. Append in die Static-Region (`region=Static` bzw. Static-Kinds) ⇒ `409` (I1; Test vorhanden).
5. Inline-`content` > 1 MB ⇒ `413` (Test vorhanden).
6. `POST /v1/sessions/{sid}/blobs` nimmt einen Upload an und liefert `201 { blob_ref }` mit `key = sha256(content)`, tenant-geprefixtem Storage-Pfad und korrekten `size_bytes`/`content_type`. Gleicher Inhalt ⇒ gleicher Key (Dedup; Test vorhanden).
7. Alle Responses/Requests nutzen snake_case-JSON-Feldnamen.

## Idempotenz (Spec §4.4)

8. Segments-Append ohne `Idempotency-Key`-Header ⇒ `400` (Pflicht-Header; Test vorhanden).
9. Zweiter Append mit identischem `Idempotency-Key` ⇒ identische Antwort, kein zweites Segment in der DB (Test vorhanden).
10. Idempotency-Keys werden mit Zeitstempel persistiert; Einträge älter als 24 h werden nicht mehr berücksichtigt/aufgeräumt.

## Concurrency (Spec §4.4)

11. Jede Mutation erhöht `context_version` monoton (Test vorhanden).
12. `If-Match` mit veralteter Version ⇒ `409`, Body enthält die aktuelle Version (Test vorhanden).
13. Segment-`seq` ist serverseitig vergeben und innerhalb der Session strikt monoton, auch bei Batch-Appends.

## Allgemein

14. `tokens` wird beim Append via `ITokenCounter` gesetzt (> 0 für nicht-leeren Content).
15. `tenant_id` stammt ausschließlich aus dem TenantContext — in keinem Request-Body-Schema vorhanden.
16. `dotnet build ctxman.sln` fehlerfrei; `dotnet test ctxman.sln` vollständig grün; WP1-Tests unverändert grün.
17. Kein Out-of-scope-Code (kein render, kein Epoch-Bump, keine Frames, kein GC).
