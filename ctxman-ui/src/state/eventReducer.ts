import type { EventItem, SegmentLifecycle, SegmentRegion } from "../api/types";

// Die API hat (spec-gemäß) keinen List-Segments-Endpunkt. Das Dashboard rekonstruiert die
// Working-Set-Sicht deshalb aus dem Event-Outbox-Stream (Spec §6): segment_appended trägt
// kind/region/seq/tokens, die GC-/Frame-Events liefern die Lebenszyklus-Übergänge.
// Die Static-Region erzeugt KEINE segment_appended-Events (initial via POST /sessions,
// danach nur Epoch-Bumps) — sie wird als Aggregat dargestellt, es sei denn, die Spielwiese
// kennt die Segmente, weil sie sie selbst gesetzt hat.

export interface SegmentView {
  id: string;
  kind: string;
  region: SegmentRegion;
  seq: number;
  tokens: number;
  state: SegmentLifecycle;
  compacting?: boolean;
  expansions: number; // ref_expanded-Zähler (Page Faults auf dieses Segment)
  lastEventSeq: number;
  synthetic?: boolean; // aus compaction_completed / frame_popped abgeleitet, nicht aus append
  /**
   * Frame-Zugehörigkeit, rekonstruiert aus der Event-Reihenfolge: die API hängt
   * jedes Working-Segment an den obersten offenen Frame (Spec §2.5), und der
   * Event-Stream ist strikt geordnet — ein segment_appended zwischen frame_pushed
   * und frame_popped gehört zu diesem Frame. null = Root.
   */
  frameId: string | null;
}

export interface FrameView {
  id: string;
  parentId: string | null;
  label: string;
  openedTurn: number;
  status: "open" | "popped";
  returnSegmentId?: string;
}

export interface TimelinePoint {
  eventSeq: number;
  tokensTotal: number;
  staticEpoch: number;
  cachePrefixHash: string;
  at: string;
}

export interface LiveCounters {
  renders: number;
  externalized: number;
  evictedSegments: number;
  evictedUnits: number;
  compactions: number;
  pageFaults: number;
  promotions: number;
  epochBumps: number;
  blobsSwept: number;
}

export interface LiveState {
  cursor: number;
  segments: Map<string, SegmentView>;
  frames: Map<string, FrameView>;
  /** Stack der aktuell offenen Frame-IDs (Tip = letztes Element), für Frame-Attribution. */
  openFrameStack: string[];
  events: EventItem[];
  timeline: TimelinePoint[];
  staticEpoch: number;
  lastWatermarkCrossed: string | null;
  counters: LiveCounters;
}

export function emptyLiveState(): LiveState {
  return {
    cursor: -1, // Event-seq beginnt bei 0; after_seq ist exklusiv.
    segments: new Map(),
    frames: new Map(),
    openFrameStack: [],
    events: [],
    timeline: [],
    staticEpoch: 0,
    lastWatermarkCrossed: null,
    counters: {
      renders: 0,
      externalized: 0,
      evictedSegments: 0,
      evictedUnits: 0,
      compactions: 0,
      pageFaults: 0,
      promotions: 0,
      epochBumps: 0,
      blobsSwept: 0,
    },
  };
}

const MAX_FEED_EVENTS = 500;
const MAX_TIMELINE_POINTS = 600;

function str(payload: Record<string, unknown>, key: string): string {
  const v = payload[key];
  return typeof v === "string" ? v : String(v ?? "");
}

function num(payload: Record<string, unknown>, key: string): number {
  const v = payload[key];
  return typeof v === "number" ? v : Number(v ?? 0);
}

function setState(state: LiveState, segmentId: string, next: SegmentLifecycle, eventSeq: number): void {
  const seg = state.segments.get(segmentId);
  if (seg) {
    seg.state = next;
    seg.compacting = false;
    seg.lastEventSeq = eventSeq;
  }
}

