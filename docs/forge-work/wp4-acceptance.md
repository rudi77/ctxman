# WP4 — Akzeptanzkriterien

Maßstab ist `docs/ctxman-spec.md` v0.2 (§2.4, §3.1, §3.2, §3.4, §4.3, §6, §7.1). Jedes Kriterium muss im Diff nachweisbar erfüllt sein (Code + Test).

## Minor Collection (Spec §3.2)

1. Der Minor-Worker läuft als Hosted Service über Channels und ist pro Session serialisiert (kein paralleler Lauf auf derselben Session; Test belegt Serialisierung).
2. Clean-Page-Eviction: `refetchable`-Segmente jenseits der Kind-TTL werden `evicted` **ohne** Blob-Write; `origin` bleibt erhalten (Test vorhanden).
3. Externalisierung: nicht-refetchable Segmente mit `tokens > externalize_threshold_tokens` werden in den fs-BlobStore geschrieben, `content := null`, `summary` gesetzt, `state := externalized` (Test vorhanden).
4. TTL-Eviction: Unit mit `current_turn − last_referenced_turn > ttl_turns` (weder pinned noch Static) wird `evicted`; gepinnte und Static-Segmente nie (Test vorhanden).
5. GC operiert auf Units: Externalisierung eines `tool_result` lässt den `tool_call` live (summary + ref); Eviction der Unit entfernt beide (Test vorhanden).

## Watermarks & Emergency (Spec §3.1)

6. Überschreiten des `soft`-Watermarks reiht eine asynchrone Minor Collection nach dem Turn ein; `watermark_crossed { level }` wird emittiert (Test vorhanden).
7. Überschreiten des `hard`-Watermarks reiht eine Major Collection ein (Marker/Event); die Ausführung ist WP5 (kein LLM-Call in diesem WP).
8. `emergency`-Watermark löst synchron innerhalb von `render` nur Clean-Page-/TTL-Eviction aus — kein Blob-Write, kein LLM-Call (Test vorhanden).
9. Bleibt der Context nach Emergency-Eviction über Budget ⇒ `render` liefert `413` (retrybar; Test vorhanden).

## Page Fault / refs (Spec §3.4, §7.1)

10. `GET /v1/sessions/{sid}/refs/{segment_id}` liefert `200 { content, content_type }` für ein externalisiertes Segment und setzt `last_referenced_turn := current_turn` (Test vorhanden).
11. Nicht mehr lives Segment (`evicted`/`compacted`) oder gesweepter Blob ⇒ `410 { summary, origin? }` (Test vorhanden).

## GC-Trigger & Pin (Spec §4.3)

12. `POST /v1/sessions/{sid}/gc { level: "minor" }` ⇒ `202 { job_id }` und startet den Minor-Worker; `{ level: "major" }` ⇒ `202` mit eingereihtem Job (Ausführung WP5).
13. `POST`/`DELETE /v1/sessions/{sid}/segments/{segid}/pin` ⇒ `204`; gepinnte Segmente werden vom GC nie angefasst (Test vorhanden). Pin auf Static ⇒ `409`.

## Blob-Sweep (Spec §7.1)

14. Der Sweep-Job markiert Keys mit ≥ 1 Live-Referenz und löscht nur Tenant-Blobs ohne Live-Referenz **und** älter als `blob_grace` (Test vorhanden).
15. Orphan-Blobs (kein zugehöriges Segment) werden nach derselben Grace-Regel gesweept; jede Löschung emittiert `blob_swept { key, size_bytes, reason }` (Test vorhanden).

## Events (Spec §4.3, §6)

16. `GET /v1/sessions/{sid}/events?after_seq=…` liefert die tenant-gefilterte Event-Liste aus der Outbox; eine SSE-Variante existiert.
17. GC-Operationen schreiben `segment_externalized`, `segment_evicted`, `unit_evicted`, `ref_expanded`, `blob_swept`, `watermark_crossed` in die Outbox (Tests vorhanden).

## Allgemein

18. `dotnet build ctxman.sln` fehlerfrei; `dotnet test ctxman.sln` vollständig grün; WP1–WP3-Tests unverändert grün.
19. Kein Out-of-scope-Code (keine Compaction/Promotion-Ausführung, keine Frames, keine `archive`, kein `api_key`/`jwt`).
