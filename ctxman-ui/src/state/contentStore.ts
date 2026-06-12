// Lokaler Inhalts-Speicher: der Event-Stream trägt keine Segment-Inhalte
// (segment_appended = nur kind/region/seq/tokens). Alles, was DIESE UI selbst
// appended (Spielwiese, Simulator, Frame-Returns), wird hier beim Append
// mitgeschrieben — damit Memory-Map und Inspektor zeigen können, was in einem
// Segment wirklich drinsteht. Für fremd befüllte Sessions bleibt nur der
// Page-Fault-Pfad (externalisierte Segmente) bzw. die gerenderte Context-Ansicht.

export interface RecordedSegment {
  content: string;
  role?: string | null;
  toolCallId?: string | null;
  source?: string | null;
  pinned?: boolean;
}

const STORAGE_KEY = "ctxman-ui.content";
const MAX_CONTENT_CHARS = 2500;
const MAX_SEGMENTS_PER_SESSION = 600;
const MAX_SESSIONS = 20;

type Store = Record<string, Record<string, RecordedSegment>>;

let cache: Store | null = null;
let persistTimer: number | null = null;

function load(): Store {
  if (cache) return cache;
  try {
    cache = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}") as Store;
  } catch {
    cache = {};
  }
  return cache;
}

function schedulePersist(): void {
  if (persistTimer !== null) return;
  persistTimer = window.setTimeout(() => {
    persistTimer = null;
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(cache ?? {}));
    } catch {
      // Quota voll → ältere Sessions verwerfen und erneut versuchen.
      const store = load();
      const ids = Object.keys(store);
      for (const id of ids.slice(0, Math.max(1, ids.length - 5))) delete store[id];
      try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(store));
      } catch {
        /* aufgeben — Inhalte bleiben dann nur in-memory */
      }
    }
  }, 800);
}

export function recordSegments(
  sessionId: string,
  entries: ReadonlyArray<{ id: string } & RecordedSegment>,
): void {
  const store = load();
  let session = store[sessionId];
  if (!session) {
    // Session-Cap: älteste (Insertion-Order) Sessions verwerfen.
    const ids = Object.keys(store);
    if (ids.length >= MAX_SESSIONS) delete store[ids[0]];
    session = store[sessionId] = {};
  }
  for (const e of entries) {
    session[e.id] = {
      content: e.content.slice(0, MAX_CONTENT_CHARS),
      role: e.role,
      toolCallId: e.toolCallId,
      source: e.source,
      pinned: e.pinned,
    };
  }
  // Segment-Cap pro Session (älteste Einträge zuerst raus).
  const keys = Object.keys(session);
  if (keys.length > MAX_SEGMENTS_PER_SESSION) {
    for (const k of keys.slice(0, keys.length - MAX_SEGMENTS_PER_SESSION)) delete session[k];
  }
  schedulePersist();
}

export function getRecorded(sessionId: string, segmentId: string): RecordedSegment | undefined {
  return load()[sessionId]?.[segmentId];
}