/** Wendet ein Event mutierend auf den (frisch geklonten) Zustand an. */
function applyEvent(state: LiveState, ev: EventItem): void {
  const p = ev.payload ?? {};
  switch (ev.type) {
    case "segment_appended": {
      const id = str(p, "segment_id");
      state.segments.set(id, {
        id,
        kind: str(p, "kind"),
        region: (str(p, "region") as SegmentRegion) || "working",
        seq: num(p, "seq"),
        tokens: num(p, "tokens"),
        state: "live",
        expansions: 0,
        lastEventSeq: ev.seq,
        frameId: state.openFrameStack[state.openFrameStack.length - 1] ?? null,
      });
      break;
    }
    case "segment_externalized": {
      setState(state, str(p, "segment_id"), "externalized", ev.seq);
      state.counters.externalized++;
      break;
    }
    case "segment_evicted": {
      setState(state, str(p, "segment_id"), "evicted", ev.seq);
      state.counters.evictedSegments++;
      break;
    }
    case "unit_evicted": {
      const ids = (p["segment_ids"] as unknown[]) ?? [];
      for (const id of ids) setState(state, String(id), "evicted", ev.seq);
      state.counters.evictedUnits++;
      break;
    }
    case "compaction_started": {
      const ids = (p["source_ids"] as unknown[]) ?? [];
      for (const id of ids) {
        const seg = state.segments.get(String(id));
        if (seg) seg.compacting = true;
      }
      break;
    }
    case "compaction_completed": {
      const ids = ((p["source_ids"] as unknown[]) ?? []).map(String);
      let minSeq = Number.MAX_SAFE_INTEGER;
      for (const id of ids) {
        const seg = state.segments.get(id);
        if (seg && seg.seq < minSeq) minSeq = seg.seq;
        setState(state, id, "compacted", ev.seq);
      }
      const summaryId = str(p, "summary_id");
      if (summaryId) {
        // Das Summary-Segment übernimmt die seq-Position des ältesten Quell-Segments (Spec §3.3).
        state.segments.set(summaryId, {
          id: summaryId,
          kind: "compaction_summary",
          region: "working",
          seq: minSeq === Number.MAX_SAFE_INTEGER ? num(p, "tokens_after") : minSeq,
          tokens: num(p, "tokens_after"),
          state: "live",
          expansions: 0,
          lastEventSeq: ev.seq,
          synthetic: true,
          frameId: null,
        });
      }
      state.counters.compactions++;
      break;
    }
    case "frame_pushed": {
      const id = str(p, "frame_id");
      state.frames.set(id, {
        id,
        parentId: p["parent_frame_id"] ? str(p, "parent_frame_id") : null,
        label: str(p, "label"),
        openedTurn: num(p, "opened_turn"),
        status: "open",
      });
      state.openFrameStack.push(id);
      break;
    }
    case "frame_popped": {
      const frameId = str(p, "frame_id");
      const frame = state.frames.get(frameId);
      const returnId = str(p, "return_segment_id");
      if (frame) {
        frame.status = "popped";
        frame.returnSegmentId = returnId;
      }
      state.openFrameStack = state.openFrameStack.filter((id) => id !== frameId);
      // Spec §2.5: beim Pop werden alle Segmente des Frames evicted — der Pop emittiert
      // dafür keine eigenen segment_evicted-Events, also hier nachziehen.
      for (const seg of state.segments.values()) {
        if (seg.frameId === frameId && (seg.state === "live" || seg.state === "externalized")) {
          seg.state = "evicted";
          seg.lastEventSeq = ev.seq;
        }
      }
      if (returnId && !state.segments.has(returnId)) {
        // Das Return-Segment erzeugt kein eigenes segment_appended-Event — Platzhalter
        // (Token-Zahl unbekannt, wird beim nächsten GET /sessions im Total korrigiert).
        const maxSeq = Math.max(0, ...Array.from(state.segments.values(), (s) => s.seq));
        state.segments.set(returnId, {
          id: returnId,
          kind: "subagent_return",
          region: "working",
          seq: maxSeq + 1,
          tokens: 0,
          state: "live",
          expansions: 0,
          lastEventSeq: ev.seq,
          synthetic: true,
          // Return-Segment landet im Parent-Frame = neuer Stack-Tip nach dem Pop.
          frameId: state.openFrameStack[state.openFrameStack.length - 1] ?? null,
        });
      }
      break;
    }
    case "ref_expanded": {
      const seg = state.segments.get(str(p, "segment_id"));
      if (seg) {
        seg.expansions++;
        seg.lastEventSeq = ev.seq;
      }
      state.counters.pageFaults++;
      break;
    }
    case "static_epoch_bumped": {
      state.staticEpoch = num(p, "new_epoch");
      state.counters.epochBumps++;
      break;
    }
    case "render_served": {
      state.timeline.push({
        eventSeq: ev.seq,
        tokensTotal: num(p, "tokens_total"),
        staticEpoch: num(p, "static_epoch"),
        cachePrefixHash: str(p, "cache_prefix_hash"),
        at: ev.created_at,
      });
      if (state.timeline.length > MAX_TIMELINE_POINTS) {
        state.timeline.splice(0, state.timeline.length - MAX_TIMELINE_POINTS);
      }
      state.counters.renders++;
      break;
    }
    case "watermark_crossed": {
      state.lastWatermarkCrossed = str(p, "level");
      break;
    }
    case "fact_promoted": {
      state.counters.promotions++;
      break;
    }
    case "blob_swept": {
      state.counters.blobsSwept++;
      break;
    }
    default:
      break;
  }
}

/**
 * Erzeugt einen neuen Zustand mit den Events angewandt (flacher Klon, Maps neu).
 * Idempotent: bereits verarbeitete Events (seq ≤ cursor) werden übersprungen —
 * überlappende Poll-Ticks können denselben Batch sonst doppelt anwenden.
 */
export function applyEvents(prev: LiveState, incoming: EventItem[]): LiveState {
  const events = incoming.filter((ev) => ev.seq > prev.cursor);
  if (events.length === 0) return prev;
  const next: LiveState = {
    ...prev,
    segments: new Map(prev.segments),
    frames: new Map(prev.frames),
    openFrameStack: [...prev.openFrameStack],
    events: [...prev.events],
    timeline: [...prev.timeline],
    counters: { ...prev.counters },
  };
  // Maps geklont, aber Segment-Objekte geteilt — vor Mutation kopieren.
  for (const [id, seg] of next.segments) next.segments.set(id, { ...seg });
  for (const [id, fr] of next.frames) next.frames.set(id, { ...fr });

  for (const ev of events) {
    applyEvent(next, ev);
    if (ev.seq > next.cursor) next.cursor = ev.seq;
    next.events.push(ev);
  }
  if (next.events.length > MAX_FEED_EVENTS) {
    next.events.splice(0, next.events.length - MAX_FEED_EVENTS);
  }
  return next;
}
