# WP5 — Akzeptanzkriterien

Maßstab ist `docs/ctxman-spec.md` v0.2 (§3.3, §5, §6, §8). Jedes Kriterium muss im Diff nachweisbar erfüllt sein (Code + Test).

## Compaction-Backend (Spec §8)

1. `ICompactionModel` existiert als Interface; v1-Adapter (Anthropic / Azure OpenAI) beziehen Credentials ausschließlich über die Standard-.NET-Konfigurationskette (keine eigene Credential-Logik, N5).
2. Tests laufen gegen einen deterministischen `ICompactionModel`-Fake — kein echter Netzwerk-/LLM-Call in der Suite.

## Major-Worker & Reihenfolge (Spec §3.3)

3. Der Major-Worker läuft als Hosted Service über die Major-Queue, pro Session serialisiert (Advisory-Lock); zwei Major-Läufe auf derselben Session überschneiden sich nicht (Test vorhanden).
4. Promotion läuft **vor** Compaction (Test belegt die Reihenfolge anhand der Event-Sequenz).
5. `hard_watermark` und `POST /gc { level: "major" }` lösen den Major-Worker aus (Test vorhanden).

## Promotion (Spec §3.3 Schritt 1)

6. Promotion schreibt extrahierte Fakten an den konfigurierten `promotion.sink` (Webhook-Adapter) und emittiert `fact_promoted { segment_id, sink, payload_digest }`; die Quellsegmente bleiben unverändert (kein State-Wechsel durch Promotion; Test vorhanden).
7. ctxman liest nie aus dem Sink zurück (N2) — nur Schreibpfad vorhanden.

## Compaction (Spec §3.3 Schritt 2)

8. Das Compaction-Fenster umfasst nur nicht-gepinnte Working-Units, von alt nach jung, bis maximal `compaction.max_share` des Working-Budgets (Test vorhanden).
9. Das Ergebnis ist **ein** `compaction_summary`-Segment, das die `seq`-Position des ältesten kompaktierten Segments übernimmt; Quell-Segmente werden `compacted` (Soft-Delete, in DB auffindbar; Test vorhanden).
10. `compaction_started` und `compaction_completed { source_ids[], summary_id, tokens_before, tokens_after }` werden emittiert (Test vorhanden).
11. `compacted`-Segmente erscheinen nie im Render-Output (I3; Test vorhanden).

## Versions-Isolation (Spec §3.3 Schritt 3)

12. Läuft die Session während der Compaction weiter, wird nur der eingefrorene `context_version`-Bereich kompaktiert; neue Segmente bleiben unberührt, kein Konflikt (Test vorhanden).
13. Gepinnte und Static-Segmente werden von Compaction nie verändert (I1; Test vorhanden).

## Policy (Spec §5)

14. `compaction { model, prompt_template_id, max_share }` und `promotion { sink }` sind vollständig wirksam und werden beim Anlegen validiert; Defaults aus §5.

## Allgemein

15. `dotnet build ctxman.sln` fehlerfrei; `dotnet test ctxman.sln` vollständig grün; WP1–WP4-Tests unverändert grün.
16. Kein Out-of-scope-Code (keine Frames, kein `archive`, kein `api_key`/`jwt`, keine SDKs).
