# WP3 — Render, Provider-Adapter, Determinismus, Epoch-Bump (Milestone M1c)

Lies zuerst `docs/ctxman-spec.md` vollständig. Für dieses Workpaket maßgeblich sind:
**§2 (I4, I5, Units §2.4), §3 (nur Turn-Definition aus dem Intro), §4.2 (Static-Epochen), §4.3 (`render`-Endpunkt), §4.4 (Idempotenz bei turn_advance), §4.6 (Render-Determinismus und Provider-Adapter), §6 (nur `render_served`/`static_epoch_bumped`-Events)**.
Implementiere exakt, was dort steht. Baue auf WP1+WP2 auf.

## Auftrag

1. **`IProviderAdapter`** (§4.6, §8): zustandslose Adapter-Registry per DI; Verantwortung wie in §4.6 gelistet (Rollen/Kind-Mapping, System-Prompt-/Tool-Platzierung, `expand_context_ref` im Provider-Schema, Cache-Breakpoint-Empfehlungen, Tokenizer-Zuordnung). v1-Adapter: **Anthropic Messages API** und **OpenAI Chat Completions**. Unbekannter `provider`-String ⇒ `400` mit Liste der registrierten Adapter.

2. **`POST /v1/sessions/{sid}/render`** (§4.3): Body `{ provider, scope?, turn_advance? (default true) }` → `200 { request_fragment, cache_breakpoints[], builtin_tools[], context_version, tokens_total, watermark_state }`.
   - `turn_advance=true` erhöht `current_turn` atomar; `Idempotency-Key` ist dabei Pflicht — ein Retry darf den Turn nicht doppelt zählen (§4.4).
   - **I5**: existiert eine unvollständige Unit (tool_call ohne tool_result), antworte `422` mit der Liste der offenen tool_call-IDs.
   - `builtin_tools` enthält die `expand_context_ref(segment_id)`-Definition im Provider-Format (§3.4) — nur die Tool-Definition; der `GET /refs`-Endpunkt selbst ist WP4.
   - `watermark_state` aus Policy-Watermarks; die GC-Aktionen selbst sind WP4 (hier nur Zustand berechnen und melden, kein 413-Pfad nötig).

3. **Render-Determinismus** (§4.6, I4):
   - Reihenfolge strikt `Static → Working`; Static kanonisch nach `(source, kind, content_hash)` sortiert, Working nach `seq`; gepinnte Segmente bleiben an chronologischer Position; kein Reordering.
   - Nur render-eligible Segmente (`state = live | externalized`; externalisierte als `summary` + Ref-Hinweis gemäß §2.4-Regel).
   - Kanonische JSON-Serialisierung: sortierte Keys, keine Timestamps, definierte Whitespace-Behandlung, stabile Float-Formatierung — byte-identische Prefixes für identischen Segment-Stand.
   - **Coalescing-Regel**: benachbarte Working-Segmente gleicher Rolle werden zu einer Message mit mehreren Content-Blocks zusammengefasst (Teil der Render-Garantie, beide Adapter).
   - `cache_breakpoints` markieren mindestens das Ende der Static-Region beim Anthropic-Adapter; beim OpenAI-Adapter leer.
   - `cache_prefix_hash` (Hash des gerenderten Static-Prefix) berechnen und im `render_served`-Event mitführen (§6).

4. **`PUT /v1/sessions/{sid}/static-segments`** (§4.2): vollständige Ersetzung der Static-Region mit `Idempotency-Key` + `If-Match`; Antwort `200 { static_epoch, context_version, diff: { added_tools[], removed_tools[], added_sources[], removed_sources[] } }`; Diff über `source`/Tool-Namen der `tool_def`-Segmente. Policy-Regel `on_tool_removed: keep | externalize (Default) | evict` auf betroffene Working-Units anwenden — für `externalize` genügt in diesem WP der Zustandsübergang + summary-Ersetzung über den vorhandenen fs-BlobStore. Direkte Static-Writes über den Segments-Endpunkt bleiben `409` (I1). Events `static_epoch_bumped` und `render_served` in die `events`-Outbox schreiben.

5. **Golden-File-Tests** (§12 M1): eingecheckte Golden-Files unter `tests/Ctxman.Tests/Golden/` mit byte-genauem Vergleich des Render-Outputs für beide Adapter; Test, dass identischer Segment-Stand denselben `cache_prefix_hash` ergibt; Test, dass dieselbe Toolset-Kombination nach Toggle-Off/On wieder byte-identischen Static-Prefix erzeugt (§4.2 Content-Addressing).

## Out of scope (NICHT implementieren)

- GC-Ausführung (Eviction/Externalisierung/TTL), Emergency-413-Pfad, `GET /refs`, `POST /gc` (WP4)
- Events-HTTP-Endpoint/SSE (WP4), Compaction/Promotion (WP5), Frames (WP6), api_key/jwt (WP7)
- Exakte Provider-Tokenizer: die WP2-`ITokenCounter`-Heuristik bleibt; nur die Adapter-Zuordnung des Counters vorsehen (§4.6)

## Verifikation

`dotnet build ctxman.sln` und `dotnet test ctxman.sln` grün. Akzeptanzkriterien: `docs/forge-work/wp3-acceptance.md`.
