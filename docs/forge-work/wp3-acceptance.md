# WP3 — Akzeptanzkriterien

Maßstab ist `docs/ctxman-spec.md` v0.2 (§4.2, §4.3 render, §4.6, I4/I5). Jedes Kriterium muss im Diff nachweisbar erfüllt sein (Code + Test).

## Render-Endpunkt (Spec §4.3, §4.4)

1. `POST /v1/sessions/{sid}/render` liefert `200` mit allen Spec-Feldern: `request_fragment`, `cache_breakpoints[]`, `builtin_tools[]`, `context_version`, `tokens_total`, `watermark_state`.
2. Anthropic-`request_fragment` hat die Form `{ system, tools[], messages[] }`; OpenAI-Form `{ tools[], messages[] }` mit System-Prompt als erster Message. Tool-Defs sind in keinem Adapter Teil der Message-Liste.
3. `turn_advance=true` (Default) erhöht `current_turn` um genau 1; Retry mit identischem `Idempotency-Key` zählt den Turn nicht doppelt (Test vorhanden). Ohne Idempotency-Key bei `turn_advance` ⇒ `400`.
4. Unvollständige Unit (tool_call ohne tool_result) ⇒ `422` mit offenen tool_call-IDs im Body (I5; Test vorhanden).
5. Unbekannter Provider ⇒ `400` mit Liste der registrierten Adapter (Test vorhanden).
6. `builtin_tools` enthält `expand_context_ref` mit `segment_id`-Parameter im jeweiligen Provider-Schema (beide Adapter; Test vorhanden).

## Determinismus (Spec §4.6, I4)

7. Render-Reihenfolge: Static vor Working; Static kanonisch nach `(source, kind, content_hash)` — nachweislich unabhängig von der Insertion-Order (Test: zwei Sessions mit gleichen Static-Segmenten in verschiedener Reihenfolge ⇒ byte-identischer Prefix).
8. Working strikt nach `seq`; gepinnte Segmente an chronologischer Position (kein separater Block).
9. Zwei `render`-Aufrufe auf identischem Segment-Stand liefern byte-identischen Output (Golden-File-Test, beide Adapter, eingecheckt unter `tests/Ctxman.Tests/Golden/`).
10. Coalescing: benachbarte Working-Segmente gleicher Rolle ⇒ eine Message mit mehreren Content-Blocks (Test mit evictions-bedingter Nachbarschaft simuliert durch Segment-Stand).
11. Externalisierte Segmente erscheinen als `summary` + Ref-Hinweis; `evicted`/`compacted` erscheinen nie (I3) (Test vorhanden).
12. `cache_breakpoints` markieren beim Anthropic-Adapter mindestens das Ende der Static-Region; beim OpenAI-Adapter leer.

## Epoch-Bump (Spec §4.2)

13. `PUT /v1/sessions/{sid}/static-segments` ersetzt die Static-Region vollständig, erhöht `static_epoch` um 1 und liefert den Tool-/Source-Diff gemäß Spec-Schema.
14. `If-Match`-Konflikt ⇒ `409`; wiederholter `Idempotency-Key` ⇒ identische Antwort ohne zweiten Bump.
15. `on_tool_removed`-Default `externalize`: Working-Units entfernter Tools werden externalisiert (summary + ref); `keep` und `evict` sind konfigurierbar und getestet.
16. Nach Toggle-Off/On derselben Quelle ist der Static-Prefix byte-identisch mit dem Stand davor; `static_epoch` ist dennoch monoton weitergezählt; `cache_prefix_hash` fällt auf den früheren Wert zurück (Test vorhanden).
17. Events `static_epoch_bumped { old_epoch, new_epoch, tokens_delta, diff }` und `render_served { context_version, static_epoch, tokens_total, cache_prefix_hash }` werden in die `events`-Outbox geschrieben (Test vorhanden).

## Allgemein

18. `dotnet build ctxman.sln` fehlerfrei; `dotnet test ctxman.sln` vollständig grün; WP1/WP2-Tests unverändert grün.
19. Kein Out-of-scope-Code (keine GC-Ausführung, kein `GET /refs`, keine Frames).
