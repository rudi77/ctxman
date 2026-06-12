import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import type { SessionDetail } from "../api/types";
import { applyEvents, emptyLiveState, type LiveState } from "./eventReducer";

export interface LiveSession {
  detail: SessionDetail | null;
  live: LiveState;
  error: string | null;
  /** Manuell sofort nachladen (z. B. direkt nach einer Spielwiesen-Aktion). */
  refresh: () => void;
}

const POLL_MS = 1200;

/**
 * Echtzeit-Sicht auf eine Session: pollt den Event-Stream inkrementell über den
 * after_seq-Cursor (Spec §4.3) und hält parallel GET /sessions/{sid} aktuell.
 * Die SSE-Variante der API schließt nach dem Backlog — Polling ist hier der
 * tragfähige Live-Mechanismus.
 */
export function useLiveSession(sessionId: string | null): LiveSession {
  const [detail, setDetail] = useState<SessionDetail | null>(null);
  const [live, setLive] = useState<LiveState>(emptyLiveState);
  const [error, setError] = useState<string | null>(null);
  // Event-seq beginnt bei 0 und after_seq ist exklusiv — Start bei -1, sonst fehlt das erste Event.
  const cursorRef = useRef(-1);
  const busyRef = useRef(false);
  const tickRef = useRef<() => void>(() => {});

  useEffect(() => {
    cursorRef.current = -1;
    setDetail(null);
    setLive(emptyLiveState());
    setError(null);
    if (!sessionId) {
      tickRef.current = () => {};
      return;
    }

    let cancelled = false;

    const tick = async () => {
      if (busyRef.current || cancelled) return;
      busyRef.current = true;
      try {
        const [detailRes, eventsRes] = await Promise.all([
          api.getSession(sessionId),
          api.events(sessionId, cursorRef.current),
        ]);
        if (cancelled) return;
        setDetail(detailRes);
        if (eventsRes.events.length > 0) {
          // Cursor sofort fortschreiben (nicht erst im React-Updater) — sonst kann ein
          // überlappender Tick denselben Batch erneut holen. applyEvents ist zusätzlich
          // idempotent (filtert seq ≤ cursor), Doppel-Anwendung ist damit ausgeschlossen.
          cursorRef.current = Math.max(
            cursorRef.current,
            ...eventsRes.events.map((ev) => ev.seq),
          );
          setLive((prev) => applyEvents(prev, eventsRes.events));
        }
        setError(null);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      } finally {
        busyRef.current = false;
      }
    };

    tickRef.current = () => void tick();
    void tick();
    const handle = setInterval(tick, POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  }, [sessionId]);

  const refresh = useCallback(() => tickRef.current(), []);

  return { detail, live, error, refresh };
}
