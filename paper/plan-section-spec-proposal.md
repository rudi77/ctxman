# Spec-Erweiterung (Entwurf): Plan als first-class Working-Konstrukt

> **Status:** Vorschlag / Diskussionsgrundlage — NICHT Teil der Spec.
> `docs/ctxman-spec.md` ist Vertrag; dieser Entwurf ist gegen die Spec formuliert,
> damit er als Revision übernommen oder verworfen werden kann.
> Ziel: einen vom Agent abzuarbeitenden **Plan** (geordnete Instruktionssequenz mit
> aktuellem Stand) als erstklassiges Konzept modellieren — ohne dritte Region,
> ohne Static, GC-sicher, mit Render-Cursor.

## 0. Designentscheidungen (begründet)

1. **Keine dritte Region.** Regionen kodieren eine Allocation-/Caching-*Disziplin*
   (Static = immutable + gecacht; Working = GC). Ein Plan ist verhaltensmäßig ein
   Heap-Objekt: mutabel, nie evicted, hochrelevant. Eine dritte Region bräche I4
   (`Static → Working`) ohne neuen Nutzen.
2. **Nicht in Static** — obwohl die „Programm/Instructions"-Analogie aufs Code-Segment
   zeigt. Static ist pro Epoche immutable (I1); jede Änderung = `static_epoch`-Bump =
   Cache-Invalidierung (§4.2). Ein Plan, der sich pro Turn ändert, würde den Prompt-Cache
   bei jedem Edit zerstören. ⇒ Plan = Working.
3. **Der Mehrwert ist Struktur, nicht ein Behälter.** `kind=task` (pinned, ttl=∞)
   deckt „überlebt GC" schon ab. Neu und spec-würdig ist der **Instruction-Pointer**:
   geordnete Schritte + aktueller Schritt + Per-Schritt-Status. Das schaltet
   plan-aware Compaction (erledigte Schritte → 1 Zeile) und einen Render-Cursor frei.
4. **Plan ist Frame-lokal.** Frames sind bereits ein Call-Stack (§2.5). Ein Plan pro
   Frame = die lokale Instruktionssequenz dieses Stack-Frames; passt zur „Programm"-
   Analogie besser als ein Session-globaler Plan und dockt sauber an Frame-Pop +
   Promotion an.
5. **Plan bleibt ein Segment** (kein neues Top-Level-Aggregat), damit die Invariante
   „Render ausschließlich aus Segmenten" (§2.2) erhalten bleibt. Die Schritt-Struktur
   lebt in einem typisierten Feld des Segments, nicht in einer Parallel-Tabelle.

---

## 1. Diff gegen §2.2 (Segment) — neues Feld `plan`

Ergänzung der Segment-Struktur um ein optionales, nur für `kind=plan` belegtes Feld:

```diff
 Segment
 ├── id: ULID
 ├── …
 ├── pinned: bool
+├── plan: PlanBody | null        (nur bei kind=plan gesetzt; sonst null)
 ├── created_turn: int
 └── state: live | externalized | compacted | evicted
```

```
PlanBody
├── steps: PlanStep[]            (geordnet; Index = Programm-Adresse)
└── cursor: int                  (Index des aktuellen Schritts = "Program Counter")

PlanStep
├── text: string                 (die Instruktion)
├── status: pending | active | done | skipped | blocked
└── note: string | null          (Ergebnis/Begründung, gefüllt beim Abschluss)
```

Begründung für `cursor` als eigenes Feld (statt nur `status=active`): macht den PC
explizit adressierbar für Render und GC und erlaubt „Sprünge" (Replan) ohne
Status-Scan.

---

## 2. Diff gegen §2.3 (Segment-Kinds) — neuer Kind `plan`

```diff
 | kind | typische Region | Default-Verhalten |
 |---|---|---|
 | `task` | Working, `pinned` | nie evicten, Kandidat für Promotion am Session-Ende |
+| `plan` | Working, `pinned` | nie evicten/externalisieren; **in-place** geupdatet;
+|        |                 | plan-aware Compaction (siehe GC); Render-Cursor; Promotion-Kandidat |
 | `decision` | Working, `pinned` | nie evicten, Promotion-Kandidat |
```

