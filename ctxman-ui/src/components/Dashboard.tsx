import { useMemo, useState } from "react";
import { api } from "../api/client";
import type { SessionDetail } from "../api/types";
import type { LiveState, SegmentView } from "../state/eventReducer";
import { DEFAULT_BUDGET, DEFAULT_WATERMARKS, type KnownSession } from "../state/sessionStore";
import { formatPercent, formatTokens } from "../lib/format";
import { BudgetGauge } from "./BudgetGauge";
import { ContextView } from "./ContextView";
import { MemoryMap } from "./MemoryMap";
import { TokenTimeline } from "./TokenTimeline";
import { EventFeed } from "./EventFeed";
import { FrameStack } from "./FrameStack";
import { SegmentInspector } from "./SegmentInspector";

interface Props {
  known: KnownSession;
  detail: SessionDetail | null;
  live: LiveState;
  pinnedIds: ReadonlySet<string>;
  onPinChange: (segmentId: string, pinned: boolean) => void;
  refresh: () => void;
}

export function Dashboard({ known, detail, live, pinnedIds, onPinChange, refresh }: Props) {
  const [selected, setSelected] = useState<SegmentView | null>(null);

  const budget = known.budgetTokens ?? DEFAULT_BUDGET;
  const watermarks = known.watermarks ?? DEFAULT_WATERMARKS;

  const segments = useMemo(() => Array.from(live.segments.values()), [live.segments]);
  const frames = useMemo(() => Array.from(live.frames.values()), [live.frames]);

  // Static-Tokens: tokens_used (autoritativ, GET /sessions) minus rekonstruierter Working-Anteil.
  const workingTokens = segments
    .filter((s) => s.region === "working" && (s.state === "live" || s.state === "externalized"))
    .reduce((sum, s) => sum + s.tokens, 0);
  const staticTokens = Math.max(0, (detail?.tokens_used ?? 0) - workingTokens);

  const selectedLive = selected ? live.segments.get(selected.id) ?? selected : null;

  if (!detail) {
    return <div className="empty-state"><p>Lade Session {known.id}…</p></div>;
  }

  const wm = detail.watermark_state;

  return (
    <div>
      <div className="stats">
        <div className="stat">
          <div className="stat-label">Tokens</div>
          <div className="stat-value">{formatTokens(detail.tokens_used)}</div>
          <div className="stat-sub">{formatPercent(detail.tokens_used / budget)} von {formatTokens(budget)}</div>
        </div>
        <div className="stat">
          <div className="stat-label">Watermark</div>
          <div className="stat-value"><span className={`badge ${wm}`}>{wm}</span></div>
          <div className="stat-sub">{live.lastWatermarkCrossed ? `zuletzt überschritten: ${live.lastWatermarkCrossed}` : "—"}</div>
        </div>
        <div className="stat">
          <div className="stat-label">Turn</div>
          <div className="stat-value">{detail.current_turn}</div>
          <div className="stat-sub">{live.counters.renders} Renders</div>
        </div>
        <div className="stat">
          <div className="stat-label">Context-Version</div>
          <div className="stat-value">{detail.context_version}</div>
          <div className="stat-sub">optimistic concurrency</div>
        </div>
        <div className="stat">
          <div className="stat-label">Static-Epoche</div>
          <div className="stat-value">{detail.static_epoch}</div>
          <div className="stat-sub">{live.counters.epochBumps} Bumps</div>
        </div>
        <div className="stat">
          <div className="stat-label">Status</div>
          <div className="stat-value" style={{ fontSize: 15 }}>
            <span className={`badge ${detail.status === "active" ? "ok" : "archived"}`}>{detail.status}</span>
          </div>
          <div className="stat-sub">
            {detail.status === "active" && (
              <button
                className="small danger"
                onClick={() => {
                  void api.archiveSession(known.id).then(refresh).catch(() => {});
                }}
              >
                archivieren
              </button>
            )}
          </div>
        </div>
      </div>

      <div className="panel">
        <h3>
          Budget &amp; Watermarks <span className="hint">— Spec §3.1: soft → Minor GC, hard → Major GC, emergency → synchrone Not-Eviction</span>
        </h3>
        <BudgetGauge tokensUsed={detail.tokens_used} budget={budget} watermarks={watermarks} />
      </div>

      <div className="panel">
        <h3>
          Memory-Map <span className="hint">— Static (Stack) / Working Set (Heap), Blockbreite ∝ Tokens, Klick für Details</span>
        </h3>
        <MemoryMap
          sessionId={known.id}
          segments={segments}
          frames={frames}
          staticEpoch={detail.static_epoch}
          knownStatic={known.staticSegments}
          staticTokensEstimate={staticTokens}
          selectedId={selectedLive?.id ?? null}
          onSelect={setSelected}
          pinnedIds={pinnedIds}
        />
      </div>

      <div className="panel">
        <h3>
          Live-Context <span className="hint">— das echte Render-Ergebnis: genau das sieht das Modell (Ground Truth vom Service)</span>
        </h3>
        <ContextView sessionId={known.id} />
      </div>

      <div className="grid-2" style={{ marginBottom: 14 }}>
        <div className="panel">
          <h3>Token-Timeline <span className="hint">— tokens_total je render_served, Sägezahn = GC wirkt</span></h3>
          <TokenTimeline points={live.timeline} budget={budget} watermarks={watermarks} />
          {live.timeline.length > 0 && (
            <div className="muted" style={{ marginTop: 8, fontFamily: "var(--mono)" }}>
              cache_prefix_hash: {live.timeline[live.timeline.length - 1].cachePrefixHash.slice(0, 16)}…
            </div>
          )}
        </div>
        <div className="col">
          <div className="panel" style={{ marginBottom: 0 }}>
            <h3>Frame-Stack <span className="hint">— Subagent-Frames (Spec §2.5), LIFO</span></h3>
            <FrameStack frames={frames} />
          </div>
          <div className="panel" style={{ marginBottom: 0 }}>
            <h3>GC-Aktivität</h3>
            <div className="counter-chips">
              <span className="chip">externalisiert <b>{live.counters.externalized}</b></span>
              <span className="chip">Segmente evicted <b>{live.counters.evictedSegments}</b></span>
              <span className="chip">Units evicted <b>{live.counters.evictedUnits}</b></span>
              <span className="chip">Compactions <b>{live.counters.compactions}</b></span>
              <span className="chip">Page Faults <b>{live.counters.pageFaults}</b></span>
              <span className="chip">Promotions <b>{live.counters.promotions}</b></span>
              <span className="chip">Blobs swept <b>{live.counters.blobsSwept}</b></span>
            </div>
          </div>
        </div>
      </div>

      <div className="grid-2">
        <div className="panel">
          <h3>Event-Stream <span className="hint">— Outbox live (Spec §6), Klick öffnet Payload</span></h3>
          <EventFeed events={live.events} />
        </div>
        <div className="panel">
          <h3>Segment-Inspektor</h3>
          <SegmentInspector
            sessionId={known.id}
            segment={selectedLive}
            pinnedIds={pinnedIds}
            onPinChange={onPinChange}
            onAction={refresh}
          />
        </div>
      </div>
    </div>
  );
}
