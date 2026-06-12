# ctxman-ui

React-Dashboard + Spielwiese für den ctxman Context-Management-Service.

## Start

```powershell
# 1. API starten (Postgres erforderlich, siehe Haupt-README)
dotnet run --project src/Ctxman.Api          # läuft auf http://localhost:5291

# 2. UI starten
cd ctxman-ui
npm install
npm run dev                                  # http://localhost:5173
```

Der Vite-Dev-Server proxied `/api/*` auf die ctxman-API (`http://localhost:5291`,
überschreibbar via `VITE_API_TARGET` in einer `.env.local`). Dadurch gibt es im
Dev-Betrieb kein CORS-Problem — die API selbst setzt keine CORS-Header.

## Was die UI kann

**Dashboard** (Echtzeit, Polling des Event-Streams mit `after_seq`-Cursor):

- **Memory-Map** — das Speicherbild der Spec §1 wörtlich: Static-Region (Stack) und
  Working Set (Heap), jedes Segment ein Block (Breite ∝ Tokens, Farbe = Kind),
  Schraffur = externalisiert, ausgegraut = evicted/compacted, 📌 = gepinnt.
- **Budget-Gauge** mit den drei Watermark-Zonen (soft/hard/emergency, Spec §3.1).
- **Token-Timeline** über die `render_served`-Events — der Sägezahn zeigt, wie der GC
  den Context wieder unter Budget drückt; Epoch-Bumps sind markiert.
- **Frame-Stack** (Spec §2.5), **Event-Feed** (Outbox live, filterbar) und
  **GC-Zähler** (Externalisierungen, Evictions, Compactions, Page Faults, …).
- **Segment-Inspektor**: Pin/Unpin und `expand_context_ref` (Page Fault) per Klick.

**Spielwiese**:

- Session anlegen (Policy-Overrides: Budget, Watermarks, Externalize-Threshold;
  Static-Region mit System-Prompt + Tool-Defs).
- Segmente anhängen (Quick-Actions inkl. großer Tool-Units, die die Externalisierung
  triggern), Render (anthropic/openai, path/frame, turn_advance), GC manuell
  (minor/major), Frames push/pop, Static-Epoch-Bump mit If-Match.
- **Turn-Simulator**: simuliert einen Agent-Loop (user_msg → render → Tool-Units →
  render → assistant_msg, gelegentlich gepinnte decisions) — auf dem Dashboard live
  zusehen, wie Watermarks reißen und der GC arbeitet.

## Architektur-Notizen

- Die API hat (spec-gemäß) **keinen** List-Sessions-/List-Segments-Endpunkt. Die UI
  führt bekannte Sessions in `localStorage` (Spielwiesen-erstellte + manuell per ID
  hinzugefügte) und **rekonstruiert das Working Set aus dem Event-Stream** (Spec §6):
  `segment_appended` liefert kind/region/seq/tokens, die GC-/Frame-Events die
  Lebenszyklus-Übergänge.
- Die Static-Region erzeugt keine Segment-Events — für Spielwiesen-Sessions kennt die
  UI die Segmente selbst, für fremde Sessions wird sie als Aggregat-Block angezeigt
  (Tokens = `tokens_used` − rekonstruiertes Working Set).
- `GET /sessions/{sid}` lieferte weder Budget noch Watermarks — für fremde Sessions
  nimmt die UI die Policy-Defaults (180k, 0.60/0.80/0.95) an.
- Tenant (Auth-Modus `none`): Header-Feld oben rechts setzt `X-Tenant-Id`.
- Pinned-Status ist in Events nicht enthalten — die UI merkt sich, was sie selbst
  gepinnt hat (Best-Effort-Anzeige).
