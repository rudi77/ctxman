import { useEffect, useRef, useState } from "react";
import { api, subscribeSessionEvents } from "../api/client";
import type { SessionDetail } from "../api/types";
import { shortId } from "../lib/format";

interface DiscoverFn {
  (session: { id: string; label: string; addedAt: string }): boolean;
}

export interface DiscoveryResult {
  /** Aktuelle Summaries ALLER Tenant-Sessions (aus GET /v1/sessions) — für die Sidebar-Karten. */
  summaries: Record<string, SessionDetail>;
  /** true, solange der SSE-Stream verbunden ist (Live-Push aktiv). */
  live: boolean;
}

const POLL_FALLBACK_MS = 8000;

/**
 * Live-Discovery aller Sessions des aktuellen Tenants: initial + per Fallback-Poll über
 * GET /v1/sessions, zusätzlich Sofort-Reaktion auf den SSE-Stream (session_created/_archived).
 * Neu entdeckte, nicht entfernte Sessions werden über discover() in die bekannte Liste
 * übernommen; die Summaries gehen an die Sidebar (eine Liste statt N Einzel-Polls).
 */
export function useSessionDiscovery(discover: DiscoverFn, tenant: string): DiscoveryResult {
  const [summaries, setSummaries] = useState<Record<string, SessionDetail>>({});
  const [live, setLive] = useState(false);
  const discoverRef = useRef(discover);
  discoverRef.current = discover;

  useEffect(() => {
    let cancelled = false;

    const reconcile = async () => {
      try {
        const { sessions } = await api.listSessions();
        if (cancelled) return;
        const map: Record<string, SessionDetail> = {};
        for (const s of sessions) {
          map[s.session_id] = s;
          // Neue, nicht entfernte Sessions automatisch in die Liste aufnehmen.
          discoverRef.current({
            id: s.session_id,
            label: `Session ${shortId(s.session_id)}`,
            addedAt: new Date().toISOString(),
          });
        }
        setSummaries(map);
      } catch {
        // API kurz nicht erreichbar — der nächste Tick/SSE-Event versucht es erneut.
      }
    };

    void reconcile();
    const poll = setInterval(reconcile, POLL_FALLBACK_MS);

    // SSE: bei jedem Lifecycle-Event sofort reconcilen (statt auf den Poll zu warten).
    const unsubscribe = subscribeSessionEvents((ev) => {
      setLive(true);
      if (ev.type === "snapshot" || ev.type === "session_created" || ev.type === "session_archived") {
        void reconcile();
      }
    });

    return () => {
      cancelled = true;
      clearInterval(poll);
      unsubscribe();
      setLive(false);
    };
  }, [tenant]);

  return { summaries, live };
}
