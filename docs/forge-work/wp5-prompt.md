# WP5 — Major GC: Compaction, Promotion, vollständige Policy (Milestone M3)

Lies zuerst `docs/ctxman-spec.md` vollständig. Für dieses Workpaket maßgeblich sind:
**§3.3 (Major Collection: Promotion vor Compaction), §5 (Policies vollständig — compaction, promotion), §6 (`compaction_started`/`compaction_completed`/`fact_promoted`-Events), §8 (`ICompactionModel`, Advisory-Lock pro Session, N5 Credentials)**.
Implementiere exakt, was dort steht. Baue auf WP1–WP4 auf (insbesondere die Major-Queue/Watermark-Verdrahtung aus WP4).

## Auftrag

1. **`ICompactionModel`** (§8): Interface für das ctxman-eigene Compaction-LLM-Backend (billiges Modell, eigenes Backend — **nie** das Modell des Agents, N1). v1-Adapter: Anthropic Messages API und Azure OpenAI; Credentials ausschließlich über die Standard-.NET-Konfigurationskette (Env/appsettings/Secret-Provider — N5, keine eigene Credential-Logik). Für Tests ein deterministischer Fake/Stub (kein echter Netzwerk-Call).

2. **Major-Collection-Worker** (§3.3, §8): Hosted Service über die in WP4 angelegte Major-Queue, **pro Session serialisiert** (Advisory-Lock). Reihenfolge zwingend: **Promotion vor Compaction** (lossy-Schritt zuletzt). Auslöser: `hard_watermark` (aus WP4) und `POST /gc { level: "major" }` (in WP4 nur eingereiht — hier ausgeführt).

3. **Promotion** (§3.3 Schritt 1, zwingend vor Compaction): Die Promotion-Policy extrahiert dauerhafte Fakten (Entscheidungen, Constraints, gelernte Invarianten) aus dem Compaction-Fenster und schreibt sie über den konfigurierten Memory-Sink hinaus (Webhook-Adapter gemäß `promotion.sink`). Promotion ist ein **Event** (`fact_promoted { segment_id, sink, payload_digest }`), **kein** Segment-State — die Quellsegmente bleiben unverändert und werden anschließend regulär kompaktiert. ctxman schreibt nur in den Sink, liest nie zurück (N2).

4. **Compaction** (§3.3 Schritt 2): Das Compaction-Fenster = alle **nicht gepinnten** Working-Units, von alt nach jung, bis maximal `compaction.max_share` des Working-Budgets abgedeckt ist. Ein LLM-Call (`ICompactionModel`, konfigurierbares `prompt_template_id`) fasst das Fenster zu **einem** `compaction_summary`-Segment zusammen. Das Summary übernimmt die `seq`-Position des **ältesten** kompaktierten Segments (Chronologie bleibt erhalten). Quell-Segmente → `state := compacted` (Soft-Delete, I3). Events `compaction_started` und `compaction_completed { source_ids[], summary_id, tokens_before, tokens_after }`.

5. **Versions-Isolation** (§3.3 Schritt 3): Compaction läuft gegen eine `context_version`; ist die Session beim Commit weitergelaufen, wird **nur der eingefrorene Bereich** kompaktiert, nichts rückwirkend — Konflikte sind damit ausgeschlossen. Gepinnte und Static-Segmente bleiben unangetastet (I1).

6. **Vollständige Policy** (§5): `compaction { model, prompt_template_id, max_share }` und `promotion { sink: { type, url } }` aus der PolicyConfig (WP1) jetzt vollständig wirksam und validiert; Defaults aus §5.

## Out of scope (NICHT implementieren)

- Frames-Push/Pop, `scope=frame`-Render, `archive` + terminale Promotion am Session-Ende (WP6)
- Client-SDKs / Degraded Mode (§9, WP6)
- Auth-Modi `api_key`/`jwt`, Azure-Blob-Adapter als Produktions-Pfad, Prometheus-Metriken, Cold-Storage-Retention (WP7)
- Render-/Minor-GC-Änderungen über die Major-Verdrahtung hinaus (WP3/WP4 bleiben unverändert)

## Verifikation

`dotnet build ctxman.sln` und `dotnet test ctxman.sln` grün (Compaction-Tests gegen den `ICompactionModel`-Fake, kein echter LLM-Call). Akzeptanzkriterien: `docs/forge-work/wp5-acceptance.md`.
