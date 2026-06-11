# WP4 — Minor GC, Externalisierung, Watermarks, Page-Fault, Events (Milestone M2)

Lies zuerst `docs/ctxman-spec.md` vollständig. Für dieses Workpaket maßgeblich sind:
**§2.4 (Units), §3 (Turn-Definition aus dem Intro, §3.1 Watermarks, §3.2 Minor Collection, §3.4 Page Fault), §4.3 (`GET /refs`, `POST /gc`, `GET /events` inkl. SSE, pin/unpin), §6 (GC-/Render-Events), §7.1 (Mark-and-Sweep), §8 (Channels + Hosted Services, Advisory Locks pro Session)**.
Implementiere exakt, was dort steht. Baue auf WP1–WP3 auf.

## Auftrag

1. **Minor-Collection-GC-Worker** (§3.2, §8): Hosted Service über `System.Threading.Channels`, **pro Session serialisiert** (kein paralleler Lauf auf derselben Session — Advisory-Lock `pg_advisory_lock(session_id)` bzw. SQLite-äquivalente Serialisierung im Test). Reihenfolge strikt billig→teuer, lossless→lossy:
   - **Clean-Page-Eviction**: `refetchable`-Segmente jenseits ihrer Kind-TTL → `state := evicted`, **ohne** Blob-Write; `origin` bleibt im Audit-Trail.
   - **Externalisierung**: nicht-refetchable Segmente mit `tokens > externalize_threshold_tokens` und Kind-Eignung → Inhalt via `IBlobStore.Put` (fs-Adapter aus WP2), `content := null`, `summary := first_n_chars + Strukturhinweis`, `state := externalized`.
   - **TTL-Eviction**: Units mit `current_turn − last_referenced_turn > ttl_turns`, weder pinned noch Static → `state := evicted`.
   GC operiert auf **Units** (§2.4), nie auf gekoppelten Einzel-Segmenten: Externalisierung eines `tool_result` lässt den `tool_call` live und ersetzt das Result durch `summary` + Ref; Eviction der Unit entfernt beide.

2. **Watermarks** (§3.1): `render` (WP3) berechnet das Budget vor jeder Antwort und meldet `watermark_state`. Verdrahte die Auslöser:
   - `soft` (Default 0.60·B): asynchrone Minor Collection nach dem Turn einreihen.
   - `hard` (Default 0.80·B): asynchrone (priorisierte) Major Collection **einreihen** — die Major-Ausführung selbst ist WP5; hier nur Queue-Marker + `watermark_crossed`-Event.
   - `emergency` (Default 0.95·B): **synchrone** Notfall-Minor-Collection **innerhalb** von `render`, beschränkt auf Operationen **ohne I/O-Seiteneffekte** (Clean-Page- und TTL-Eviction; **keine** Externalisierung, **keine** LLM-Calls). Reicht das nicht unter Budget ⇒ `render` antwortet `413 Budget Exceeded` (retrybar).

3. **Page Fault** (§3.4, §4.3): `GET /v1/sessions/{sid}/refs/{segment_id}` → `200 { content, content_type }`; Seiteneffekt `last_referenced_turn := current_turn` des Ursprungssegments. Ist das Segment nicht mehr live (`evicted`/`compacted`) oder der Blob bereits gesweept ⇒ `410 Gone` mit `{ summary, origin? }` als Restinformation (§7.1). Die `expand_context_ref`-Tool-Definition liefert bereits WP3 im Render-Output; das Anhängen des `ref_expansion`-Segments ist SDK-Sache (out of scope).

4. **Manueller GC-Trigger** (§4.3): `POST /v1/sessions/{sid}/gc` Body `{ level: "minor" | "major" }` → `202 { job_id }`. `minor` startet den Minor-Worker; `major` wird angenommen und eingereiht, die Ausführung folgt in WP5.

5. **Pin/Unpin** (§4.3): `POST` / `DELETE /v1/sessions/{sid}/segments/{segid}/pin` → `204`. Gepinnte Working-Segmente sind für den GC unantastbar (weder evicted noch externalized noch compacted). Static-Segmente pinnen ⇒ `409` (I1 sinngemäß: Static ist ohnehin GC-immun).

6. **Blob-Mark-and-Sweep** (§7.1): Hosted Service (Default täglich, pro Tenant, Advisory-Lock-geschützt). *Mark*: Keys mit ≥ 1 Live-Referenz (`state = externalized`) ermitteln. *Sweep*: Tenant-Blobs ohne Live-Referenz **und** älter als `blob_grace` löschen. *Orphan-Sweep*: Blobs ohne zugehörige Segment-Zeile nach derselben Grace-Regel. Jede Löschung emittiert `blob_swept { key, size_bytes, reason: unreferenced | orphan | session_deleted }`. Retention-Config aus §7.1 (`blob_grace_hours`, `sweep_interval`, …) auf die PolicyConfig anwenden.

7. **Events-Endpoint** (§4.3, §6): `GET /v1/sessions/{sid}/events?after_seq=…` → `200 { events[] }` (tenant-gefiltert, aus der `events`-Outbox); zusätzlich SSE-Variante (`text/event-stream`). Alle GC-Operationen schreiben ihre Events in die Outbox: `segment_externalized`, `segment_evicted`, `unit_evicted`, `ref_expanded { segment_id }`, `blob_swept`, `watermark_crossed { level }`.

## Out of scope (NICHT implementieren)

- Compaction, Promotion, Major-Collection-**Ausführung**, `ICompactionModel`, Promotion-Sink (WP5)
- Frames-Endpoints, `scope=frame`-Render, `archive` (WP6)
- Auth-Modi `api_key`/`jwt`, Azure-Blob-Adapter, Prometheus-Metriken, Cold-Storage-Retention (WP7)
- Client-SDK-Logik (Page-Fault-Append, Degraded Mode, ToolsetManager — §9)

## Verifikation

`dotnet build ctxman.sln` und `dotnet test ctxman.sln` grün. API-Tests via `WebApplicationFactory<Program>`. Akzeptanzkriterien: `docs/forge-work/wp4-acceptance.md`.