- **Genau ein** `live` `plan`-Segment pro Frame (siehe Invariante I6). Updates
  mutieren `plan.steps`/`plan.cursor` in-place — kein Append pro Turn (sonst
  Chronologie-Müll). `tokens` wird beim Update neu gezählt.
- `refetchable=false`, nie Externalisierung (der Plan ist die Source of Truth).

---

## 3. Diff gegen §2.2 — neue Invarianten I6/I7

```diff
 - I5: Eine Unit ist nur dann render-eligible, wenn sie vollständig ist …
+- I6: Pro Frame existiert höchstens **ein** `plan`-Segment im Zustand `live`.
+      Ein zweites `POST` von `kind=plan` in denselben Frame ⇒ 409 Conflict
+      (Update läuft über den Plan-Endpunkt §4.x, nicht über Append).
+- I7: `0 ≤ plan.cursor < plan.steps.length`. Genau der Schritt an `cursor` hat
+      `status=active`; alle Schritte < cursor sind `done | skipped`. Verletzung
+      ⇒ 422. (Der PC zeigt immer auf eine gültige, noch nicht erledigte Instruktion;
+      Vorwärts-Only-Default, Replan über expliziten Reset.)
```

---

## 4. Diff gegen §3 (GC) — plan-aware Compaction + Eviction-Schutz

### 4.1 Minor Collection (§3.2)
Keine Änderung der Reihenfolge nötig — `plan` ist `pinned`, fällt also bereits aus
Clean-Page-Eviction (1) und TTL-Eviction (3) heraus und ist von Externalisierung (2)
per Kind ausgeschlossen. **Klarstellung** ergänzen:

```diff
 3. TTL-Eviction: Units, deren kind-TTL überschritten ist … und die weder pinned
    noch Static sind → state := evicted.
+   (`plan`-Segmente sind pinned und damit immer eviction-/externalisierungs-fest.)
```

### 4.2 Major Collection (§3.3) — neuer Schritt „Plan-Compaction"
Vor der allgemeinen Compaction läuft eine **deterministische, lossless
Plan-interne Verdichtung** (kein LLM nötig):

```diff
 1. Promotion (vor Compaction, zwingend): …
+1a. Plan-Compaction (deterministisch, lossless): In jedem live `plan`-Segment
+    werden Schritte mit status ∈ {done, skipped} zu einer Kurzform gerendert
+    (nur `text` + ✓/–, `note` wird gedroppt bzw. ist bereits via Promotion gesichert).
+    Schritte mit status ∈ {active, pending, blocked} bleiben **verbatim** inkl. `note`.
+    Ergebnis ersetzt `content`/`tokens` des Segments in-place; `plan.steps` bleibt als
+    strukturierte Wahrheit erhalten. Kein neues Segment, kein seq-Wechsel.
 2. Compaction: Das Compaction-Fenster — alle nicht gepinnten Working-Units …
```

`note`-Inhalte erledigter Schritte, die dauerhaft relevant sind (Entscheidungen,
Constraints), gehen vorher durch die reguläre Promotion (§3.3.1) — Plan-Compaction
darf sie also droppen.

---

## 5. Diff gegen §2.5 (Frame) — Plan-Lebenszyklus an Frame-Pop

```diff
 - pop: alle Segmente des Frames werden evicted; der Return-Content wird als
   subagent_return im Parent-Frame angelegt. Vor der Eviction läuft die
   Promotion-Policy über die Frame-Segmente …
+  Das `plan`-Segment des Frames ist Teil dieses Promotion-Durchlaufs: offene
+  (pending/blocked) Schritte beim Pop sind ein Signal für die Promotion-Policy
+  ("Subtask unvollständig abgeschlossen"). Nach Promotion wird auch das
+  plan-Segment evicted (kein Plan überlebt seinen Frame).
```

---

