# WP6 — Akzeptanzkriterien

Maßstab ist `docs/ctxman-spec.md` v0.2 (§2.5, §3.3, §4.3, §6). Jedes Kriterium muss im Diff nachweisbar erfüllt sein (Code + Test).

## Frame-Push (Spec §2.5, §4.3)

1. `POST /v1/sessions/{sid}/frames { label }` ⇒ `201 { frame_id }`; der Frame landet auf dem Frame-Stack, `parent_frame_id` = oberster offener Frame oder null (Test vorhanden). `Idempotency-Key` Pflicht ⇒ ohne Header `400`.
2. Ein Segments-Append bei offenem Frame setzt `frame_id` auf den obersten offenen Frame; ohne offenen Frame `frame_id = null` (Test vorhanden; WP2-Tests bleiben grün).

## Frame-Pop (Spec §2.5, §3.3)

3. `DELETE /v1/sessions/{sid}/frames/{fid} { return_content }` ⇒ `200 { return_segment_id, context_version }`; alle Frame-Segmente werden `evicted`, der Return wird als `subagent_return` im Parent-Frame angelegt (Test vorhanden).
4. Frame mit offenen Kind-Frames ⇒ `409` (LIFO; Test vorhanden).
5. Vor der Eviction läuft die Promotion-Policy über die Frame-Segmente (Test belegt `fact_promoted` vor `frame_popped` bei promotion-fähigen Kinds).
6. Events `frame_pushed` und `frame_popped { return_segment_id }` werden emittiert (Test vorhanden).

## Render-Scope (Spec §2.5)

7. `render` mit `scope=path` (Default) rendert Root + offene Frames des aktuellen Pfads (Test vorhanden).
8. `render` mit `scope=frame` rendert Static + gepinnte Root-Segmente + Segmente des aktuellen Frames (isolierte Sicht; Test vorhanden).
9. Determinismus-Garantien aus WP3 (kanonische Sortierung, byte-identischer Prefix, Coalescing) gelten unverändert im Frame-Scope (Golden-/Determinismus-Test vorhanden).

## Archivierung (Spec §4.3, §3.3)

10. `POST /v1/sessions/{sid}/archive` ⇒ `204`; vorher läuft die terminale Promotion über die verbliebenen Working-Segmente, danach `status := archived` (Test vorhanden).
11. Nach der Archivierung enden die Live-Referenzen der Session (vom Sweep-Job aus WP4 nach `blob_grace` aufräumbar); kein Cold-Storage-Export in diesem WP.

## Allgemein

12. `dotnet build ctxman.sln` fehlerfrei; `dotnet test ctxman.sln` vollständig grün; WP1–WP5-Tests unverändert grün.
13. Kein Out-of-scope-Code (keine Client-SDKs, kein `api_key`/`jwt`, kein Azure-Blob-Adapter, keine Prometheus-Metriken).
