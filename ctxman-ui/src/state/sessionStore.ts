import { useCallback, useEffect, useState } from "react";
import type { StaticSegmentInput } from "../api/types";

// Die UI führt die ihr bekannten Sessions in localStorage: alles, was über die Spielwiese/
// Simulation erstellt, per ID hinzugefügt ODER über GET /v1/sessions bzw. den SSE-Stream
// (GET /v1/sessions/events) live entdeckt wurde. Für UI-erstellte Sessions merken wir uns
// zusätzlich Budget/Watermarks und die Static-Segmente (die Static-Region erzeugt keine Events).

export interface KnownSession {
  id: string;
  label: string;
  addedAt: string;
  budgetTokens?: number;
  watermarks?: { soft: number; hard: number; emergency: number };
  staticSegments?: StaticSegmentInput[];
  /** Über Discovery (Liste/SSE) automatisch hinzugefügt — für das „neu entdeckt"-Badge. */
  discovered?: boolean;
}

const STORAGE_KEY = "ctxman-ui.sessions";
const DISMISSED_KEY = "ctxman-ui.dismissed";

function load(): KnownSession[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as KnownSession[]) : [];
  } catch {
    return [];
  }
}

function persist(sessions: KnownSession[]): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(sessions));
}

function loadDismissed(): Set<string> {
  try {
    const raw = localStorage.getItem(DISMISSED_KEY);
    return new Set(raw ? (JSON.parse(raw) as string[]) : []);
  } catch {
    return new Set();
  }
}

function persistDismissed(ids: Set<string>): void {
  localStorage.setItem(DISMISSED_KEY, JSON.stringify([...ids]));
}

export function useKnownSessions() {
  const [sessions, setSessions] = useState<KnownSession[]>(load);
  // Vom Nutzer aus der Liste entfernte IDs — die Discovery fügt sie NICHT wieder hinzu.
  const [dismissed, setDismissed] = useState<Set<string>>(loadDismissed);

  useEffect(() => persist(sessions), [sessions]);
  useEffect(() => persistDismissed(dismissed), [dismissed]);

  const add = useCallback((session: KnownSession) => {
    setSessions((prev) => {
      if (prev.some((s) => s.id === session.id)) return prev;
      return [session, ...prev];
    });
    // Explizit hinzugefügt ⇒ aus der Dismissed-Liste nehmen (Re-Discovery wieder erlauben).
    setDismissed((prev) => {
      if (!prev.has(session.id)) return prev;
      const next = new Set(prev);
      next.delete(session.id);
      return next;
    });
  }, []);

  /** Discovery-Pfad: nur hinzufügen, wenn nicht bekannt UND nicht vom Nutzer entfernt. */
  const discover = useCallback((session: KnownSession): boolean => {
    let added = false;
    setSessions((prev) => {
      if (prev.some((s) => s.id === session.id)) return prev;
      if (loadDismissedSync().has(session.id)) return prev;
      added = true;
      return [{ ...session, discovered: true }, ...prev];
    });
    return added;
  }, []);

  const remove = useCallback((id: string) => {
    setSessions((prev) => prev.filter((s) => s.id !== id));
    setDismissed((prev) => {
      const next = new Set(prev);
      next.add(id);
      return next;
    });
  }, []);

  const update = useCallback((id: string, patch: Partial<KnownSession>) => {
    setSessions((prev) => prev.map((s) => (s.id === id ? { ...s, ...patch } : s)));
  }, []);

  return { sessions, dismissed, add, discover, remove, update };
}

// Synchroner Lesezugriff für discover() — vermeidet eine veraltete dismissed-Closure.
function loadDismissedSync(): Set<string> {
  return loadDismissed();
}

export const DEFAULT_BUDGET = 180_000;
export const DEFAULT_WATERMARKS = { soft: 0.6, hard: 0.8, emergency: 0.95 };