## 6. Diff gegen §4 (API) — Endpunkte

```diff
+  PUT    /v1/sessions/{id}/frames/{frame_id}/plan
+         body:   { steps: [{ text, status?, note? }], cursor }
+         Legt den Plan des Frames an oder ersetzt ihn vollständig (idempotent).
+         Erstanlage in einen Frame ohne Plan ⇒ 201; Ersetzen ⇒ 200.
+         frame_id weggelassen / "root" ⇒ Root-Frame.
+         Verletzt I7 ⇒ 422.
+
+  PATCH  /v1/sessions/{id}/frames/{frame_id}/plan
+         body:   { advance?: bool, set_status?: { index, status, note? },
+                   insert?: { index, text }, cursor? }
+         Inkrementelle Mutation (Schritt abschließen + PC weiterrücken, Schritt
+         einschieben, Replan). `advance:true` setzt aktuellen Schritt auf done und
+         cursor++ bis zum nächsten nicht-done Schritt.
```

Append (`POST …/segments` mit `kind=plan`) wird durch I6 auf "erste Anlage"
beschränkt — empfohlen ist der dedizierte Endpunkt, damit Mutation und Append
sauber getrennt bleiben.

---

## 7. Diff gegen §5 (Policy) — Plan-Konfiguration

```diff
 kinds:
   decision:           { ttl_turns: ∞,  promote: true      }
   task:               { ttl_turns: ∞ }
+  plan:               { ttl_turns: ∞,  promote: true, compact_done_steps: true }
 compaction:
   …
+plan:
+  render_placement: "tail"     # tail (Recency) | pinned (chronologisch, Default-Fallback)
+  max_steps: 50                # Schutz gegen Plan-Wildwuchs; Überlauf ⇒ 422
```

---

## 8. Diff gegen §4.x (Render) — Cursor-Sichtbarkeit

- Bei `render_placement: "tail"` wird das `plan`-Segment **zusätzlich** zu seiner
  pinned-Position als letztes Working-Segment vor dem Generierungs-Turn gerendert
  (frische Sicht auf den aktuellen Schritt). Achtung: bricht die strikte
  seq-Order-Heuristik aus I4 — daher als **opt-in Policy**, Default bleibt `pinned`
  (chronologisch an seq-Position, I4-konform).
- Render markiert den `cursor`-Schritt explizit (z. B. `▶`), erledigte als `✓`.

---

## 9. Diff gegen §6 (Events)

```diff
 frame_pushed, frame_popped { return_segment_id },
+plan_created { segment_id, step_count },
+plan_advanced { segment_id, from_cursor, to_cursor, completed_step },
+plan_revised  { segment_id, change },          # insert / status / reset
 ref_expanded { segment_id },
```

---

## 10. Offene Punkte / bewusst noch nicht entschieden

- **Rückwärts-Sprünge / Loops:** I7 erzwingt Vorwärts-Only. Echte Schleifen
  („Schritt 3–5 wiederholen bis X") würden ein reicheres Modell brauchen
  (Labels statt Index, Jump-Targets). Bewusst draußen gelassen — YAGNI bis es
  einen echten Use-Case gibt.
- **Sub-Pläne ↔ Frames:** Ob ein Frame seinen Plan vom Parent *erbt* (Slice) oder
  immer frisch anlegt. Vorschlag: immer frisch (klare Frame-Lokalität), Parent-Plan
  bleibt im Parent sichtbar bei `render` Default (Frame-Pfad).
- **Determinismus/Golden-Files:** Plan-Compaction (§4.2.1a) ist deterministisch und
  sollte einen Golden-File-Test bekommen (byte-genau, analog I4-Tests).
- **Nutzen-Check:** Lohnt der dedizierte Endpunkt (§6) gegenüber „nur ein pinned
  task-Segment, das der Agent selbst als Markdown-Checklist pflegt"? Der Gewinn
  steht und fällt mit plan-aware Compaction (§4.2.1a) und dem Cursor — ohne die
  beiden ist `task` ausreichend.
