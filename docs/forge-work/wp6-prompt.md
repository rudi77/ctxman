# WP6 — Frames, Frame-Scope-Render, Archivierung (Milestone M4, Service-Anteil)

Lies zuerst `docs/ctxman-spec.md` vollständig. Für dieses Workpaket maßgeblich sind:
**§2.1 (frames-Stack), §2.5 (Frame-Semantik: push/render/pop, LIFO), §3.3 (Promotion vor Frame-Pop und terminale Promotion bei archive), §4.3 (`POST`/`DELETE /frames`, `render scope`, `archive`), §6 (`frame_pushed`/`frame_popped`-Events)**.
Implementiere exakt, was dort steht. Baue auf WP1–WP5 auf (Render aus WP3, Promotion aus WP5).

## Auftrag

1. **`POST /v1/sessions/{sid}/frames`** (§4.3, §2.5): Body `{ label }` → `201 { frame_id }`. Öffnet einen Frame auf dem Frame-Stack der Session; `parent_frame_id` = oberster offener Frame (oder null = Root). `Idempotency-Key` Pflicht (§4.4).

2. **Frame-Zuordnung beim Append** (§2.5 push): Der Segments-Append (WP2) setzt `frame_id` auf den obersten offenen Frame der Session. Bestehende WP2-Tests bleiben grün (Root-Frame ⇒ `frame_id = null`).

3. **`DELETE /v1/sessions/{sid}/frames/{fid}`** (§4.3, §2.5 pop): Body `{ return_content, return_kind?: "subagent_return" }` → `200 { return_segment_id, context_version }`. `Idempotency-Key` Pflicht.
   - **LIFO**: Ein Frame mit offenen Kind-Frames kann nicht gepoppt werden ⇒ `409` (Kinder zuerst).
   - **Promotion vor Eviction**: Vor der Eviction läuft die Promotion-Policy (WP5) über die Frame-Segmente, damit frame-lokale Entscheidungen nicht verloren gehen.
   - Danach: alle Segmente des Frames → `state := evicted`; der `return_content` wird als ein neues Segment `kind=subagent_return` im **Parent-Frame** angelegt.
   - Events `frame_pushed` und `frame_popped { return_segment_id }`.

4. **Render-Scope** (§2.5, erweitert WP3-Render): Body-Feld `scope?: "path" | "frame"`.
   - `path` (Default): rendert nur Segmente des aktuellen Frame-Pfads (Root + offene Frames).
   - `frame`: isolierte Subagent-Sicht — Static + gepinnte Root-Segmente + Segmente des aktuellen Frames.
   Determinismus-Garantien aus WP3 (I4, kanonische Sortierung, Coalescing) gelten unverändert auch im Frame-Scope.

5. **`POST /v1/sessions/{sid}/archive`** (§4.3): → `204`. Vor der Statusänderung läuft die **terminale Promotion** (WP5-Promotion-Policy) über die verbliebenen Working-Segmente; danach `status := archived`. Anschließend enden alle Live-Referenzen der Session (Sweep-Job WP4 räumt nach `blob_grace`). Cold-Storage-Export bzw. Retention-Härtung gemäß §7.1 ist WP7 — hier genügt der Statusübergang + terminale Promotion + Beenden der Live-Referenzen.

## Out of scope (NICHT implementieren)

- **Client-SDKs** (Python `ctxman-client`, C# `Ctxman.Client`, ToolsetManager, Degraded Mode — §9): separate Pakete/Repos, nicht Teil dieser Service-WP-Reihe.
- Auth-Modi `api_key`/`jwt`, `ICtxmanAuthorizationHandler`-PDP-Verdrahtung (WP7)
- Azure-Blob-Adapter, Prometheus-Metriken, Cold-Storage-/Retention-Härtung der Archivierung (WP7)
- Änderungen an Minor/Major-GC über die Frame-Promotion-Anbindung hinaus (WP4/WP5 bleiben unverändert)

## Verifikation

`dotnet build ctxman.sln` und `dotnet test ctxman.sln` grün. API-Tests via `WebApplicationFactory<Program>`. Akzeptanzkriterien: `docs/forge-work/wp6-acceptance.md`.
